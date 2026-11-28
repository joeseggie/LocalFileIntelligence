using System.Globalization;
using LocalFileIntelligence.Application.Persistence;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.Storage;

public sealed class SqliteIndexJobRepository : IIndexJobRepository
{
    public const int MaximumAttempts = 3;
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(1);
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteIndexJobRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<long> EnqueueAsync(
        IndexJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        string createdAt = SqliteRepositoryValues.FormatTimestamp(request.CreatedAt);
        string nextAttemptAt = SqliteRepositoryValues.FormatTimestamp(request.NextAttemptAt);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand retireCommand = connection.CreateCommand();
        retireCommand.Transaction = transaction;
        retireCommand.CommandText =
            """
            UPDATE index_jobs
            SET status = 'cancelled', completed_at = $created_at, lease_owner = NULL, lease_until = NULL
            WHERE root_id = $root_id AND job_type = $job_type AND dedupe_key = $dedupe_key
              AND status IN ('queued', 'running')
              AND NOT (
                  file_id IS $file_id
                  AND expected_file_revision IS $file_revision
                  AND expected_policy_revision = $policy_revision
              );
            """;
        AddRequestParameters(retireCommand, request, createdAt);
        await retireCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO index_jobs (
                root_id, file_id, job_type, dedupe_key, status, priority, created_at, next_attempt_at,
                expected_file_revision, expected_policy_revision
            )
            SELECT $root_id, $file_id, $job_type, $dedupe_key, 'queued', $priority, $created_at, $next_attempt_at,
                   $file_revision, $policy_revision
            FROM index_roots AS r
            WHERE r.id = $root_id
              AND r.enabled = 1
              AND r.status IN ('pending', 'scanning', 'ready')
              AND r.policy_revision = $policy_revision
              AND (
                  $file_id IS NULL
                  OR EXISTS (
                      SELECT 1 FROM file_entries AS f
                      WHERE f.id = $file_id AND f.root_id = r.id
                        AND f.revision = $file_revision AND f.is_deleted = 0 AND f.entry_type = 'file'
                  )
              )
            ON CONFLICT(root_id, job_type, dedupe_key) WHERE status IN ('queued', 'running')
            DO UPDATE SET priority = MAX(index_jobs.priority, excluded.priority)
            RETURNING id;
            """;
        AddRequestParameters(command, request, createdAt);
        command.Parameters.AddWithValue("$priority", request.Priority);
        command.Parameters.AddWithValue("$next_attempt_at", nextAttemptAt);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            throw new InvalidOperationException("The root or file is unavailable or its revision has changed.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<IndexJob?> ClaimNextAsync(
        string leaseOwner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseOwner(leaseOwner);
        ValidateLeaseDuration(leaseDuration);
        string timestamp = SqliteRepositoryValues.FormatTimestamp(now);
        string leaseUntil = SqliteRepositoryValues.FormatTimestamp(now.Add(leaseDuration));
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction();
        _ = await RecoverExpiredLeasesAsync(connection, transaction, timestamp, cancellationToken).ConfigureAwait(false);
        await CancelStaleQueuedJobsAsync(connection, transaction, timestamp, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE index_jobs
            SET status = 'running', attempts = attempts + 1, started_at = $now, completed_at = NULL,
                lease_owner = $lease_owner, lease_until = $lease_until
            WHERE id = (
                SELECT j.id
                FROM index_jobs AS j
                JOIN index_roots AS r ON r.id = j.root_id
                WHERE j.status = 'queued' AND j.next_attempt_at <= $now
                  AND r.enabled = 1 AND r.status IN ('pending', 'scanning', 'ready')
                  AND j.expected_policy_revision = r.policy_revision
                  AND (
                      j.file_id IS NULL
                      OR EXISTS (
                          SELECT 1 FROM file_entries AS f
                          WHERE f.id = j.file_id AND f.root_id = j.root_id
                            AND f.revision = j.expected_file_revision
                            AND f.is_deleted = 0 AND f.entry_type = 'file'
                      )
                  )
                ORDER BY j.priority DESC, j.next_attempt_at, j.created_at, j.id
                LIMIT 1
            )
            RETURNING id, root_id, file_id, job_type, dedupe_key, status, priority, attempts, created_at,
                      next_attempt_at, expected_file_revision, expected_policy_revision, lease_owner, lease_until;
            """;
        command.Parameters.AddWithValue("$now", timestamp);
        command.Parameters.AddWithValue("$lease_owner", leaseOwner);
        command.Parameters.AddWithValue("$lease_until", leaseUntil);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        IndexJob? job = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadJob(reader)
            : null;
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        string timestamp = SqliteRepositoryValues.FormatTimestamp(now);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteTransaction transaction = lease.Connection.BeginTransaction();
        int recovered = await RecoverExpiredLeasesAsync(
            lease.Connection,
            transaction,
            timestamp,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    public async Task CompleteAsync(
        long jobId,
        string leaseOwner,
        int attemptNumber,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateJobId(jobId);
        ValidateLeaseOwner(leaseOwner);
        ValidateAttemptNumber(attemptNumber);
        string timestamp = SqliteRepositoryValues.FormatTimestamp(completedAt);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText =
            """
            UPDATE index_jobs
            SET status = 'completed', completed_at = $completed_at, lease_owner = NULL, lease_until = NULL
            WHERE id = $job_id AND status = 'running' AND lease_owner = $lease_owner
              AND attempts = $attempt_number AND lease_until > $completed_at;
            """;
        command.Parameters.AddWithValue("$completed_at", timestamp);
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$lease_owner", leaseOwner);
        command.Parameters.AddWithValue("$attempt_number", attemptNumber);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The job does not have a valid lease for this owner.");
        }
    }

    public async Task RetryAsync(
        long jobId,
        string leaseOwner,
        int attemptNumber,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        ValidateJobId(jobId);
        ValidateLeaseOwner(leaseOwner);
        ValidateAttemptNumber(attemptNumber);
        SqliteRepositoryValues.ValidateErrorMessage(errorMessage);
        string failedTimestamp = SqliteRepositoryValues.FormatTimestamp(failedAt);
        string nextAttemptTimestamp = SqliteRepositoryValues.FormatTimestamp(nextAttemptAt);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText =
            """
            UPDATE index_jobs
            SET status = CASE WHEN attempts < $maximum_attempts THEN 'queued' ELSE 'failed' END,
                next_attempt_at = CASE WHEN attempts < $maximum_attempts THEN $next_attempt_at ELSE next_attempt_at END,
                completed_at = CASE WHEN attempts < $maximum_attempts THEN NULL ELSE $failed_at END,
                error_message = $error_message, lease_owner = NULL, lease_until = NULL
            WHERE id = $job_id AND status = 'running' AND lease_owner = $lease_owner
              AND attempts = $attempt_number AND lease_until > $failed_at;
            """;
        command.Parameters.AddWithValue("$maximum_attempts", MaximumAttempts);
        command.Parameters.AddWithValue("$next_attempt_at", nextAttemptTimestamp);
        command.Parameters.AddWithValue("$failed_at", failedTimestamp);
        command.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$lease_owner", leaseOwner);
        command.Parameters.AddWithValue("$attempt_number", attemptNumber);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The job does not have a valid lease for this owner.");
        }
    }

