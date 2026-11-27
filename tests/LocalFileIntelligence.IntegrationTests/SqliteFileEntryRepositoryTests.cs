using System.Globalization;
using LocalFileIntelligence.Application.Persistence;
using LocalFileIntelligence.Storage;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.IntegrationTests;

public sealed class SqliteFileEntryRepositoryTests
{
    private static readonly DateTimeOffset Timestamp =
        DateTimeOffset.Parse("2026-01-01T00:00:00.000Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task UpsertKeepsStableIdAndInvalidatesStaleHashAndContentOnMetadataChange()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        long rootId = await AddReadyRootAsync(factory);
        SqliteFileEntryRepository repository = new(factory);
        FileEntry inserted = await repository.UpsertAsync(rootId, Observation("notes.txt"));
        await SetCurrentContentAsync(factory, inserted);

        FileEntry repeated = await repository.UpsertAsync(
            rootId,
            Observation("notes.txt", lastSeenAt: Timestamp.AddMinutes(1), generation: 2));

        Assert.Equal(inserted.Id, repeated.Id);
        Assert.Equal(inserted.Revision, repeated.Revision);
        Assert.Equal(2, repeated.LastSeenGeneration);
        Assert.Equal(new string('a', 64), repeated.Sha256);
        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM file_contents;"));
        long afterRepeatRevision = await ScalarAsync(
            factory,
            "SELECT index_revision FROM index_roots WHERE id = $id;",
            rootId);
        Assert.Equal(3, afterRepeatRevision);

        FileEntry changed = await repository.UpsertAsync(
            rootId,
            Observation("notes.txt", size: 18, modifiedAt: Timestamp.AddSeconds(1), generation: 3));

        Assert.Equal(inserted.Id, changed.Id);
        Assert.Equal(inserted.Revision + 1, changed.Revision);
        Assert.Null(changed.Sha256);
        Assert.Null(changed.LastIndexedAt);
        Assert.Equal(FileEntryExtractionStatus.Pending, changed.ExtractionStatus);
        Assert.Equal(FileEntryIndexingStatus.Pending, changed.IndexingStatus);
        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM file_contents;"));
        Assert.Equal(0, await MatchesAsync(factory, "stale"));
        Assert.Equal(
            afterRepeatRevision + 1,
            await ScalarAsync(factory, "SELECT index_revision FROM index_roots WHERE id = $id;", rootId));
    }

    [Fact]
    public async Task RenameRetainsIdInvalidatesContentAndConflictRollsBack()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        long rootId = await AddReadyRootAsync(factory);
        SqliteFileEntryRepository repository = new(factory);
        FileEntry original = await repository.UpsertAsync(rootId, Observation("old.txt"));
        FileEntry occupied = await repository.UpsertAsync(rootId, Observation("occupied.txt"));
        await SetCurrentContentAsync(factory, original);

        FileEntry renamed = Assert.IsType<FileEntry>(
            await repository.RenameAsync(rootId, "old.txt", Observation("new.txt")));

