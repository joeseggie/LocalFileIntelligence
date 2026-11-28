using System.Globalization;
using LocalFileIntelligence.Application.Persistence;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.Storage;

public sealed class SqliteScanRunRepository : IScanRunRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteScanRunRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<ScanRun> StartAsync(
        long rootId,
        ScanType scanType,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        if (rootId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rootId));
        }

        string timestamp = SqliteRepositoryValues.FormatTimestamp(startedAt);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand generationCommand = connection.CreateCommand();
        generationCommand.Transaction = transaction;
        generationCommand.CommandText =
            """
            UPDATE index_roots
            SET scan_generation = scan_generation + 1, status = 'scanning', updated_at = $started_at,
                index_revision = index_revision + 1
            WHERE id = $root_id
              AND enabled = 1
              AND status IN ('pending', 'ready', 'scanning', 'error')
              AND NOT EXISTS (
                  SELECT 1 FROM scan_runs WHERE root_id = $root_id AND status = 'running'
              )
            RETURNING scan_generation;
            """;
        generationCommand.Parameters.AddWithValue("$root_id", rootId);
        generationCommand.Parameters.AddWithValue("$started_at", timestamp);
        object? generationValue = await generationCommand.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (generationValue is null)
        {
            throw new InvalidOperationException("The root is unavailable or already has an active scan.");
        }

        long generation = Convert.ToInt64(generationValue, CultureInfo.InvariantCulture);
        await using SqliteCommand insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText =
            """
            INSERT INTO scan_runs (root_id, generation, scan_type, status, started_at)
            VALUES ($root_id, $generation, $scan_type, 'running', $started_at)
            RETURNING id;
            """;
        insertCommand.Parameters.AddWithValue("$root_id", rootId);
        insertCommand.Parameters.AddWithValue("$generation", generation);
        insertCommand.Parameters.AddWithValue("$scan_type", scanType.ToString().ToLowerInvariant());
        insertCommand.Parameters.AddWithValue("$started_at", timestamp);
        long id = Convert.ToInt64(
            await insertCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ScanRun(id, rootId, generation);
    }

    public async Task RecordProgressAsync(
        long scanRunId,
        ScanProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (scanRunId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scanRunId));
        }

        if (progress.FilesDiscovered < 0
            || progress.FilesProcessed < 0
            || progress.FilesFailed < 0
            || (long)progress.FilesProcessed + progress.FilesFailed > progress.FilesDiscovered)
        {
            throw new ArgumentOutOfRangeException(nameof(progress), "Scan progress counts are inconsistent.");
        }

        SqliteRepositoryValues.ValidateRelativePath(progress.LastRelativePath);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_runs
            SET files_discovered = $discovered,
                files_processed = $processed,
                files_failed = $failed,
                last_relative_path = COALESCE($last_path, last_relative_path)
            WHERE id = $scan_run_id
              AND status = 'running'
              AND files_discovered <= $discovered
              AND files_processed <= $processed
              AND files_failed <= $failed
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$scan_run_id", scanRunId);
        command.Parameters.AddWithValue("$discovered", progress.FilesDiscovered);
        command.Parameters.AddWithValue("$processed", progress.FilesProcessed);
        command.Parameters.AddWithValue("$failed", progress.FilesFailed);
        command.Parameters.AddWithValue("$last_path", (object?)progress.LastRelativePath ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("The scan is not active or its progress cannot move backwards.");
        }
    }

    public async Task FinishAsync(
        long scanRunId,
        ScanStatus status,
        DateTimeOffset completedAt,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (scanRunId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scanRunId));
        }

        string timestamp = SqliteRepositoryValues.FormatTimestamp(completedAt);
        SqliteRepositoryValues.ValidateErrorMessage(errorMessage);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand finishCommand = connection.CreateCommand();
        finishCommand.Transaction = transaction;
        finishCommand.CommandText =
            """
            UPDATE scan_runs
            SET status = $status, completed_at = $completed_at, error_message = $error_message
            WHERE id = $scan_run_id AND status = 'running'
            RETURNING root_id, generation, files_discovered, files_processed, files_failed;
            """;
        finishCommand.Parameters.AddWithValue("$status", status.ToString().ToLowerInvariant());
        finishCommand.Parameters.AddWithValue("$completed_at", timestamp);
        finishCommand.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);
        finishCommand.Parameters.AddWithValue("$scan_run_id", scanRunId);
        await using SqliteDataReader reader = await finishCommand.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The scan is not active.");
        }

        long rootId = reader.GetInt64(0);
        long generation = reader.GetInt64(1);
        int filesDiscovered = reader.GetInt32(2);
        int filesProcessed = reader.GetInt32(3);
        int filesFailed = reader.GetInt32(4);
        await reader.DisposeAsync().ConfigureAwait(false);

        if (status == ScanStatus.Completed && filesFailed != 0)
        {
            throw new InvalidOperationException("A scan with failed files cannot be completed successfully.");
        }

        if (status == ScanStatus.Completed && filesProcessed != filesDiscovered)
        {
            throw new InvalidOperationException(
                "A scan with unprocessed discovered files cannot be completed successfully.");
        }

        if (status == ScanStatus.Completed)
        {
            await using SqliteCommand rootStateCommand = connection.CreateCommand();
            rootStateCommand.Transaction = transaction;
            rootStateCommand.CommandText =
                """
                SELECT 1 FROM index_roots
                WHERE id = $root_id AND enabled = 1 AND status = 'scanning';
                """;
            rootStateCommand.Parameters.AddWithValue("$root_id", rootId);
            if (await rootStateCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("The root changed state while the scan was running.");
            }

            await using SqliteCommand tombstoneCommand = connection.CreateCommand();
            tombstoneCommand.Transaction = transaction;
            tombstoneCommand.CommandText =
                """
                UPDATE file_entries
                SET is_deleted = 1, deleted_at = $completed_at, revision = revision + 1,
                    sha256 = NULL, last_indexed_at = NULL, extraction_status = 'pending',
                    indexing_status = 'pending', last_error = NULL
                WHERE root_id = $root_id
                  AND is_deleted = 0
                  AND last_seen_generation < $generation;
                """;
            tombstoneCommand.Parameters.AddWithValue("$completed_at", timestamp);
            tombstoneCommand.Parameters.AddWithValue("$root_id", rootId);
            tombstoneCommand.Parameters.AddWithValue("$generation", generation);
            await tombstoneCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand rootUpdateCommand = connection.CreateCommand();
        rootUpdateCommand.Transaction = transaction;
        rootUpdateCommand.CommandText =
            """
            UPDATE index_roots
            SET status = CASE $status
                    WHEN 'completed' THEN 'ready'
                    WHEN 'cancelled' THEN 'pending'
                    WHEN 'incomplete' THEN CASE WHEN last_scan_at IS NULL THEN 'pending' ELSE 'ready' END
                    ELSE 'error'
                END,
                updated_at = $completed_at,
                last_scan_at = CASE WHEN $status = 'completed' THEN $completed_at ELSE last_scan_at END,
                last_error = CASE WHEN $status = 'completed' THEN NULL ELSE $error_message END,
                index_revision = index_revision + 1
            WHERE id = $root_id AND enabled = 1 AND status = 'scanning';
            """;
        rootUpdateCommand.Parameters.AddWithValue("$status", status.ToString().ToLowerInvariant());
        rootUpdateCommand.Parameters.AddWithValue("$completed_at", timestamp);
        rootUpdateCommand.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);
        rootUpdateCommand.Parameters.AddWithValue("$root_id", rootId);
        await rootUpdateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RecoverInterruptedAsync(
        long rootId,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken = default)
    {
        if (rootId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rootId));
        }

        string timestamp = SqliteRepositoryValues.FormatTimestamp(recoveredAt);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using SqliteCommand recoverCommand = connection.CreateCommand();
        recoverCommand.Transaction = transaction;
        recoverCommand.CommandText =
            """
            UPDATE scan_runs
            SET status = 'incomplete', completed_at = $recovered_at, error_message = 'interrupted'
            WHERE root_id = $root_id AND status = 'running'
            RETURNING id;
            """;
        recoverCommand.Parameters.AddWithValue("$recovered_at", timestamp);
        recoverCommand.Parameters.AddWithValue("$root_id", rootId);
        bool recovered = await recoverCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (recovered)
        {
            await using SqliteCommand rootCommand = connection.CreateCommand();
            rootCommand.Transaction = transaction;
            rootCommand.CommandText =
                """
                UPDATE index_roots
                SET status = CASE WHEN last_scan_at IS NULL THEN 'pending' ELSE 'ready' END,
                    updated_at = $recovered_at, last_error = 'interrupted', index_revision = index_revision + 1
                WHERE id = $root_id AND enabled = 1 AND status = 'scanning';
                """;
            rootCommand.Parameters.AddWithValue("$recovered_at", timestamp);
            rootCommand.Parameters.AddWithValue("$root_id", rootId);
            await rootCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovered;
    }
}
