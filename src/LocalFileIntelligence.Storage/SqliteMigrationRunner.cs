using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.Storage;

public static class SqliteMigrationRunner
{
    private const string LedgerTable = "schema_migrations";
    private const string LedgerSchema =
        """
        CREATE TABLE schema_migrations (
            version INTEGER PRIMARY KEY CHECK (version > 0),
            name TEXT NOT NULL UNIQUE,
            sha256 TEXT NOT NULL CHECK (length(sha256) = 64),
            applied_at TEXT NOT NULL CHECK (applied_at GLOB '????-??-??T??:??:??.???Z')
        ) STRICT;
        """;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MigrationGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static Task ApplyAsync(
        SqliteConnectionFactory factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return ApplyAsync(factory, LoadMigrations(), cancellationToken);
    }

    internal static async Task ApplyAsync(
        SqliteConnectionFactory factory,
        IReadOnlyList<SqliteMigrationDefinition> migrations,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? afterMigrationSql = null,
        Func<CancellationToken, Task>? afterBackupStep = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ValidateMigrations(migrations);

        using MigrationLease migrationLease = AcquireMigrationLease(factory, cancellationToken);
        await using SqliteConnectionLease lease = await factory.OpenWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnection connection = lease.Connection;
        await ValidateIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        bool ledgerExists = await LedgerExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!ledgerExists)
        {
            long objectCount = await GetUserObjectCountAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (objectCount != 0)
            {
                throw new InvalidOperationException(
                    "The database contains objects but has no migration ledger; refusing to modify it.");
            }

            await ExecuteNonQueryAsync(connection, transaction, LedgerSchema, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ValidateLedgerSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        List<AppliedMigration> applied = await ReadAppliedMigrationsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        ValidateAppliedMigrations(applied, migrations);
        if (applied.Count == 0
            && await GetUnmigratedObjectCountAsync(connection, transaction, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidOperationException(
                "The database contains schema objects but no applied migration entries; refusing to modify it.");
        }

        List<PendingMigration> pending = migrations
            .Skip(applied.Count)
            .Select(migration => new PendingMigration(migration, GetChecksum(migration.Sql)))
            .ToList();
        if (pending.Count > 0 && applied.Count > 0)
        {
            await CreatePreUpgradeBackupAsync(
                factory,
                applied[^1].Version,
                pending[^1].Definition.Version,
                cancellationToken,
                afterBackupStep).ConfigureAwait(false);
        }

        foreach (PendingMigration migration in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteNonQueryAsync(connection, transaction, migration.Definition.Sql, cancellationToken)
                .ConfigureAwait(false);
            if (afterMigrationSql is not null)
            {
                await afterMigrationSql(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO schema_migrations (version, name, sha256, applied_at)
                VALUES ($version, $name, $sha256, $applied_at);
                """;
            insert.Parameters.AddWithValue("$version", migration.Definition.Version);
            insert.Parameters.AddWithValue("$name", migration.Definition.Name);
            insert.Parameters.AddWithValue("$sha256", migration.Checksum);
            insert.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MigrationLease AcquireMigrationLease(
        SqliteConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemaphoreSlim gate = MigrationGates.GetOrAdd(factory.DatabasePath, static _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            throw new InvalidOperationException("A migration is already in progress for this database.");
        }

        return new MigrationLease(gate);
    }

    private static IReadOnlyList<SqliteMigrationDefinition> LoadMigrations()
    {
        const string resourceName = "LocalFileIntelligence.Storage.Migrations.0001_initial_schema.sql";
        using Stream stream = typeof(SqliteMigrationRunner).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' was not found.");
        using StreamReader reader = new(stream, Utf8, detectEncodingFromByteOrderMarks: false);
        string sql = reader.ReadToEnd();
        return [new SqliteMigrationDefinition(1, "initial_schema", sql)];
    }

    private static void ValidateMigrations(IReadOnlyList<SqliteMigrationDefinition> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        if (migrations.Count == 0)
        {
            throw new ArgumentException("At least one migration is required.", nameof(migrations));
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < migrations.Count; index++)
        {
            SqliteMigrationDefinition migration = migrations[index];
            if (migration.Version != index + 1
                || string.IsNullOrWhiteSpace(migration.Name)
                || string.IsNullOrWhiteSpace(migration.Sql)
                || !names.Add(migration.Name))
            {
                throw new ArgumentException(
                    "Migrations must have unique names, non-empty SQL, and contiguous versions starting at one.",
                    nameof(migrations));
            }
        }
    }

    private static async Task ValidateIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        int rows = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The SQLite database failed its integrity check.");
            }
        }

        if (rows != 1)
        {
            throw new InvalidOperationException("The SQLite database returned an invalid integrity-check result.");
        }
    }

    private static async Task<bool> LedgerExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", LedgerTable);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task<long> GetUnmigratedObjectCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT count(*) FROM sqlite_master
            WHERE substr(name, 1, 7) <> 'sqlite_' AND name <> $ledger
              AND type IN ('table', 'index', 'view', 'trigger');
            """;
        command.Parameters.AddWithValue("$ledger", LedgerTable);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<long> GetUserObjectCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT count(*) FROM sqlite_master
            WHERE substr(name, 1, 7) <> 'sqlite_' AND type IN ('table', 'index', 'view', 'trigger');
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task ValidateLedgerSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(schema_migrations);";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        (string Name, string Type, long NotNull, long PrimaryKey)[] expected =
        [
            ("version", "INTEGER", 0, 1),
            ("name", "TEXT", 1, 0),
            ("sha256", "TEXT", 1, 0),
            ("applied_at", "TEXT", 1, 0),
        ];
        int index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (index >= expected.Length
                || !string.Equals(reader.GetString(1), expected[index].Name, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), expected[index].Type, StringComparison.Ordinal)
                || reader.GetInt64(3) != expected[index].NotNull
                || reader.GetInt64(5) != expected[index].PrimaryKey)
            {
                throw new InvalidOperationException("The migration ledger has an unsupported schema.");
            }

            index++;
        }

        if (index != expected.Length)
        {
            throw new InvalidOperationException("The migration ledger has an unsupported schema.");
        }
    }

    private static async Task<List<AppliedMigration>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, name, sha256 FROM schema_migrations ORDER BY version;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<AppliedMigration> migrations = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            migrations.Add(new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return migrations;
    }

    private static void ValidateAppliedMigrations(
        IReadOnlyList<AppliedMigration> applied,
        IReadOnlyList<SqliteMigrationDefinition> migrations)
    {
        if (applied.Count > migrations.Count || applied.Any(migration => migration.Version > migrations.Count))
        {
            throw new InvalidOperationException("The database schema is newer than this application supports.");
        }

        for (int index = 0; index < applied.Count; index++)
        {
            AppliedMigration existing = applied[index];
            SqliteMigrationDefinition expected = migrations[index];
            if (existing.Version != index + 1)
            {
                throw new InvalidOperationException("The migration ledger contains a version gap.");
            }

            if (!string.Equals(existing.Name, expected.Name, StringComparison.Ordinal)
                || !string.Equals(existing.Checksum, GetChecksum(expected.Sql), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Migration {existing.Version} does not match its stored checksum.");
            }
        }
    }

    private static async Task CreatePreUpgradeBackupAsync(
        SqliteConnectionFactory factory,
        int fromVersion,
        int toVersion,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterBackupStep = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string backupPath = $"{factory.DatabasePath}.pre-upgrade-{fromVersion}-to-{toVersion}-{Guid.NewGuid():N}.bak";
        await using SqliteConnectionLease reader = await factory.OpenReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteConnectionStringBuilder destinationString = new()
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        try
        {
            CreateRestrictedBackupFile(backupPath);
            await using SqliteConnection destination = new(destinationString.ToString());
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            await CopyDatabaseInStepsAsync(reader.Connection, destination, cancellationToken, afterBackupStep)
                .ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            throw;
        }
    }

    private static void CreateRestrictedBackupFile(string backupPath)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateRestrictedWindowsBackupFile(backupPath);
            return;
        }

        using FileStream file = new(backupPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CreateRestrictedWindowsBackupFile(string backupPath)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User
            ?? throw new UnauthorizedAccessException("The current Windows owner SID could not be determined.");
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        FileSecurity security = new();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        using (FileStream file = new(backupPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
        {
            new FileInfo(backupPath).SetAccessControl(security);
        }

        FileSecurity actualSecurity = new FileInfo(backupPath).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        SqliteStorageGuard.VerifyRestrictedAccess(actualSecurity, owner, identity.Owner);
    }

    private static async Task CopyDatabaseInStepsAsync(
        SqliteConnection source,
        SqliteConnection destination,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterBackupStep)
    {
        const int pagesPerStep = 128;
        const int maxBusyRetries = 50;
        const int busyRetryDelayMilliseconds = 100;
        const int sqliteOk = 0;
        const int sqliteBusy = 5;
        const int sqliteLocked = 6;
        const int sqliteDone = 101;
        SQLitePCL.sqlite3_backup backup = SQLitePCL.raw.sqlite3_backup_init(
            destination.Handle,
            "main",
            source.Handle,
            "main")
            ?? throw new InvalidOperationException("SQLite could not initialize the pre-upgrade backup.");
        try
        {
            int busyRetries = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int result = SQLitePCL.raw.sqlite3_backup_step(backup, pagesPerStep);
                if (result == sqliteDone)
                {
                    return;
                }

                if (result == sqliteOk)
                {
                    busyRetries = 0;
                    if (afterBackupStep is not null)
                    {
                        await afterBackupStep(cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (result is sqliteBusy or sqliteLocked && busyRetries++ < maxBusyRetries)
                {
                    await Task.Delay(busyRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                SqliteException.ThrowExceptionForRC(result, destination.Handle);
            }
        }
        finally
        {
            SQLitePCL.raw.sqlite3_backup_finish(backup);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetChecksum(string sql)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(sql)));
    }

    internal sealed record SqliteMigrationDefinition(int Version, string Name, string Sql);

    private sealed record AppliedMigration(int Version, string Name, string Checksum);

    private sealed record PendingMigration(SqliteMigrationDefinition Definition, string Checksum);

    private sealed class MigrationLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