        Assert.Equal(original.Id, renamed.Id);
        Assert.Equal(original.Revision + 1, renamed.Revision);
        Assert.Equal("new.txt", renamed.RelativePath);
        Assert.Null(renamed.Sha256);
        Assert.Null(await repository.GetByPathAsync(rootId, "old.txt"));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM file_contents;"));

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.RenameAsync(rootId, "new.txt", Observation("occupied.txt")));

        FileEntry unchanged = Assert.IsType<FileEntry>(await repository.GetByIdAsync(rootId, original.Id));
        Assert.Equal("new.txt", unchanged.RelativePath);
        Assert.Equal(renamed.Revision, unchanged.Revision);
        Assert.Equal(occupied.Id, Assert.IsType<FileEntry>(
            await repository.GetByPathAsync(rootId, "occupied.txt")).Id);
    }

    [Fact]
    public async Task RenameRejectsPopulatedDirectoryWithoutChangingDescendantsOrContent()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        long rootId = await AddReadyRootAsync(factory);
        SqliteFileEntryRepository repository = new(factory);
        FileEntry folder = await repository.UpsertAsync(rootId, Observation("Project", IndexEntryType.Directory, size: 0));
        FileEntry child = await repository.UpsertAsync(rootId, Observation(@"Project\child.txt"));
        await SetCurrentContentAsync(factory, child);
        long rootRevision = await ScalarAsync(
            factory,
            "SELECT index_revision FROM index_roots WHERE id = $id;",
            rootId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.RenameAsync(
                rootId,
                "Project",
                Observation("Renamed", IndexEntryType.Directory, size: 0)));

        FileEntry unchangedFolder = Assert.IsType<FileEntry>(
            await repository.GetByPathAsync(rootId, "Project"));
        FileEntry unchangedChild = Assert.IsType<FileEntry>(
            await repository.GetByPathAsync(rootId, @"Project\child.txt"));
        Assert.Equal(folder.Id, unchangedFolder.Id);
        Assert.Equal(folder.Revision, unchangedFolder.Revision);
        Assert.Equal(child.Id, unchangedChild.Id);
        Assert.Equal(child.Revision, unchangedChild.Revision);
        Assert.Single(await repository.ListChildrenAsync(rootId, "Project", null, 10));
        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM file_contents;"));
        Assert.Equal(rootRevision, await ScalarAsync(
            factory,
            "SELECT index_revision FROM index_roots WHERE id = $id;",
            rootId));
    }

    [Fact]
    public async Task TombstonesAreIdempotentAndUpsertResurrectsStableEntry()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        long rootId = await AddReadyRootAsync(factory);
        SqliteFileEntryRepository repository = new(factory);
        FileEntry inserted = await repository.UpsertAsync(rootId, Observation("delete-me.txt"));
        await SetCurrentContentAsync(factory, inserted);

        FileEntry tombstone = Assert.IsType<FileEntry>(
            await repository.TombstoneAsync(rootId, "delete-me.txt", Timestamp.AddMinutes(1)));
        FileEntry repeatedTombstone = Assert.IsType<FileEntry>(
            await repository.TombstoneAsync(rootId, "delete-me.txt", Timestamp.AddMinutes(2)));

        Assert.True(tombstone.IsDeleted);
        Assert.Equal(Timestamp.AddMinutes(1), tombstone.DeletedAt);
        Assert.Equal(tombstone.Revision, repeatedTombstone.Revision);
        Assert.Empty(await repository.ListChildrenAsync(rootId, string.Empty, null, 10));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM file_contents;"));

        FileEntry resurrected = await repository.UpsertAsync(
            rootId,
            Observation("delete-me.txt", lastSeenAt: Timestamp.AddMinutes(3)));

        Assert.Equal(inserted.Id, resurrected.Id);
        Assert.Equal(tombstone.Revision + 1, resurrected.Revision);
        Assert.False(resurrected.IsDeleted);
        Assert.Null(resurrected.DeletedAt);
        Assert.Null(resurrected.Sha256);
    }

    [Fact]
    public async Task DirectoryQueriesReturnOnlyDirectChildrenInStableBoundedPages()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        long firstRootId = await AddReadyRootAsync(factory);
        long secondRootId = await AddReadyRootAsync(factory, @"C:\Synthetic2", "Synthetic root 2");
        SqliteFileEntryRepository repository = new(factory);
        await repository.UpsertAsync(firstRootId, Observation("Project", IndexEntryType.Directory, size: 0));
        await repository.UpsertAsync(firstRootId, Observation(@"Project\README.md"));
        await repository.UpsertAsync(firstRootId, Observation(@"Project\Sub", IndexEntryType.Directory, size: 0));
        await repository.UpsertAsync(firstRootId, Observation(@"Project\Sub\deep.txt"));
        await repository.UpsertAsync(firstRootId, Observation("top.txt"));
        await repository.UpsertAsync(secondRootId, Observation("Project"));

        IReadOnlyList<FileEntry> firstPage = await repository.ListChildrenAsync(
            firstRootId, "project", afterRelativePath: null, limit: 1);
        IReadOnlyList<FileEntry> nextPage = await repository.ListChildrenAsync(
            firstRootId, "PROJECT", firstPage[0].RelativePath, limit: 10);
        IReadOnlyList<FileEntry> rootChildren = await repository.ListChildrenAsync(
            firstRootId, string.Empty, afterRelativePath: null, limit: 10);
        IReadOnlyList<FileEntry> otherRootChildren = await repository.ListChildrenAsync(
            secondRootId, string.Empty, afterRelativePath: null, limit: 10);

        Assert.Single(firstPage);
        Assert.Single(nextPage);
        Assert.All(firstPage.Concat(nextPage), entry => Assert.Equal("PROJECT", entry.ParentPathKey));
        Assert.Equal(2, rootChildren.Count);
        Assert.Equal(new[] { "Project" }, otherRootChildren.Select(entry => entry.Name));
        Assert.Equal(IndexEntryType.Directory, rootChildren.Single(entry => entry.Name == "Project").EntryType);

        await using SqliteConnectionLease planLease = await factory.OpenReaderAsync();
        await using SqliteCommand plan = planLease.Connection.CreateCommand();
        plan.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT id FROM file_entries
            WHERE root_id = $root_id
              AND parent_path_key = $parent_path_key
              AND is_deleted = 0
              AND path_key > $after_path_key
            ORDER BY path_key
            LIMIT 10;
            """;
        plan.Parameters.AddWithValue("$root_id", firstRootId);
        plan.Parameters.AddWithValue("$parent_path_key", "PROJECT");
        plan.Parameters.AddWithValue("$after_path_key", "PROJECT\\README.MD");
        await using SqliteDataReader planReader = await plan.ExecuteReaderAsync();
        List<string> planDetails = [];
        while (await planReader.ReadAsync())
        {
            planDetails.Add(planReader.GetString(3));
        }

        Assert.Contains(planDetails, detail => detail.Contains("entries_by_parent_path_key", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListChildrenAsync(firstRootId, string.Empty, null, 501));
    }

    [Fact]
    public async Task RootScopedLookupsAndMutationsNeverCrossOwnership()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        long firstRootId = await AddReadyRootAsync(factory);
        long secondRootId = await AddReadyRootAsync(factory, @"C:\Synthetic2", "Synthetic root 2");
        SqliteFileEntryRepository repository = new(factory);
        FileEntry firstEntry = await repository.UpsertAsync(firstRootId, Observation("private.txt"));

        Assert.Null(await repository.GetByIdAsync(secondRootId, firstEntry.Id));
        Assert.Null(await repository.GetByPathAsync(secondRootId, "private.txt"));
        Assert.Null(await repository.RenameAsync(secondRootId, "private.txt", Observation("renamed.txt")));
        Assert.Null(await repository.TombstoneAsync(secondRootId, "private.txt", Timestamp));

        FileEntry secondEntry = await repository.UpsertAsync(secondRootId, Observation("private.txt"));
        Assert.NotEqual(firstEntry.Id, secondEntry.Id);
        Assert.Equal(firstRootId, Assert.IsType<FileEntry>(
            await repository.GetByIdAsync(firstRootId, firstEntry.Id)).RootId);
        Assert.Equal(secondRootId, Assert.IsType<FileEntry>(
            await repository.GetByIdAsync(secondRootId, secondEntry.Id)).RootId);
    }

    [Fact]
    public async Task NormalizedUnicodePathKeysEnforcePerRootUniqueness()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        long rootId = await AddReadyRootAsync(factory);
        SqliteFileEntryRepository repository = new(factory);

        FileEntry original = await repository.UpsertAsync(rootId, Observation("Résumé.txt"));
        FileEntry sameKey = await repository.UpsertAsync(rootId, Observation("résumé.txt"));

        Assert.Equal(original.Id, sameKey.Id);
        Assert.Equal(original.Revision + 1, sameKey.Revision);
        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM file_entries WHERE root_id = $id;", rootId));
        Assert.Equal(original.Id, Assert.IsType<FileEntry>(
            await repository.GetByPathAsync(rootId, "RÉSUMÉ.TXT")).Id);
    }

    [Fact]
    public async Task InvalidPathsAndCanceledWritesDoNotPersistEntries()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await CreateMigratedFactoryAsync(directory.Path);
        long rootId = await AddReadyRootAsync(factory);
        SqliteFileEntryRepository repository = new(factory);

        foreach (string invalidPath in new[] { @"..\escape.txt", @"C:\absolute.txt", @"\rooted.txt", @"a\\b.txt", "\ud800.txt" })
        {
            await Assert.ThrowsAsync<FormatException>(
                () => repository.UpsertAsync(rootId, Observation(invalidPath)));
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.UpsertAsync(rootId, Observation("bad-directory", IndexEntryType.Directory, size: 1)));

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.UpsertAsync(rootId, Observation("cancelled.txt"), cancellation.Token));

        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM file_entries;"));
    }

    private static FileEntryObservation Observation(
        string path,
        IndexEntryType type = IndexEntryType.File,
        long size = 17,
        DateTimeOffset? modifiedAt = null,
        DateTimeOffset? lastSeenAt = null,
        long generation = 1)
    {
        return new(
            path,
            type,
            size,
            Timestamp,
            modifiedAt ?? Timestamp,
            lastSeenAt ?? Timestamp,
            "synthetic-file-id",
            generation);
    }

    private static async Task<long> AddReadyRootAsync(
        SqliteConnectionFactory factory,
        string path = @"C:\Synthetic",
        string displayName = "Synthetic root")
    {
        SqliteIndexRootRepository roots = new(factory);
        IndexRoot root = await roots.AddAsync(new(
            path,
            displayName,
            $"volume-{displayName}",
            $"directory-{displayName}",
            Timestamp));
        await roots.UpdateStateAsync(root.Id, enabled: true, IndexRootStatus.Ready, Timestamp.AddSeconds(1));
        return root.Id;
    }

    private static async Task SetCurrentContentAsync(SqliteConnectionFactory factory, FileEntry entry)
    {
        await using SqliteConnectionLease lease = await factory.OpenWriterAsync();
        await using SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: false);
        await using SqliteCommand update = lease.Connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE file_entries
            SET sha256 = $sha256, last_indexed_at = $indexed_at,
                extraction_status = 'extracted', indexing_status = 'indexed'
            WHERE root_id = $root_id AND id = $id AND revision = $revision;
            """;
        update.Parameters.AddWithValue("$sha256", new string('a', 64));
        update.Parameters.AddWithValue("$indexed_at", UtcTimestamp.Format(Timestamp));
        update.Parameters.AddWithValue("$root_id", entry.RootId);
        update.Parameters.AddWithValue("$id", entry.Id);
        update.Parameters.AddWithValue("$revision", entry.Revision);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());

        await using SqliteCommand insert = lease.Connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO file_contents (file_id, file_revision, extracted_text, extractor, extractor_version,
                                       extracted_at, content_length)
            VALUES ($file_id, $revision, 'stale indexed content', 'synthetic', '1',
                    $extracted_at, 21);
            """;
        insert.Parameters.AddWithValue("$file_id", entry.Id);
        insert.Parameters.AddWithValue("$revision", entry.Revision);
        insert.Parameters.AddWithValue("$extracted_at", UtcTimestamp.Format(Timestamp));
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<SqliteConnectionFactory> CreateMigratedFactoryAsync(string path)
    {
        SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(path);
        await SqliteMigrationRunner.ApplyAsync(factory);
        return factory;
    }

    private static async Task<long> ScalarAsync(
        SqliteConnectionFactory factory,
        string sql,
        long? value = null)
    {
        await using SqliteConnectionLease lease = await factory.OpenReaderAsync();
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        if (value is not null)
        {
            command.Parameters.AddWithValue("$id", value.Value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> MatchesAsync(SqliteConnectionFactory factory, string term)
    {
        await using SqliteConnectionLease lease = await factory.OpenReaderAsync();
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM file_contents_fts WHERE file_contents_fts MATCH $term;";
        command.Parameters.AddWithValue("$term", term);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
