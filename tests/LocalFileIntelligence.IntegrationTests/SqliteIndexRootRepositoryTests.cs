using System.Globalization;
using LocalFileIntelligence.Application.Persistence;
using LocalFileIntelligence.Storage;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.IntegrationTests;

public sealed class SqliteIndexRootRepositoryTests
{
    [Fact]
    public async Task StartsEmptyAndAddIsIdempotentForUnicodeNormalizedPath()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);
        Assert.Empty(await repository.ListAsync());

        DateTimeOffset createdAt = DateTimeOffset.Parse(
            "2026-01-01T03:04:05.678+03:00",
            CultureInfo.InvariantCulture);
        IndexRoot first = await repository.AddAsync(new(
            @"C:\Synthetic\Résumé\",
            "Synthetic root",
            "volume-1",
            "directory-1",
            createdAt));
        IndexRoot second = await repository.AddAsync(new(
            @"c:\sYNTHETIC\rÉSUMÉ",
            "Ignored on idempotent insert",
            "volume-1",
            "directory-1",
            createdAt.AddDays(1)));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(@"C:\Synthetic\Résumé", first.Path);
        Assert.Equal(@"C:\SYNTHETIC\RÉSUMÉ", first.PathKey);
        Assert.Equal("Synthetic root", second.DisplayName);
        Assert.False(first.Enabled);
        Assert.Equal(IndexRootStatus.Pending, first.Status);
        Assert.Equal(1, first.PolicyRevision);
        Assert.Equal(0, first.IndexRevision);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:04:05.678Z", CultureInfo.InvariantCulture), first.CreatedAt);
        Assert.Equal(first.CreatedAt, first.UpdatedAt);
        Assert.Single(await repository.ListAsync());

        IndexRoot[] concurrentAdds = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => repository.AddAsync(new(
                @"C:\SYNTHETIC\RÉSUMÉ",
                "Another spelling",
                "volume-1",
                "directory-1",
                createdAt.AddDays(2)))));
        Assert.All(concurrentAdds, root => Assert.Equal(first.Id, root.Id));
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task RejectsNormalizedPathCollisionWithDifferentIdentity()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);
        await repository.AddAsync(CreateRegistration());

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(
            CreateRegistration(path: @"c:\SYNTHETIC", directoryIdentity: "different-directory")));

        Assert.Single(await repository.ListAsync());
    }

    [Theory]
    [InlineData(true, IndexRootStatus.Offline)]
    [InlineData(false, IndexRootStatus.Paused)]
    public async Task FailedStateInvalidationRollsBackAllChanges(bool enabled, IndexRootStatus status)
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);
        IndexRoot added = await repository.AddAsync(CreateRegistration());
        IndexRoot root = Assert.IsType<IndexRoot>(
            await repository.UpdateStateAsync(added.Id, enabled: true, IndexRootStatus.Ready, added.CreatedAt));
        string leaseUntil = UtcTimestamp.Format(root.CreatedAt.AddMinutes(1));

        await using (SqliteConnectionLease lease = await factory.OpenWriterAsync())
        {
            await ExecuteAsync(
                lease.Connection,
                """
                INSERT INTO file_entries (root_id, relative_path, path_key, parent_path_key, name, extension,
                                          entry_type, size_bytes, created_at_fs, modified_at_fs, last_seen_at,
                                          extraction_status, indexing_status)
                VALUES ($root_id, 'note.txt', 'NOTE.TXT', '', 'note.txt', '.txt', 'file', 4,
                        $timestamp, $timestamp, $timestamp, 'extracted', 'indexed');
                INSERT INTO file_contents
                VALUES (last_insert_rowid(), 1, 'data', 'plain-text', '1', $timestamp, 4);
                INSERT INTO index_jobs (root_id, job_type, dedupe_key, status, created_at, next_attempt_at,
                                        expected_policy_revision, started_at, lease_owner, lease_until)
                VALUES ($root_id, 'scan', 'queued-scan', 'queued', $timestamp, $timestamp,
                        $revision, NULL, NULL, NULL),
                       ($root_id, 'scan', 'running-scan', 'running', $timestamp, $timestamp,
                        $revision, $timestamp, 'worker-1', $lease_until);
                CREATE TRIGGER fail_job_cancellation
                BEFORE UPDATE OF status ON index_jobs
                WHEN NEW.status = 'cancelled'
                BEGIN
                    SELECT RAISE(ABORT, 'synthetic invalidation failure');
                END;
                """,
                ("$root_id", root.Id),
                ("$timestamp", UtcTimestamp.Format(root.CreatedAt)),
                ("$revision", root.PolicyRevision),
                ("$lease_until", leaseUntil));
        }

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(() => repository.UpdateStateAsync(
            root.Id, enabled, status, root.CreatedAt.AddMinutes(1)));

        Assert.Contains("synthetic invalidation failure", exception.Message);
        Assert.Equal(root, await repository.GetByIdAsync(root.Id));
        Assert.Equal("data", await ScalarTextAsync(factory, "SELECT extracted_text FROM file_contents;"));
        Assert.Equal("extracted", await ScalarTextAsync(factory, "SELECT extraction_status FROM file_entries;"));
        Assert.Equal(1, await ScalarAsync(factory,
            "SELECT count(*) FROM index_jobs WHERE status = 'queued' AND completed_at IS NULL AND lease_owner IS NULL AND lease_until IS NULL;"));
        Assert.Equal(1, await ScalarAsync(factory,
            "SELECT count(*) FROM index_jobs WHERE status = 'running' AND completed_at IS NULL AND lease_owner = 'worker-1';"));
        Assert.Equal(leaseUntil, await ScalarTextAsync(factory,
            "SELECT lease_until FROM index_jobs WHERE status = 'running';"));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("details")]
    [InlineData("state")]
    [InlineData("disable")]
    [InlineData("remove")]
    public async Task MutationWaitingForWriterLeaseHonorsCancellation(string operation)
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);
        IndexRoot added = await repository.AddAsync(CreateRegistration());
        IndexRoot root = Assert.IsType<IndexRoot>(
            await repository.UpdateStateAsync(added.Id, enabled: true, IndexRootStatus.Ready, added.CreatedAt));
        DateTimeOffset changedAt = root.CreatedAt.AddMinutes(1);

        await using (SqliteConnectionLease writer = await factory.OpenWriterAsync())
        {
            using CancellationTokenSource cancellation = new();
            Task mutation = operation switch
            {
                "add" => repository.AddAsync(CreateRegistration(path: @"C:\OtherSynthetic"), cancellation.Token),
                "details" => repository.UpdateDetailsAsync(root.Id, "Renamed root", changedAt, cancellation.Token),
                "state" => repository.UpdateStateAsync(
                    root.Id, enabled: true, IndexRootStatus.Offline, changedAt, cancellation.Token),
                "disable" => repository.UpdateStateAsync(
                    root.Id, enabled: false, IndexRootStatus.Paused, changedAt, cancellation.Token),
                "remove" => repository.RemoveAsync(root.Id, cancellation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };

            Assert.False(mutation.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => mutation.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(root, await repository.GetByIdAsync(root.Id));
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task DetailsAndStateUpdatesAreTransactionalAndIdempotent()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);
        IndexRoot added = await repository.AddAsync(CreateRegistration());
        DateTimeOffset changedAt = DateTimeOffset.Parse("2026-01-02T03:04:05.678+03:00", CultureInfo.InvariantCulture);

        IndexRoot renamed = Assert.IsType<IndexRoot>(
            await repository.UpdateDetailsAsync(added.Id, "Renamed root", changedAt));
        IndexRoot repeatedRename = Assert.IsType<IndexRoot>(
            await repository.UpdateDetailsAsync(added.Id, "Renamed root", changedAt.AddHours(1)));
        Assert.Equal("Renamed root", renamed.DisplayName);
        Assert.Equal(1, renamed.IndexRevision);
        Assert.Equal(1, repeatedRename.IndexRevision);
        Assert.Equal(renamed.UpdatedAt, repeatedRename.UpdatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T00:04:05.678Z", CultureInfo.InvariantCulture), renamed.UpdatedAt);

        IndexRoot enabled = Assert.IsType<IndexRoot>(
            await repository.UpdateStateAsync(added.Id, enabled: true, IndexRootStatus.Ready, changedAt.AddHours(2)));
        IndexRoot repeatedState = Assert.IsType<IndexRoot>(
            await repository.UpdateStateAsync(added.Id, enabled: true, IndexRootStatus.Ready, changedAt.AddHours(3)));
        Assert.True(enabled.Enabled);
        Assert.Equal(IndexRootStatus.Ready, enabled.Status);
        Assert.Equal(2, enabled.PolicyRevision);
        Assert.Equal(2, enabled.IndexRevision);
        Assert.Equal(enabled.UpdatedAt, repeatedState.UpdatedAt);

        IndexRoot scanning = Assert.IsType<IndexRoot>(
            await repository.UpdateStateAsync(added.Id, enabled: true, IndexRootStatus.Scanning, changedAt.AddHours(3)));
        IndexRoot readyAgain = Assert.IsType<IndexRoot>(
            await repository.UpdateStateAsync(added.Id, enabled: true, IndexRootStatus.Ready, changedAt.AddHours(3)));
        Assert.Equal(2, scanning.PolicyRevision);
        Assert.Equal(3, scanning.IndexRevision);
        Assert.Equal(2, readyAgain.PolicyRevision);
        Assert.Equal(4, readyAgain.IndexRevision);

        await using (SqliteConnectionLease lease = await factory.OpenWriterAsync())
        {
            await ExecuteAsync(
                lease.Connection,
                """
                INSERT INTO file_entries (root_id, relative_path, path_key, parent_path_key, name, extension,
                                          entry_type, size_bytes, created_at_fs, modified_at_fs, last_seen_at,
                                          extraction_status, indexing_status)
                VALUES ($root_id, 'note.txt', 'NOTE.TXT', '', 'note.txt', '.txt', 'file', 4,
                        '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                        'extracted', 'indexed');
                INSERT INTO file_contents
                VALUES (last_insert_rowid(), 1, 'data', 'plain-text', '1', '2026-01-01T00:00:00.000Z', 4);
                """,
                ("$root_id", added.Id));
        }

        IndexRoot disabled = Assert.IsType<IndexRoot>(
            await repository.UpdateStateAsync(added.Id, enabled: false, IndexRootStatus.Paused, changedAt.AddHours(4)));
        Assert.False(disabled.Enabled);
        Assert.Equal(IndexRootStatus.Paused, disabled.Status);
        Assert.Equal(3, disabled.PolicyRevision);
        Assert.Equal(5, disabled.IndexRevision);
        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM file_contents;"));
        Assert.Null(await repository.UpdateDetailsAsync(999, "Missing", changedAt));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.UpdateStateAsync(
            added.Id,
            enabled: false,
            (IndexRootStatus)999,
            changedAt.AddHours(5)));
        Assert.Equal(disabled, await repository.GetByIdAsync(added.Id));
    }

    [Fact]
    public async Task RemoveIsIdempotentAndDoesNotReuseRootIds()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);
        IndexRoot original = await repository.AddAsync(CreateRegistration());

        Assert.True(await repository.RemoveAsync(original.Id));
        Assert.False(await repository.RemoveAsync(original.Id));
        Assert.Null(await repository.GetByIdAsync(original.Id));

        IndexRoot replacement = await repository.AddAsync(CreateRegistration());
        Assert.True(replacement.Id > original.Id);
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task MarkingRootOfflineInvalidatesContentAndCancelsQueuedWork()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);
        IndexRoot root = await repository.AddAsync(CreateRegistration());
        await repository.UpdateStateAsync(root.Id, enabled: true, IndexRootStatus.Ready, root.CreatedAt);

        await using (SqliteConnectionLease lease = await factory.OpenWriterAsync())
        {
            await ExecuteAsync(
                lease.Connection,
                """
                INSERT INTO file_entries (root_id, relative_path, path_key, parent_path_key, name, extension,
                                          entry_type, size_bytes, created_at_fs, modified_at_fs, last_seen_at,
                                          extraction_status, indexing_status)
                VALUES ($root_id, 'note.txt', 'NOTE.TXT', '', 'note.txt', '.txt', 'file', 4,
                        '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                        'extracted', 'indexed');
                INSERT INTO file_contents
                VALUES (1, 1, 'data', 'plain-text', '1', '2026-01-01T00:00:00.000Z', 4);
                INSERT INTO index_jobs (file_id, root_id, job_type, dedupe_key, status, created_at,
                                        next_attempt_at, expected_file_revision, expected_policy_revision)
                VALUES (1, $root_id, 'extract', 'extract-1', 'queued', '2026-01-01T00:00:00.000Z',
                        '2026-01-01T00:00:00.000Z', 1, 2);
                INSERT INTO index_jobs (root_id, job_type, dedupe_key, status, created_at, started_at,
                                        next_attempt_at, expected_policy_revision, lease_owner, lease_until)
                VALUES ($root_id, 'scan', 'scan-1', 'running', '2026-01-01T00:00:00.000Z',
                        '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 2, 'worker-1',
                        '2026-01-01T00:01:00.000Z');
                """,
                ("$root_id", root.Id));
        }

        IndexRoot offline = Assert.IsType<IndexRoot>(
            await repository.UpdateStateAsync(root.Id, enabled: true, IndexRootStatus.Offline, root.CreatedAt.AddMinutes(1)));

        Assert.Equal(IndexRootStatus.Offline, offline.Status);
        Assert.Equal(2, offline.PolicyRevision);
        Assert.Equal(2, offline.IndexRevision);
        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM file_contents;"));
        Assert.Equal("pending", await ScalarTextAsync(factory,
            "SELECT extraction_status FROM file_entries WHERE id = 1;"));
        Assert.Equal("cancelled", await ScalarTextAsync(factory,
            "SELECT status FROM index_jobs WHERE root_id = 1;"));
        Assert.Equal(2, await ScalarAsync(factory,
            "SELECT count(*) FROM index_jobs WHERE status = 'cancelled' AND completed_at IS NOT NULL;"));
        Assert.Equal(0, await ScalarAsync(factory,
            "SELECT count(*) FROM index_jobs WHERE lease_owner IS NOT NULL OR lease_until IS NOT NULL;"));
    }

    [Fact]
    public async Task RejectsMalformedPathTimestampAndStoredCalendarDate()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);

        await Assert.ThrowsAsync<FormatException>(() => repository.AddAsync(CreateRegistration(path: @"C:\..")));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(CreateRegistration(displayName: " ")));
        await Assert.ThrowsAsync<FormatException>(
            () => repository.AddAsync(CreateRegistration(path: @"C:\Synthetic\" + '\uD800')));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.AddAsync(CreateRegistration(displayName: "Invalid " + '\uD800')));
        Assert.Throws<FormatException>(() => UtcTimestamp.Parse("2026-02-30T00:00:00.000Z"));
        Assert.Throws<FormatException>(() => UtcTimestamp.Parse("2026-01-01T03:00:00+03:00"));

        IndexRoot root = await repository.AddAsync(CreateRegistration());
        await using (SqliteConnectionLease lease = await factory.OpenWriterAsync())
        {
            await ExecuteAsync(
                lease.Connection,
                "UPDATE index_roots SET updated_at = '2026-02-30T00:00:00.000Z' WHERE id = $id;",
                ("$id", root.Id));
        }

        await Assert.ThrowsAsync<FormatException>(() => repository.GetByIdAsync(root.Id));
    }

    [Fact]
    public async Task CanceledInsertDoesNotCreateRoot()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        SqliteIndexRootRepository repository = new(factory);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.AddAsync(CreateRegistration(), cancellation.Token));

        Assert.Empty(await repository.ListAsync());
    }

    private static IndexRootRegistration CreateRegistration(
        string path = @"C:\Synthetic",
        string displayName = "Synthetic root",
        string volumeIdentity = "volume-1",
        string directoryIdentity = "directory-1")
    {
        return new(
            path,
            displayName,
            volumeIdentity,
            directoryIdentity,
            DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", CultureInfo.InvariantCulture));
    }

    private static async Task<SqliteConnectionFactory> CreateMigratedFactoryAsync(string path)
    {
        SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(path);
        await SqliteMigrationRunner.ApplyAsync(factory);
        return factory;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnectionFactory factory, string sql)
    {
        await using SqliteConnectionLease lease = await factory.OpenReaderAsync();
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnectionFactory factory, string sql)
    {
        await using SqliteConnectionLease lease = await factory.OpenReaderAsync();
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("The test query returned no text value.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Path = System.IO.Path.Combine(localData, $"lfi-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
