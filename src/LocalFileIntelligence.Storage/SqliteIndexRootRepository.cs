using System.Buffers;
using System.Text;
using LocalFileIntelligence.Application.Persistence;
using LocalFileIntelligence.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.Storage;

public sealed class SqliteIndexRootRepository : IIndexRootRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    private const string SelectColumns =
        """
        id, path, path_key, display_name, enabled, status, volume_identity, directory_identity,
        policy_revision, index_revision, created_at, updated_at, last_scan_at, last_error
        """;

    public SqliteIndexRootRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IndexRoot> AddAsync(
        IndexRootRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        string path = SelectedFolderPath.Normalize(registration.Path);
        string pathKey = SelectedFolderPath.NormalizeKey(registration.Path);
        ValidateText(registration.DisplayName, nameof(registration.DisplayName), 256);
        ValidateText(registration.VolumeIdentity, nameof(registration.VolumeIdentity), 1024);
        ValidateText(registration.DirectoryIdentity, nameof(registration.DirectoryIdentity), 1024);
        string createdAt = UtcTimestamp.Format(registration.CreatedAt);

        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        IndexRoot? existing = await GetByPathKeyAsync(connection, transaction, pathKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.VolumeIdentity, registration.VolumeIdentity, StringComparison.Ordinal)
                || !string.Equals(existing.DirectoryIdentity, registration.DirectoryIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The normalized root path is already associated with another directory identity.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await CheckSelectionAsync(connection, transaction, pathKey, registration, cancellationToken).ConfigureAwait(false);
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO index_roots (
                path, path_key, display_name, enabled, status, volume_identity, directory_identity,
                policy_revision, index_revision, created_at, updated_at)
            VALUES (
                $path, $path_key, $display_name, 0, 'pending', $volume_identity, $directory_identity,
                1, 0, $created_at, $created_at);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$path", path);
        insert.Parameters.AddWithValue("$path_key", pathKey);
        insert.Parameters.AddWithValue("$display_name", registration.DisplayName);
        insert.Parameters.AddWithValue("$volume_identity", registration.VolumeIdentity);
        insert.Parameters.AddWithValue("$directory_identity", registration.DirectoryIdentity);
        insert.Parameters.AddWithValue("$created_at", createdAt);
        long id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        IndexRoot root = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The inserted root could not be read back.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return root;
    }

    public async Task<IndexRoot?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "A root ID must be positive.");
        }

        await using SqliteConnectionLease lease = await _connectionFactory.OpenReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await GetByIdAsync(lease.Connection, transaction: null, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexRoot>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnectionLease lease = await _connectionFactory.OpenReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM index_roots ORDER BY id;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<IndexRoot> roots = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roots.Add(ReadRoot(reader));
        }

        return roots;
    }

    public async Task<IndexRoot?> UpdateDetailsAsync(
        long id,
        string displayName,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ValidateText(displayName, nameof(displayName), 256);
        cancellationToken.ThrowIfCancellationRequested();
        string timestamp = UtcTimestamp.Format(updatedAt);

        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        IndexRoot? current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(current.DisplayName, displayName, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE index_roots
            SET display_name = $display_name, updated_at = $updated_at, index_revision = index_revision + 1
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$display_name", displayName);
        update.Parameters.AddWithValue("$updated_at", timestamp);
        update.Parameters.AddWithValue("$id", id);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        IndexRoot root = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The updated root could not be read back.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return root;
    }

    public async Task<IndexRoot?> UpdateStateAsync(
        long id,
        bool enabled,
        IndexRootStatus status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        string databaseStatus = ToDatabaseStatus(status);
        cancellationToken.ThrowIfCancellationRequested();
        string timestamp = UtcTimestamp.Format(updatedAt);

        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        IndexRoot? current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (current.Enabled == enabled && current.Status == status)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE index_roots
            SET enabled = $enabled, status = $status, updated_at = $updated_at,
                policy_revision = policy_revision + CASE WHEN enabled <> $enabled THEN 1 ELSE 0 END,
                index_revision = index_revision + 1
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        update.Parameters.AddWithValue("$status", databaseStatus);
        update.Parameters.AddWithValue("$updated_at", timestamp);
        update.Parameters.AddWithValue("$id", id);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (status == IndexRootStatus.Paused)
        {
            await using SqliteCommand cancelJobs = connection.CreateCommand();
            cancelJobs.Transaction = transaction;
            cancelJobs.CommandText =
                """
                UPDATE index_jobs
                SET status = 'cancelled', completed_at = $updated_at, lease_owner = NULL, lease_until = NULL
                WHERE root_id = $root_id AND status IN ('queued', 'running');
                """;
            cancelJobs.Parameters.AddWithValue("$root_id", id);
            cancelJobs.Parameters.AddWithValue("$updated_at", timestamp);
            await cancelJobs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        IndexRoot root = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The updated root could not be read back.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return root;
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand delete = lease.Connection.CreateCommand();
        delete.CommandText = "DELETE FROM index_roots WHERE id = $id;";
        delete.Parameters.AddWithValue("$id", id);
        return await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<IndexRoot?> GetByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM index_roots WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRoot(reader) : null;
    }

    private static async Task<IndexRoot?> GetByPathKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string pathKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM index_roots WHERE path_key = $path_key;";
        command.Parameters.AddWithValue("$path_key", pathKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRoot(reader) : null;
    }

    private static async Task CheckSelectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string pathKey, IndexRootRegistration registration, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT path_key, volume_identity, directory_identity FROM index_roots;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string existingKey = reader.GetString(0);
            bool sameVolume = reader.GetString(1) == registration.VolumeIdentity;
            if (SelectedFolderPath.Overlaps(existingKey, pathKey)
                || (sameVolume && (reader.GetString(2) == registration.DirectoryIdentity
                    || SelectedFolderPath.Overlaps(existingKey[2..], pathKey[2..]))))
            {
                throw new InvalidOperationException("The selected directory overlaps an existing root.");
            }

            if (++count == IndexingConfiguration.MaximumSelectedFolders)
            {
                throw new InvalidOperationException($"At most {IndexingConfiguration.MaximumSelectedFolders} folders may be registered.");
            }
        }
    }

    private static IndexRoot ReadRoot(SqliteDataReader reader)
    {
        return new IndexRoot(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) == 1,
            ParseDatabaseStatus(reader.GetString(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            UtcTimestamp.Parse(reader.GetString(10)),
            UtcTimestamp.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : UtcTimestamp.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private static IndexRootStatus ParseDatabaseStatus(string status)
    {
        return status switch
        {
            "pending" => IndexRootStatus.Pending,
            "scanning" => IndexRootStatus.Scanning,
            "ready" => IndexRootStatus.Ready,
            "paused" => IndexRootStatus.Paused,
            "offline" => IndexRootStatus.Offline,
            "access_denied" => IndexRootStatus.AccessDenied,
            "error" => IndexRootStatus.Error,
            _ => throw new InvalidDataException("The database contains an unsupported root status."),
        };
    }

    private static string ToDatabaseStatus(IndexRootStatus status)
    {
        return status switch
        {
            IndexRootStatus.Pending => "pending",
            IndexRootStatus.Scanning => "scanning",
            IndexRootStatus.Ready => "ready",
            IndexRootStatus.Paused => "paused",
            IndexRootStatus.Offline => "offline",
            IndexRootStatus.AccessDenied => "access_denied",
            IndexRootStatus.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(status), "The root status is not supported."),
        };
    }

    private static void ValidateId(long id)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "A root ID must be positive.");
        }
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException($"The value must contain 1-{maximumLength} non-control characters.", parameterName);
        }

        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out int charactersConsumed) != OperationStatus.Done)
            {
                throw new ArgumentException("The value must contain valid Unicode.", parameterName);
            }

            remaining = remaining[charactersConsumed..];
        }
    }
}