    public async Task CancelAsync(
        long jobId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default)
    {
        ValidateJobId(jobId);
        string timestamp = SqliteRepositoryValues.FormatTimestamp(cancelledAt);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText =
            """
            UPDATE index_jobs
            SET status = 'cancelled', completed_at = $cancelled_at, lease_owner = NULL, lease_until = NULL
            WHERE id = $job_id AND status IN ('queued', 'running');
            """;
        command.Parameters.AddWithValue("$cancelled_at", timestamp);
        command.Parameters.AddWithValue("$job_id", jobId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Only a queued or running job can be cancelled.");
        }
    }

    private static async Task<int> RecoverExpiredLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand recoverCommand = connection.CreateCommand();
        recoverCommand.Transaction = transaction;
        recoverCommand.CommandText =
            """
            UPDATE index_jobs
            SET status = CASE WHEN attempts < $maximum_attempts THEN 'queued' ELSE 'failed' END,
                next_attempt_at = CASE WHEN attempts < $maximum_attempts THEN $now ELSE next_attempt_at END,
                completed_at = CASE WHEN attempts < $maximum_attempts THEN NULL ELSE $now END,
                lease_owner = NULL, lease_until = NULL
            WHERE status = 'running' AND lease_until <= $now
              AND EXISTS (
                  SELECT 1 FROM index_roots AS r
                  WHERE r.id = index_jobs.root_id AND r.enabled = 1
                    AND r.status IN ('pending', 'scanning', 'ready', 'paused')
                    AND r.policy_revision = index_jobs.expected_policy_revision
                    AND (
                        index_jobs.file_id IS NULL
                        OR EXISTS (
                            SELECT 1 FROM file_entries AS f
                            WHERE f.id = index_jobs.file_id AND f.root_id = index_jobs.root_id
                              AND f.revision = index_jobs.expected_file_revision
                              AND f.is_deleted = 0 AND f.entry_type = 'file'
                        )
                    )
              );
            """;
        recoverCommand.Parameters.AddWithValue("$maximum_attempts", MaximumAttempts);
        recoverCommand.Parameters.AddWithValue("$now", timestamp);
        int recovered = await recoverCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand cancelCommand = connection.CreateCommand();
        cancelCommand.Transaction = transaction;
        cancelCommand.CommandText =
            """
            UPDATE index_jobs
            SET status = 'cancelled', completed_at = $now, lease_owner = NULL, lease_until = NULL
            WHERE status = 'running' AND lease_until <= $now;
            """;
        cancelCommand.Parameters.AddWithValue("$now", timestamp);
        return recovered + await cancelCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CancelStaleQueuedJobsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE index_jobs
            SET status = 'cancelled', completed_at = $now
            WHERE status = 'queued'
              AND NOT EXISTS (
                  SELECT 1 FROM index_roots AS r
                  WHERE r.id = index_jobs.root_id AND r.enabled = 1
                    AND r.status IN ('pending', 'scanning', 'ready', 'paused')
                    AND r.policy_revision = index_jobs.expected_policy_revision
                    AND (
                        index_jobs.file_id IS NULL
                        OR EXISTS (
                            SELECT 1 FROM file_entries AS f
                            WHERE f.id = index_jobs.file_id AND f.root_id = index_jobs.root_id
                              AND f.revision = index_jobs.expected_file_revision
                              AND f.is_deleted = 0 AND f.entry_type = 'file'
                        )
                    )
              );
            """;
        command.Parameters.AddWithValue("$now", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddRequestParameters(SqliteCommand command, IndexJobRequest request, string createdAt)
    {
        command.Parameters.AddWithValue("$root_id", request.RootId);
        command.Parameters.AddWithValue("$file_id", (object?)request.FileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$job_type", request.JobType.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$dedupe_key", request.DedupeKey);
        command.Parameters.AddWithValue("$created_at", createdAt);
        command.Parameters.AddWithValue("$file_revision", (object?)request.ExpectedFileRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$policy_revision", request.ExpectedPolicyRevision);
    }

    private static IndexJob ReadJob(SqliteDataReader reader)
    {
        return new IndexJob(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            Enum.Parse<IndexJobType>(reader.GetString(3), ignoreCase: true),
            reader.GetString(4),
            Enum.Parse<IndexJobStatus>(reader.GetString(5), ignoreCase: true),
            reader.GetInt32(6),
            reader.GetInt32(7),
            ParseTimestamp(reader.GetString(8)),
            ParseTimestamp(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)));
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static void ValidateRequest(IndexJobRequest request)
    {
        if (request.RootId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A positive root ID is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DedupeKey) || request.DedupeKey.Length > 256)
        {
            throw new ArgumentException("The deduplication key must contain between 1 and 256 characters.", nameof(request));
        }

        if (request.Priority is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Priority must be between 0 and 100.");
        }

        if (request.ExpectedPolicyRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A positive policy revision is required.");
        }

        bool fileJob = request.JobType is IndexJobType.Hash or IndexJobType.Extract;
        if (fileJob != (request.FileId is not null && request.ExpectedFileRevision is > 0)
            || (!fileJob && (request.FileId is not null || request.ExpectedFileRevision is not null)))
        {
            throw new ArgumentException("File jobs require a file ID and positive file revision only.", nameof(request));
        }
    }

    private static void ValidateLeaseOwner(string leaseOwner)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 256)
        {
            throw new ArgumentException("A lease owner between 1 and 256 characters is required.", nameof(leaseOwner));
        }
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                $"Lease duration must be greater than zero and at most {MaximumLeaseDuration}.");
        }
    }

    private static void ValidateJobId(long jobId)
    {
        if (jobId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jobId));
        }
    }

    private static void ValidateAttemptNumber(int attemptNumber)
    {
        if (attemptNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        }
    }
}
