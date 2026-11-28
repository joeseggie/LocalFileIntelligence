using LocalFileIntelligence.Application.Persistence;
using LocalFileIntelligence.Storage;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.IntegrationTests;

public sealed class ScanAndJobRepositoryTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IncompleteScanDoesNotTombstoneUnseenEntriesOrDiscardQueuedWork()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteScanRunRepository scans = new(fixture.Factory);
        SqliteIndexJobRepository jobs = new(fixture.Factory);

        ScanRun scan = await scans.StartAsync(1, ScanType.Initial, StartTime);
        long jobId = await jobs.EnqueueAsync(FileJobRequest(fileRevision: 1));
        await scans.RecordProgressAsync(scan.Id, new ScanProgress(2, 1, 1, "folder\\item.txt"));
        await scans.FinishAsync(scan.Id, ScanStatus.Incomplete, StartTime.AddMinutes(1), "access denied");

        Assert.Equal(0, await fixture.ScalarAsync("SELECT is_deleted FROM file_entries WHERE id = 1;"));
        Assert.Equal("pending", await fixture.ScalarTextAsync("SELECT status FROM index_roots WHERE id = 1;"));
        Assert.Equal("access denied", await fixture.ScalarTextAsync("SELECT last_error FROM index_roots WHERE id = 1;"));
        Assert.Equal("incomplete", await fixture.ScalarTextAsync("SELECT status FROM scan_runs WHERE id = $id;", scan.Id));
        IndexJob? job = await jobs.ClaimNextAsync("worker", StartTime.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.NotNull(job);
        Assert.Equal(jobId, job.Id);
    }

    [Fact]
    public async Task OnlySuccessfulCompleteScanTombstonesEntriesAndAdvancesIndexRevision()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteScanRunRepository scans = new(fixture.Factory);
        ScanRun scan = await scans.StartAsync(1, ScanType.Reconciliation, StartTime);

        await scans.FinishAsync(scan.Id, ScanStatus.Completed, StartTime.AddMinutes(1));

        Assert.Equal(1, await fixture.ScalarAsync("SELECT is_deleted FROM file_entries WHERE id = 1;"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT index_revision FROM index_roots WHERE id = 1;"));
        Assert.Equal("ready", await fixture.ScalarTextAsync("SELECT status FROM index_roots WHERE id = 1;"));
        Assert.Equal(1, await fixture.ScalarAsync("SELECT generation FROM scan_runs WHERE id = $id;", scan.Id));
    }

    [Fact]
    public async Task ScanProgressIsMonotonicAndAReportedFailureCannotCompleteSuccessfully()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteScanRunRepository scans = new(fixture.Factory);
        ScanRun scan = await scans.StartAsync(1, ScanType.Manual, StartTime);

        await scans.RecordProgressAsync(scan.Id, new ScanProgress(2, 1, 0, "one.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scans.RecordProgressAsync(scan.Id, new ScanProgress(1, 1, 0, "two.txt")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => scans.RecordProgressAsync(scan.Id, new ScanProgress(2, 1, 0, @"..\outside.txt")));
        await scans.RecordProgressAsync(scan.Id, new ScanProgress(2, 1, 1, "two.txt"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scans.FinishAsync(scan.Id, ScanStatus.Completed, StartTime.AddMinutes(1)));
        Assert.Equal("running", await fixture.ScalarTextAsync("SELECT status FROM scan_runs WHERE id = $id;", scan.Id));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT is_deleted FROM file_entries WHERE id = 1;"));
    }

    [Fact]
    public async Task SuccessfulCompletionIsRejectedWhileDiscoveredFilesRemainUnprocessed()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteScanRunRepository scans = new(fixture.Factory);
        ScanRun scan = await scans.StartAsync(1, ScanType.Manual, StartTime);
        await scans.RecordProgressAsync(scan.Id, new ScanProgress(2, 1, 0, "one.txt"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scans.FinishAsync(scan.Id, ScanStatus.Completed, StartTime.AddMinutes(1)));

        Assert.Equal("running", await fixture.ScalarTextAsync("SELECT status FROM scan_runs WHERE id = $id;", scan.Id));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT is_deleted FROM file_entries WHERE id = 1;"));
    }

    [Fact]
    public async Task ScanCannotStartWhenRootIsDisabledOrAlreadyHasAnActiveRun()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteScanRunRepository scans = new(fixture.Factory);

        ScanRun active = await scans.StartAsync(1, ScanType.Initial, StartTime);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scans.StartAsync(1, ScanType.Manual, StartTime.AddMinutes(1)));
        await fixture.ExecuteAsync("UPDATE index_roots SET enabled = 0 WHERE id = 2;");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scans.StartAsync(2, ScanType.Initial, StartTime));

        await scans.FinishAsync(active.Id, ScanStatus.Cancelled, StartTime.AddMinutes(1));
    }

    [Fact]
    public async Task InterruptedScanRecoversWithoutTombstoningAndCanBeRestarted()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteScanRunRepository scans = new(fixture.Factory);
        await fixture.ExecuteAsync("UPDATE index_roots SET last_scan_at = '2025-12-31T00:00:00.000Z' WHERE id = 1;");
        ScanRun interrupted = await scans.StartAsync(1, ScanType.Initial, StartTime);

        Assert.True(await scans.RecoverInterruptedAsync(1, StartTime.AddMinutes(1)));
        Assert.Equal("incomplete",
            await fixture.ScalarTextAsync("SELECT status FROM scan_runs WHERE id = $id;", interrupted.Id));
        Assert.Equal("ready", await fixture.ScalarTextAsync("SELECT status FROM index_roots WHERE id = 1;"));
        Assert.Equal("interrupted", await fixture.ScalarTextAsync("SELECT last_error FROM index_roots WHERE id = 1;"));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT is_deleted FROM file_entries WHERE id = 1;"));
        Assert.False(await scans.RecoverInterruptedAsync(1, StartTime.AddMinutes(2)));

        ScanRun restarted = await scans.StartAsync(1, ScanType.Reconciliation, StartTime.AddMinutes(2));
        Assert.Equal(interrupted.Generation + 1, restarted.Generation);
        await scans.FinishAsync(restarted.Id, ScanStatus.Cancelled, StartTime.AddMinutes(3));
    }

    [Fact]
    public async Task ActiveJobsDeduplicateAndAreClaimedByPriority()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteIndexJobRepository jobs = new(fixture.Factory);
        long lowId = await jobs.EnqueueAsync(JobRequest("low", 10));
        long highId = await jobs.EnqueueAsync(JobRequest("high", 90));
        long duplicateId = await jobs.EnqueueAsync(JobRequest("high", 20));

        Assert.Equal(highId, duplicateId);
        Assert.Equal(2, await fixture.ScalarAsync("SELECT count(*) FROM index_jobs WHERE status = 'queued';"));

        IndexJob? first = await jobs.ClaimNextAsync("worker-a", StartTime, TimeSpan.FromMinutes(1));
        Assert.NotNull(first);
        Assert.Equal(highId, first.Id);
        Assert.Equal(1, first.Attempts);
        Assert.Equal("worker-a", first.LeaseOwner);
        Assert.Equal(StartTime.AddMinutes(1), first.LeaseUntil);

        await jobs.CompleteAsync(first.Id, "worker-a", first.Attempts, StartTime.AddSeconds(30));
        IndexJob? second = await jobs.ClaimNextAsync("worker-b", StartTime.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.NotNull(second);
        Assert.Equal(lowId, second.Id);
    }

    [Fact]
    public async Task ConcurrentClaimsReturnEachReadyJobAtMostOnce()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteIndexJobRepository jobs = new(fixture.Factory);
        long id = await jobs.EnqueueAsync(JobRequest("only-job", 50));

        Task<IndexJob?>[] claims = Enumerable.Range(0, 8)
            .Select(index => jobs.ClaimNextAsync($"worker-{index}", StartTime, TimeSpan.FromMinutes(1)))
            .ToArray();
        IndexJob?[] results = await Task.WhenAll(claims);

        IndexJob claimed = Assert.Single(results, result => result is not null)!;
        Assert.Equal(id, claimed.Id);
        Assert.Equal(1, claimed.Attempts);
    }

    [Fact]
    public async Task ExpiredLeasesRecoverAndRetriesStopAtTheConfiguredAttemptLimit()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteIndexJobRepository jobs = new(fixture.Factory);
        long id = await jobs.EnqueueAsync(JobRequest("retry", 50));
        IndexJob? first = await jobs.ClaimNextAsync("worker", StartTime, TimeSpan.FromMinutes(1));
        Assert.NotNull(first);

        Assert.Equal(0, await jobs.RecoverExpiredLeasesAsync(StartTime.AddSeconds(59)));
        Assert.Equal(1, await jobs.RecoverExpiredLeasesAsync(StartTime.AddMinutes(1)));
        Assert.Equal("queued", await fixture.ScalarTextAsync("SELECT status FROM index_jobs WHERE id = $id;", id));

        DateTimeOffset attemptTime = StartTime.AddMinutes(1);
        for (int attempt = 2; attempt <= SqliteIndexJobRepository.MaximumAttempts; attempt++)
        {
            IndexJob? job = await jobs.ClaimNextAsync("worker", attemptTime, TimeSpan.FromMinutes(1));
            Assert.NotNull(job);
            Assert.Equal(attempt, job.Attempts);
            if (attempt == 2)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => jobs.CompleteAsync(id, "worker", first.Attempts, attemptTime.AddSeconds(30)));
            }

            await jobs.RetryAsync(
                job.Id,
                "worker",
                job.Attempts,
                attemptTime.AddSeconds(30),
                attemptTime.AddMinutes(1),
                "synthetic failure");
            attemptTime = attemptTime.AddMinutes(1);
        }

        Assert.Equal("failed", await fixture.ScalarTextAsync("SELECT status FROM index_jobs WHERE id = $id;", id));
        Assert.Equal(SqliteIndexJobRepository.MaximumAttempts,
            await fixture.ScalarAsync("SELECT attempts FROM index_jobs WHERE id = $id;", id));
        Assert.Null(await jobs.ClaimNextAsync("worker", attemptTime, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task CancellationReleasesLeaseAndInvalidLeaseTransitionsFail()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteIndexJobRepository jobs = new(fixture.Factory);
        long id = await jobs.EnqueueAsync(JobRequest("cancel", 50));
        IndexJob? job = await jobs.ClaimNextAsync("worker-a", StartTime, TimeSpan.FromMinutes(1));
        Assert.NotNull(job);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => jobs.CompleteAsync(id, "worker-b", job.Attempts, StartTime.AddSeconds(10)));
        await jobs.CancelAsync(id, StartTime.AddSeconds(10));

        Assert.Equal("cancelled", await fixture.ScalarTextAsync("SELECT status FROM index_jobs WHERE id = $id;", id));
        Assert.Equal(0, await fixture.ScalarAsync(
            "SELECT count(*) FROM index_jobs WHERE id = $id AND lease_owner IS NOT NULL AND lease_until IS NOT NULL;",
            id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => jobs.CompleteAsync(id, "worker-a", job.Attempts, StartTime.AddSeconds(20)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.CancelAsync(id, StartTime.AddSeconds(20)));
    }

    [Fact]
    public async Task JobsWithChangedPolicyOrFileRevisionAreCancelledAndCannotCrossRoots()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteIndexJobRepository jobs = new(fixture.Factory);
        long stalePolicyJob = await jobs.EnqueueAsync(JobRequest("stale-policy", 50));
        await fixture.ExecuteAsync("UPDATE index_roots SET policy_revision = 2 WHERE id = 1;");

        Assert.Null(await jobs.ClaimNextAsync("worker", StartTime, TimeSpan.FromMinutes(1)));
        Assert.Equal("cancelled",
            await fixture.ScalarTextAsync("SELECT status FROM index_jobs WHERE id = $id;", stalePolicyJob));

        IndexJobRequest crossRootRequest = new(
            1,
            2,
            IndexJobType.Hash,
            "cross-root",
            50,
            StartTime,
            StartTime,
            1,
            2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.EnqueueAsync(crossRootRequest));

        await fixture.ExecuteAsync("UPDATE index_roots SET policy_revision = 1 WHERE id = 1;");
        long staleFileJob = await jobs.EnqueueAsync(new IndexJobRequest(
            1,
            1,
            IndexJobType.Extract,
            "stale-file",
            50,
            StartTime,
            StartTime,
            1,
            1));
        await fixture.ExecuteAsync("UPDATE file_entries SET revision = 2 WHERE id = 1;");
        Assert.Null(await jobs.ClaimNextAsync("worker", StartTime, TimeSpan.FromMinutes(1)));
        Assert.Equal("cancelled",
            await fixture.ScalarTextAsync("SELECT status FROM index_jobs WHERE id = $id;", staleFileJob));
    }

    [Fact]
    public async Task EnqueueReplacesStaleActiveWorkAndInvalidatesItsLease()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteIndexJobRepository jobs = new(fixture.Factory);
        long staleQueuedId = await jobs.EnqueueAsync(FileJobRequest(fileRevision: 1));
        await fixture.ExecuteAsync("UPDATE file_entries SET revision = 2 WHERE id = 1;");

        long currentId = await jobs.EnqueueAsync(FileJobRequest(fileRevision: 2));

        Assert.NotEqual(staleQueuedId, currentId);
        Assert.Equal("cancelled",
            await fixture.ScalarTextAsync("SELECT status FROM index_jobs WHERE id = $id;", staleQueuedId));
        IndexJob? running = await jobs.ClaimNextAsync("worker-a", StartTime, TimeSpan.FromMinutes(1));
        Assert.NotNull(running);
        Assert.Equal(currentId, running.Id);
        Assert.Equal(2, running.ExpectedFileRevision);

        await fixture.ExecuteAsync("UPDATE file_entries SET revision = 3 WHERE id = 1;");
        long replacementId = await jobs.EnqueueAsync(FileJobRequest(fileRevision: 3));

        Assert.NotEqual(currentId, replacementId);
        Assert.Equal(0, await fixture.ScalarAsync(
            "SELECT count(*) FROM index_jobs WHERE id = $id AND (status <> 'cancelled' OR lease_owner IS NOT NULL);",
            currentId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => jobs.CompleteAsync(currentId, "worker-a", running.Attempts, StartTime.AddSeconds(10)));
        IndexJob? replacement = await jobs.ClaimNextAsync("worker-b", StartTime.AddSeconds(10), TimeSpan.FromMinutes(1));
        Assert.NotNull(replacement);
        Assert.Equal(replacementId, replacement.Id);
        Assert.Equal(3, replacement.ExpectedFileRevision);
    }

    [Fact]
    public async Task PausedRootsRetainQueuedAndRecoveredWorkButAreNotClaimed()
    {
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync();
        SqliteIndexJobRepository jobs = new(fixture.Factory);
        long expiringId = await jobs.EnqueueAsync(JobRequest("expiring", 90, rootId: 2));
        IndexJob? expiring = await jobs.ClaimNextAsync("worker", StartTime, TimeSpan.FromMinutes(1));
        Assert.NotNull(expiring);
        Assert.Equal(expiringId, expiring.Id);
        long queuedId = await jobs.EnqueueAsync(JobRequest("queued", 80, rootId: 2));
        long activeRootJobId = await jobs.EnqueueAsync(JobRequest("active-root", 10));
        await fixture.ExecuteAsync("UPDATE index_roots SET status = 'paused' WHERE id = 2;");

        IndexJob? claimed = await jobs.ClaimNextAsync("worker", StartTime.AddMinutes(2), TimeSpan.FromMinutes(1));

        Assert.NotNull(claimed);
        Assert.Equal(activeRootJobId, claimed.Id);
        Assert.Equal("queued", await fixture.ScalarTextAsync("SELECT status FROM index_jobs WHERE id = $id;", expiringId));
        Assert.Equal("queued", await fixture.ScalarTextAsync("SELECT status FROM index_jobs WHERE id = $id;", queuedId));
        Assert.Null(await jobs.ClaimNextAsync("worker", StartTime.AddMinutes(2), TimeSpan.FromMinutes(1)));

        await fixture.ExecuteAsync("UPDATE index_roots SET status = 'ready' WHERE id = 2;");
        IndexJob? resumed = await jobs.ClaimNextAsync("worker", StartTime.AddMinutes(2), TimeSpan.FromMinutes(1));
        Assert.NotNull(resumed);
        Assert.Equal(expiringId, resumed.Id);
        Assert.Equal(2, resumed.Attempts);
    }

    private static IndexJobRequest JobRequest(string dedupeKey, int priority, long rootId = 1)
    {
        return new IndexJobRequest(
            rootId,
            null,
            IndexJobType.Reconcile,
            dedupeKey,
            priority,
            StartTime,
            StartTime,
            null,
            1);
    }

    private static IndexJobRequest FileJobRequest(long fileRevision)
    {
        return new IndexJobRequest(
            1,
            1,
            IndexJobType.Extract,
            "extract-file-1",
            50,
            StartTime,
            StartTime,
            fileRevision,
            1);
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private DatabaseFixture(string directory, SqliteConnectionFactory factory)
        {
            _directory = directory;
            Factory = factory;
        }

        public SqliteConnectionFactory Factory { get; }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string directory = Path.Combine(localData, $"lfi-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory);
            DatabaseFixture fixture = new(directory, factory);
            try
            {
                await using SqliteConnectionLease lease = await factory.OpenWriterAsync();
                await using (SqliteCommand schema = lease.Connection.CreateCommand())
                {
                    schema.CommandText = await File.ReadAllTextAsync(
                        Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema.sql"));
                    await schema.ExecuteNonQueryAsync();
                }

                await using SqliteCommand seed = lease.Connection.CreateCommand();
                seed.CommandText =
                    """
                    INSERT INTO index_roots (id, path, path_key, display_name, enabled, status, volume_identity,
                                             directory_identity, created_at, updated_at)
                    VALUES
                        (1, 'C:\Synthetic', 'C:\SYNTHETIC', 'Synthetic root', 1, 'ready', 'volume-1',
                         'directory-1', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z'),
                        (2, 'C:\Synthetic2', 'C:\SYNTHETIC2', 'Synthetic root 2', 1, 'ready', 'volume-1',
                         'directory-2', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
                    INSERT INTO file_entries (id, root_id, relative_path, path_key, parent_path_key, name, extension,
                                              entry_type, size_bytes, created_at_fs, modified_at_fs, last_seen_at,
                                              extraction_status, indexing_status)
                    VALUES
                        (1, 1, 'one.txt', 'ONE.TXT', '', 'one.txt', '.txt', 'file', 3,
                         '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                         '2026-01-01T00:00:00.000Z', 'pending', 'pending'),
                        (2, 2, 'two.txt', 'TWO.TXT', '', 'two.txt', '.txt', 'file', 3,
                         '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                         '2026-01-01T00:00:00.000Z', 'pending', 'pending');
                    """;
                await seed.ExecuteNonQueryAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<long> ScalarAsync(string sql, long? id = null)
        {
            await using SqliteConnectionLease lease = await Factory.OpenWriterAsync();
            await using SqliteCommand command = lease.Connection.CreateCommand();
            command.CommandText = sql;
            if (id is not null)
            {
                command.Parameters.AddWithValue("$id", id.Value);
            }

            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<string?> ScalarTextAsync(string sql, long? id = null)
        {
            await using SqliteConnectionLease lease = await Factory.OpenWriterAsync();
            await using SqliteCommand command = lease.Connection.CreateCommand();
            command.CommandText = sql;
            if (id is not null)
            {
                command.Parameters.AddWithValue("$id", id.Value);
            }

            object? value = await command.ExecuteScalarAsync();
            return value is DBNull or null ? null : Convert.ToString(value);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using SqliteConnectionLease lease = await Factory.OpenWriterAsync();
            await using SqliteCommand command = lease.Connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            await Task.Run(() => Directory.Delete(_directory, recursive: true));
        }
    }
}
