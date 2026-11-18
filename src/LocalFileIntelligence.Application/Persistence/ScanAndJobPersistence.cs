namespace LocalFileIntelligence.Application.Persistence;

public enum ScanType
{
    Initial,
    Manual,
    Reconciliation,
}

public enum ScanStatus
{
    Completed,
    Incomplete,
    Cancelled,
    Failed,
}

public sealed record ScanRun(long Id, long RootId, long Generation);

public sealed record ScanProgress(
    int FilesDiscovered,
    int FilesProcessed,
    int FilesFailed,
    string? LastRelativePath);

public interface IScanRunRepository
{
    Task<ScanRun> StartAsync(
        long rootId,
        ScanType scanType,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task RecordProgressAsync(
        long scanRunId,
        ScanProgress progress,
        CancellationToken cancellationToken = default);

    Task FinishAsync(
        long scanRunId,
        ScanStatus status,
        DateTimeOffset completedAt,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    Task<bool> RecoverInterruptedAsync(
        long rootId,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken = default);
}

public enum IndexJobType
{
    Scan,
    Hash,
    Extract,
    Reconcile,
    Purge,
}

public enum IndexJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record IndexJobRequest(
    long RootId,
    long? FileId,
    IndexJobType JobType,
    string DedupeKey,
    int Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    long? ExpectedFileRevision,
    long ExpectedPolicyRevision);

public sealed record IndexJob(
    long Id,
    long RootId,
    long? FileId,
    IndexJobType JobType,
    string DedupeKey,
    IndexJobStatus Status,
    int Priority,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    long? ExpectedFileRevision,
    long ExpectedPolicyRevision,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntil);

public interface IIndexJobRepository
{
    Task<long> EnqueueAsync(
        IndexJobRequest request,
        CancellationToken cancellationToken = default);

    Task<IndexJob?> ClaimNextAsync(
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        long jobId,
        string leaseOwner,
        int attemptNumber,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task RetryAsync(
        long jobId,
        string leaseOwner,
        int attemptNumber,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        long jobId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default);
}
