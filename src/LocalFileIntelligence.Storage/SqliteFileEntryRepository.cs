using System.Globalization;
using System.IO;
using LocalFileIntelligence.Application.Persistence;
using LocalFileIntelligence.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.Storage;

public sealed class SqliteFileEntryRepository : IFileEntryRepository
{
    private const int MaximumChildren = 500;
    private readonly SqliteConnectionFactory _connectionFactory;

    private const string SelectColumns =
        """
        id, root_id, relative_path, path_key, parent_path_key, name, extension, entry_type, size_bytes,
        created_at_fs, modified_at_fs, last_seen_at, last_indexed_at, sha256, extraction_status,
        indexing_status, is_deleted, deleted_at, volume_file_identity, revision, last_seen_generation
        """;

    public SqliteFileEntryRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<FileEntry?> GetByIdAsync(
        long rootId,
        long id,
        CancellationToken cancellationToken = default)
    {
        ValidateId(rootId, nameof(rootId));
        ValidateId(id, nameof(id));
        await using SqliteConnectionLease lease = await _connectionFactory.OpenReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await GetByIdAsync(lease.Connection, transaction: null, rootId, id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<FileEntry?> GetByPathAsync(
        long rootId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ValidateId(rootId, nameof(rootId));
        cancellationToken.ThrowIfCancellationRequested();
        string pathKey = IndexEntryPath.NormalizeKey(relativePath);
        await using SqliteConnectionLease lease = await _connectionFactory.OpenReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await GetByPathKeyAsync(lease.Connection, transaction: null, rootId, pathKey, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileEntry>> ListChildrenAsync(
        long rootId,
        string parentRelativePath,
        string? afterRelativePath,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateId(rootId, nameof(rootId));
        if (limit is < 1 or > MaximumChildren)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"The child page size must be between 1 and {MaximumChildren}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string parentPathKey = IndexEntryPath.NormalizeParentKey(parentRelativePath);
        string? afterPathKey = afterRelativePath is null ? null : IndexEntryPath.NormalizeKey(afterRelativePath);
        string continuationPredicate = afterPathKey is null ? string.Empty : "AND path_key > $after_path_key";

        await using SqliteConnectionLease lease = await _connectionFactory.OpenReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {SelectColumns}
            FROM file_entries
            WHERE root_id = $root_id
              AND parent_path_key = $parent_path_key
              AND is_deleted = 0
              {continuationPredicate}
            ORDER BY path_key
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$root_id", rootId);
        command.Parameters.AddWithValue("$parent_path_key", parentPathKey);
        if (afterPathKey is not null)
        {
            command.Parameters.AddWithValue("$after_path_key", afterPathKey);
        }

        command.Parameters.AddWithValue("$limit", limit);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<FileEntry> entries = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    public async Task<FileEntry> UpsertAsync(
        long rootId,
        FileEntryObservation observation,
        CancellationToken cancellationToken = default)
    {
        ValidateId(rootId, nameof(rootId));
        cancellationToken.ThrowIfCancellationRequested();
        NormalizedObservation normalized = NormalizeObservation(observation);

        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await EnsureRootExistsAsync(connection, transaction, rootId, cancellationToken).ConfigureAwait(false);

        FileEntry? existing = await GetByPathKeyAsync(
                connection,
                transaction,
                rootId,
                normalized.PathKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            long id = await InsertAsync(connection, transaction, rootId, normalized, cancellationToken)
                .ConfigureAwait(false);
            await BumpRootRevisionAsync(connection, transaction, rootId, cancellationToken).ConfigureAwait(false);
            FileEntry added = await GetByIdAsync(connection, transaction, rootId, id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The inserted file entry could not be read back.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return added;
        }

        FileEntry updated = await ApplyObservationAsync(
                connection,
                transaction,
                rootId,
                existing,
                normalized,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<FileEntry?> RenameAsync(
        long rootId,
        string oldRelativePath,
        FileEntryObservation observation,
        CancellationToken cancellationToken = default)
    {
        ValidateId(rootId, nameof(rootId));
        cancellationToken.ThrowIfCancellationRequested();
        string oldPathKey = IndexEntryPath.NormalizeKey(oldRelativePath);
        NormalizedObservation normalized = NormalizeObservation(observation);

        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await EnsureRootExistsAsync(connection, transaction, rootId, cancellationToken).ConfigureAwait(false);

        FileEntry? existing = await GetByPathKeyAsync(
                connection,
                transaction,
                rootId,
                oldPathKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is null || existing.IsDeleted)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (existing.EntryType == IndexEntryType.Directory
            && !string.Equals(existing.PathKey, normalized.PathKey, StringComparison.Ordinal)
            && await HasDescendantsAsync(connection, transaction, rootId, existing.PathKey, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("Renaming a populated directory is not supported.");
        }

        FileEntry renamed = await ApplyObservationAsync(
                connection,
                transaction,
                rootId,
                existing,
                normalized,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return renamed;
    }

    public async Task<FileEntry?> TombstoneAsync(
        long rootId,
        string relativePath,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateId(rootId, nameof(rootId));
        cancellationToken.ThrowIfCancellationRequested();
        string pathKey = IndexEntryPath.NormalizeKey(relativePath);
        string timestamp = UtcTimestamp.Format(deletedAt);

        await using SqliteConnectionLease lease = await _connectionFactory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await EnsureRootExistsAsync(connection, transaction, rootId, cancellationToken).ConfigureAwait(false);

        FileEntry? existing = await GetByPathKeyAsync(connection, transaction, rootId, pathKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null || existing.IsDeleted)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE file_entries
            SET is_deleted = 1, deleted_at = $deleted_at, revision = revision + 1,
                sha256 = NULL, last_indexed_at = NULL, extraction_status = 'pending',
                indexing_status = 'pending', last_error = NULL
            WHERE root_id = $root_id AND id = $id AND is_deleted = 0;
            """;
        update.Parameters.AddWithValue("$deleted_at", timestamp);
        update.Parameters.AddWithValue("$root_id", rootId);
        update.Parameters.AddWithValue("$id", existing.Id);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await BumpRootRevisionAsync(connection, transaction, rootId, cancellationToken).ConfigureAwait(false);

        FileEntry tombstone = await GetByIdAsync(connection, transaction, rootId, existing.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The tombstoned file entry could not be read back.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return tombstone;
    }

    private static async Task<bool> HasDescendantsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        string pathKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM file_entries
                WHERE root_id = $root_id
                  AND path_key >= ($path_key || '\')
                  AND path_key < ($path_key || ']')
            );
            """;
        command.Parameters.AddWithValue("$root_id", rootId);
        command.Parameters.AddWithValue("$path_key", pathKey);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<FileEntry> ApplyObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        FileEntry existing,
        NormalizedObservation observation,
        CancellationToken cancellationToken)
    {
        bool changed = existing.IsDeleted
            || !string.Equals(existing.RelativePath, observation.RelativePath, StringComparison.Ordinal)
            || existing.EntryType != observation.EntryType
            || existing.SizeBytes != observation.SizeBytes
            || UtcTimestamp.Format(existing.CreatedAtFs) != observation.CreatedAtFs
            || UtcTimestamp.Format(existing.ModifiedAtFs) != observation.ModifiedAtFs
            || !string.Equals(existing.VolumeFileIdentity, observation.VolumeFileIdentity, StringComparison.Ordinal);
        bool seenChanged = UtcTimestamp.Format(existing.LastSeenAt) != observation.LastSeenAt
            || existing.LastSeenGeneration != observation.LastSeenGeneration;
        if (!changed && !seenChanged)
        {
            return existing;
        }

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = changed
            ? """
              UPDATE file_entries
              SET relative_path = $relative_path, path_key = $path_key, parent_path_key = $parent_path_key,
                  name = $name, extension = $extension, entry_type = $entry_type, size_bytes = $size_bytes,
                  created_at_fs = $created_at_fs, modified_at_fs = $modified_at_fs, last_seen_at = $last_seen_at,
                  volume_file_identity = $volume_file_identity, last_seen_generation = $last_seen_generation,
                  revision = revision + 1, sha256 = NULL, last_indexed_at = NULL,
                  extraction_status = 'pending', indexing_status = 'pending', is_deleted = 0,
                  deleted_at = NULL, last_error = NULL
              WHERE root_id = $root_id AND id = $id;
              """
            : """
              UPDATE file_entries
              SET last_seen_at = $last_seen_at, last_seen_generation = $last_seen_generation
              WHERE root_id = $root_id AND id = $id;
              """;
        AddObservationParameters(update, rootId, observation);
        update.Parameters.AddWithValue("$id", existing.Id);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (changed || seenChanged)
        {
            await BumpRootRevisionAsync(connection, transaction, rootId, cancellationToken).ConfigureAwait(false);
        }

        return await GetByIdAsync(connection, transaction, rootId, existing.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The updated file entry could not be read back.");
    }

    private static async Task<long> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        NormalizedObservation observation,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO file_entries (
                root_id, relative_path, path_key, parent_path_key, name, extension, entry_type, size_bytes,
                created_at_fs, modified_at_fs, last_seen_at, volume_file_identity, extraction_status,
                indexing_status, last_seen_generation)
            VALUES (
                $root_id, $relative_path, $path_key, $parent_path_key, $name, $extension, $entry_type, $size_bytes,
                $created_at_fs, $modified_at_fs, $last_seen_at, $volume_file_identity, 'pending', 'pending',
                $last_seen_generation);
            SELECT last_insert_rowid();
            """;
        AddObservationParameters(insert, rootId, observation);
        return Convert.ToInt64(
            await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<FileEntry?> GetByPathKeyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long rootId,
        string pathKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM file_entries WHERE root_id = $root_id AND path_key = $path_key;";
        command.Parameters.AddWithValue("$root_id", rootId);
        command.Parameters.AddWithValue("$path_key", pathKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    private static async Task<FileEntry?> GetByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long rootId,
        long id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM file_entries WHERE root_id = $root_id AND id = $id;";
        command.Parameters.AddWithValue("$root_id", rootId);
        command.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    private static async Task EnsureRootExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM index_roots WHERE id = $root_id;";
        command.Parameters.AddWithValue("$root_id", rootId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("The index root does not exist.");
        }
    }

    private static async Task BumpRootRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE index_roots SET index_revision = index_revision + 1 WHERE id = $root_id;";
        command.Parameters.AddWithValue("$root_id", rootId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The index root disappeared during a file-entry transaction.");
        }
    }

    private static NormalizedObservation NormalizeObservation(FileEntryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(observation.EntryType))
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "The entry type is invalid.");
        }

        if (observation.SizeBytes < 0 || (observation.EntryType == IndexEntryType.Directory && observation.SizeBytes != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "A file size must be non-negative and a directory size must be zero.");
        }

        if (observation.LastSeenGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "A scan generation cannot be negative.");
        }

        if (observation.VolumeFileIdentity is { } identity)
        {
            ValidateText(identity, nameof(observation.VolumeFileIdentity), 1024);
        }

        string relativePath = IndexEntryPath.Normalize(observation.RelativePath);
        int separator = relativePath.LastIndexOf('\\');
        string name = relativePath[(separator + 1)..];
        string parentPathKey = separator < 0
            ? string.Empty
            : IndexEntryPath.NormalizeKey(relativePath[..separator]);
        string extension = observation.EntryType == IndexEntryType.File
            ? Path.GetExtension(name).ToLowerInvariant()
            : string.Empty;

        return new(
            relativePath,
            IndexEntryPath.NormalizeKey(relativePath),
            parentPathKey,
            name,
            extension,
            observation.EntryType,
            observation.SizeBytes,
            UtcTimestamp.Format(observation.CreatedAtFs),
            UtcTimestamp.Format(observation.ModifiedAtFs),
            UtcTimestamp.Format(observation.LastSeenAt),
            observation.VolumeFileIdentity,
            observation.LastSeenGeneration);
    }

    private static void AddObservationParameters(
        SqliteCommand command,
        long rootId,
        NormalizedObservation observation)
    {
        command.Parameters.AddWithValue("$root_id", rootId);
        command.Parameters.AddWithValue("$relative_path", observation.RelativePath);
        command.Parameters.AddWithValue("$path_key", observation.PathKey);
        command.Parameters.AddWithValue("$parent_path_key", observation.ParentPathKey);
        command.Parameters.AddWithValue("$name", observation.Name);
        command.Parameters.AddWithValue("$extension", observation.Extension);
        command.Parameters.AddWithValue("$entry_type", ToDatabaseEntryType(observation.EntryType));
        command.Parameters.AddWithValue("$size_bytes", observation.SizeBytes);
        command.Parameters.AddWithValue("$created_at_fs", observation.CreatedAtFs);
        command.Parameters.AddWithValue("$modified_at_fs", observation.ModifiedAtFs);
        command.Parameters.AddWithValue("$last_seen_at", observation.LastSeenAt);
        command.Parameters.AddWithValue(
            "$volume_file_identity",
            (object?)observation.VolumeFileIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_seen_generation", observation.LastSeenGeneration);
    }

    private static FileEntry ReadEntry(SqliteDataReader reader)
    {
        return new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ParseEntryType(reader.GetString(7)),
            reader.GetInt64(8),
            UtcTimestamp.Parse(reader.GetString(9)),
            UtcTimestamp.Parse(reader.GetString(10)),
            UtcTimestamp.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : UtcTimestamp.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            Enum.Parse<FileEntryExtractionStatus>(reader.GetString(14), ignoreCase: true),
            Enum.Parse<FileEntryIndexingStatus>(reader.GetString(15), ignoreCase: true),
            reader.GetInt32(16) != 0,
            reader.IsDBNull(17) ? null : UtcTimestamp.Parse(reader.GetString(17)),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.GetInt64(19),
            reader.GetInt64(20));
    }

    private static IndexEntryType ParseEntryType(string value)
    {
        return value switch
        {
            "file" => IndexEntryType.File,
            "directory" => IndexEntryType.Directory,
            _ => throw new InvalidOperationException("The database contains an unknown file-entry type."),
        };
    }

    private static string ToDatabaseEntryType(IndexEntryType value)
    {
        return value switch
        {
            IndexEntryType.File => "file",
            IndexEntryType.Directory => "directory",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static void ValidateId(long id, string parameterName)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "An ID must be positive.");
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
    }

    private sealed record NormalizedObservation(
        string RelativePath,
        string PathKey,
        string ParentPathKey,
        string Name,
        string Extension,
        IndexEntryType EntryType,
        long SizeBytes,
        string CreatedAtFs,
        string ModifiedAtFs,
        string LastSeenAt,
        string? VolumeFileIdentity,
        long LastSeenGeneration);
}
