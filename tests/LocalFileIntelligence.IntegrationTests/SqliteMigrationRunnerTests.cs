using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using LocalFileIntelligence.Storage;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.IntegrationTests;

public sealed class SqliteMigrationRunnerTests
{
    [Fact]
    public async Task AppliesInitialSchemaAndIsIdempotent()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);

        await SqliteMigrationRunner.ApplyAsync(factory);
        await SqliteMigrationRunner.ApplyAsync(factory);

        await using SqliteConnectionLease lease = await factory.OpenReaderAsync();
        await using SqliteConnection snapshot = new("Data Source=:memory:");
        await snapshot.OpenAsync();
        await ExecuteAsync(
            snapshot,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema.sql")));
        Assert.Equal(1, await ScalarAsync<long>(lease.Connection, "SELECT count(*) FROM schema_migrations;"));
        Assert.Equal(await GetObjectNamesAsync(snapshot), await GetObjectNamesAsync(lease.Connection));
    }

    [Fact]
    public async Task MigratedSchemaEnforcesConstraintsAndFtsTriggers()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await SqliteMigrationRunner.ApplyAsync(factory);
        await using SqliteConnectionLease writer = await factory.OpenWriterAsync();
        await ExecuteAsync(
            writer.Connection,
            """
            INSERT INTO index_roots (id, path, path_key, display_name, enabled, status, volume_identity,
                                     directory_identity, created_at, updated_at)
            VALUES (1, 'C:\Synthetic', 'C:\SYNTHETIC', 'Synthetic root', 1, 'ready', 'volume-1', 'directory-1',
                    '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');
            INSERT INTO file_entries (id, root_id, relative_path, path_key, parent_path_key, name, extension,
                                      entry_type, size_bytes, created_at_fs, modified_at_fs, last_seen_at,
                                      extraction_status, indexing_status)
            VALUES (1, 1, 'notes.txt', 'NOTES.TXT', '', 'notes.txt', '.txt', 'file', 17,
                    '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                    'extracted', 'indexed');
            INSERT INTO file_contents
            VALUES (1, 1, 'hello local world', 'plain-text', '1', '2026-01-01T00:00:00.000Z', 17);
            """);

        Assert.Equal(1, await FtsMatchesAsync(writer.Connection, "hello"));
        await ExecuteAsync(writer.Connection, "UPDATE file_contents SET extracted_text = 'updated local world';");
        Assert.Equal(0, await FtsMatchesAsync(writer.Connection, "hello"));
        Assert.Equal(1, await FtsMatchesAsync(writer.Connection, "updated"));

        SqliteException constraint = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                writer.Connection,
                """
                INSERT INTO file_entries (root_id, relative_path, path_key, parent_path_key, name, extension,
                                          entry_type, size_bytes, created_at_fs, modified_at_fs, last_seen_at,
                                          extraction_status, indexing_status)
                VALUES (1, 'invalid.txt', 'INVALID.TXT', '', 'invalid.txt', '.txt', 'file', -1,
                        '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z',
                        'pending', 'pending');
                """));
        Assert.Equal(19, constraint.SqliteErrorCode);

        await ExecuteAsync(writer.Connection, "DELETE FROM index_roots WHERE id = 1;");
        Assert.Equal(0, await FtsMatchesAsync(writer.Connection, "updated"));
    }

    [Fact]
    public void InitialMigrationChecksumIsImmutable()
    {
        const string resourceName = "LocalFileIntelligence.Storage.Migrations.0001_initial_schema.sql";
        using Stream migration = typeof(SqliteMigrationRunner).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' was not found.");

        Assert.Equal(
            "002e7056d0b1dce8eeb2b0c5d4160bc49285ee6228b141d584c8c5b8b34aba0b",
            Convert.ToHexStringLower(SHA256.HashData(migration)));
    }

    [Fact]
    public async Task UpgradeCreatesBackupWithPreUpgradeContents()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        SqliteMigrationRunner.SqliteMigrationDefinition[] initial = [Migration(1, "one", "CREATE TABLE data_v1 (value TEXT);")];
        SqliteMigrationRunner.SqliteMigrationDefinition[] upgraded =
        [
            initial[0],
            Migration(2, "two", "CREATE TABLE data_v2 (value TEXT);"),
        ];

        await SqliteMigrationRunner.ApplyAsync(factory, initial);
        await using (SqliteConnectionLease writer = await factory.OpenWriterAsync())
        {
            await ExecuteAsync(writer.Connection, "INSERT INTO data_v1 VALUES ('before-upgrade');");
        }

        await SqliteMigrationRunner.ApplyAsync(factory, upgraded);

        string backupPath = Assert.Single(Directory.GetFiles(directory.Path, "index.db.pre-upgrade-1-to-2-*.bak"));
        await using SqliteConnection backup = new(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await backup.OpenAsync();
        Assert.Equal("before-upgrade", await ScalarAsync<string>(backup, "SELECT value FROM data_v1;"));
        Assert.Equal(1, await ScalarAsync<long>(backup, "SELECT count(*) FROM schema_migrations;"));
        Assert.Equal(0, await ScalarAsync<long>(backup, "SELECT count(*) FROM sqlite_master WHERE name = 'data_v2';"));
    }

    [SqliteConnectionFactoryTests.WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task UpgradeBackupHasRestrictedAclWhenDirectoryDoesNotInheritPermissions()
    {
        using TemporaryDirectory directory = new();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User
            ?? throw new InvalidOperationException("The current Windows owner SID could not be determined.");
        DirectorySecurity directorySecurity = new();
        directorySecurity.SetOwner(owner);
        directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in new[]
                 {
                     owner,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            directorySecurity.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(directory.Path).SetAccessControl(directorySecurity);
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        SqliteMigrationRunner.SqliteMigrationDefinition[] migrations =
        [
            Migration(1, "one", "CREATE TABLE data_v1 (value TEXT);"),
            Migration(2, "two", "CREATE TABLE data_v2 (value TEXT);"),
        ];

        await SqliteMigrationRunner.ApplyAsync(factory, [migrations[0]]);
        await SqliteMigrationRunner.ApplyAsync(factory, migrations);

        string backupPath = Assert.Single(Directory.GetFiles(directory.Path, "index.db.pre-upgrade-1-to-2-*.bak"));
        FileSecurity backupSecurity = new FileInfo(backupPath).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        Assert.True(backupSecurity.AreAccessRulesProtected);
        SqliteStorageGuard.VerifyRestrictedAccess(backupSecurity, owner, identity.Owner);
    }

    [Fact]
    public async Task FailedMigrationRollsBackAndCanBeReopened()
    {
        using TemporaryDirectory directory = new();
        SqliteMigrationRunner.SqliteMigrationDefinition initialMigration =
            Migration(1, "one", "CREATE TABLE data_v1 (value TEXT);");
        SqliteMigrationRunner.SqliteMigrationDefinition failedMigration =
            Migration(2, "two", "CREATE TABLE data_v2 (value TEXT); INSERT INTO missing_table VALUES (1);");

        using (SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path))
        {
            await SqliteMigrationRunner.ApplyAsync(factory, [initialMigration]);
            await Assert.ThrowsAsync<SqliteException>(
                () => SqliteMigrationRunner.ApplyAsync(factory, [initialMigration, failedMigration]));
        }

        using (SqliteConnectionFactory reopened = await SqliteConnectionFactory.CreateAsync(directory.Path))
        {
            await SqliteMigrationRunner.ApplyAsync(reopened, [initialMigration]);
            await using SqliteConnectionLease lease = await reopened.OpenReaderAsync();
            Assert.Equal(1, await ScalarAsync<long>(lease.Connection, "SELECT count(*) FROM schema_migrations;"));
            Assert.Equal(0, await ScalarAsync<long>(
                lease.Connection,
                "SELECT count(*) FROM sqlite_master WHERE name = 'data_v2';"));
        }

        Assert.Single(Directory.GetFiles(directory.Path, "index.db.pre-upgrade-1-to-2-*.bak"));
    }

    [Fact]
    public async Task RejectsChangedChecksumAndUnsupportedNewerSchemaWithoutDeletingDatabase()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        SqliteMigrationRunner.SqliteMigrationDefinition initial = Migration(1, "one", "CREATE TABLE data_v1 (value TEXT);");
        SqliteMigrationRunner.SqliteMigrationDefinition changed = Migration(1, "one", "CREATE TABLE data_v1 (value INTEGER);");
        SqliteMigrationRunner.SqliteMigrationDefinition second = Migration(2, "two", "CREATE TABLE data_v2 (value TEXT);");

        await SqliteMigrationRunner.ApplyAsync(factory, [initial]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SqliteMigrationRunner.ApplyAsync(factory, [changed]));
        await SqliteMigrationRunner.ApplyAsync(factory, [initial, second]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SqliteMigrationRunner.ApplyAsync(factory, [initial]));

        Assert.True(File.Exists(factory.DatabasePath));
        await using SqliteConnectionLease lease = await factory.OpenReaderAsync();
        Assert.Equal(2, await ScalarAsync<long>(lease.Connection, "SELECT count(*) FROM schema_migrations;"));
        Assert.Equal(1, await ScalarAsync<long>(
            lease.Connection,
            "SELECT count(*) FROM sqlite_master WHERE name = 'data_v2';"));
    }

    [Fact]
    public async Task RejectsUnknownExistingSchemaWithoutDeletingIt()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await using (SqliteConnectionLease writer = await factory.OpenWriterAsync())
        {
            await ExecuteAsync(writer.Connection, "CREATE TABLE sqliteX (value TEXT);");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteMigrationRunner.ApplyAsync(factory));

        await using SqliteConnectionLease reader = await factory.OpenReaderAsync();
        Assert.Equal(1, await ScalarAsync<long>(
            reader.Connection,
            "SELECT count(*) FROM sqlite_master WHERE name = 'sqliteX';"));
    }

    [Fact]
    public async Task RejectsLiteralSqlitePrefixNearMatchWithEmptyLedger()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await using (SqliteConnectionLease writer = await factory.OpenWriterAsync())
        {
            await ExecuteAsync(
                writer.Connection,
                """
                CREATE TABLE schema_migrations (
                    version INTEGER PRIMARY KEY CHECK (version > 0),
                    name TEXT NOT NULL UNIQUE,
                    sha256 TEXT NOT NULL CHECK (length(sha256) = 64),
                    applied_at TEXT NOT NULL CHECK (applied_at GLOB '????-??-??T??:??:??.???Z')
                ) STRICT;
                CREATE TABLE sqliteX (value TEXT);
                """);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteMigrationRunner.ApplyAsync(factory));

        await using SqliteConnectionLease reader = await factory.OpenReaderAsync();
        Assert.Equal(1, await ScalarAsync<long>(
            reader.Connection,
            "SELECT count(*) FROM sqlite_master WHERE name = 'sqliteX';"));
    }

    [Fact]
    public async Task RefusesMalformedDatabaseWithoutDeletingIt()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await using (SqliteConnectionLease writer = await factory.OpenWriterAsync())
        {
            await ExecuteAsync(
                writer.Connection,
                """
                CREATE TABLE malformed (value TEXT);
                PRAGMA writable_schema=ON;
                UPDATE sqlite_master SET sql = 'CREATE TABLE malformed(' WHERE name = 'malformed';
                PRAGMA writable_schema=OFF;
                """);
        }

        await Assert.ThrowsAnyAsync<SqliteException>(() => SqliteMigrationRunner.ApplyAsync(factory));

        Assert.True(File.Exists(factory.DatabasePath));
        Assert.True(new FileInfo(factory.DatabasePath).Length > 0);
    }

    [Fact]
    public async Task ProcessCrashDuringMigrationRecoversOnReopen()
    {
        using TemporaryDirectory directory = new();
        SqliteMigrationRunner.SqliteMigrationDefinition first = Migration(
            1,
            "one",
            "CREATE TABLE data_v1 (value TEXT); INSERT INTO data_v1 VALUES ('preserved');");
        SqliteMigrationRunner.SqliteMigrationDefinition second = Migration(2, "two", "CREATE TABLE data_v2 (value TEXT);");
        using (SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path))
        {
            await SqliteMigrationRunner.ApplyAsync(factory, [first]);
        }

        string markerPath = Path.Combine(directory.Path, "migration-ready");
        using Process child = StartCrashHelper(directory.Path, markerPath);
        Task<string> standardOutput = child.StandardOutput.ReadToEndAsync();
        Task<string> standardError = child.StandardError.ReadToEndAsync();
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(markerPath) && !child.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        if (!File.Exists(markerPath))
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }

            string output = await standardOutput;
            string error = await standardError;
            throw new InvalidOperationException(
                $"Migration crash helper did not reach its transaction. stdout: {output} stderr: {error}");
        }

        int helperProcessId = int.Parse(
            await File.ReadAllTextAsync(markerPath),
            System.Globalization.CultureInfo.InvariantCulture);
        using Process helper = Process.GetProcessById(helperProcessId);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _ = await standardOutput;
        _ = await standardError;

        using SqliteConnectionFactory reopened = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await using (SqliteConnectionLease recovered = await reopened.OpenReaderAsync())
        {
            Assert.Equal(1, await ScalarAsync<long>(
                recovered.Connection,
                "SELECT count(*) FROM schema_migrations;"));
            Assert.Equal("preserved", await ScalarAsync<string>(
                recovered.Connection,
                "SELECT value FROM data_v1;"));
            Assert.Equal(0, await ScalarAsync<long>(
                recovered.Connection,
                "SELECT count(*) FROM sqlite_master WHERE name = 'data_v2';"));
        }

        await SqliteMigrationRunner.ApplyAsync(reopened, [first, second]);
        await using SqliteConnectionLease reader = await reopened.OpenReaderAsync();
        Assert.Equal(2, await ScalarAsync<long>(reader.Connection, "SELECT count(*) FROM schema_migrations;"));
        Assert.Equal("preserved", await ScalarAsync<string>(reader.Connection, "SELECT value FROM data_v1;"));
        Assert.Equal(1, await ScalarAsync<long>(
            reader.Connection,
            "SELECT count(*) FROM sqlite_master WHERE name = 'data_v2';"));
    }

    [Fact]
    public async Task MigrationCrashHelper()
    {
        string? directory = Environment.GetEnvironmentVariable("LFI_MIGRATION_CRASH_DIRECTORY");
        string? markerPath = Environment.GetEnvironmentVariable("LFI_MIGRATION_CRASH_MARKER");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(markerPath))
        {
            return;
        }

        SqliteMigrationRunner.SqliteMigrationDefinition first = Migration(
            1,
            "one",
            "CREATE TABLE data_v1 (value TEXT); INSERT INTO data_v1 VALUES ('preserved');");
        SqliteMigrationRunner.SqliteMigrationDefinition second = Migration(2, "two", "CREATE TABLE data_v2 (value TEXT);");
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory);
        await SqliteMigrationRunner.ApplyAsync(
            factory,
            [first, second],
            afterMigrationSql: async cancellationToken =>
            {
                string temporaryMarkerPath = markerPath + ".tmp";
                await File.WriteAllTextAsync(
                    temporaryMarkerPath,
                    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken);
                File.Move(temporaryMarkerPath, markerPath);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
    }

    [Fact]
    public async Task ConcurrentMigrationFailsOnDatabaseWriteLockAndCanRetry()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory lockFactory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        using SqliteConnectionFactory migrationFactory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await using SqliteConnectionLease lockLease = await lockFactory.OpenWriterAsync();
        using SqliteTransaction transaction = lockLease.Connection.BeginTransaction(deferred: false);

        await Assert.ThrowsAsync<SqliteException>(() => SqliteMigrationRunner.ApplyAsync(migrationFactory));

        transaction.Rollback();
        await SqliteMigrationRunner.ApplyAsync(migrationFactory);
    }

    [Fact]
    public async Task OverlappingMigrationsInOneFactoryFailImmediately()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await AssertOverlappingMigrationFailsAsync(factory, factory);
    }

    [Fact]
    public async Task OverlappingMigrationsAcrossFactoriesFailImmediately()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory firstFactory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        using SqliteConnectionFactory secondFactory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await AssertOverlappingMigrationFailsAsync(firstFactory, secondFactory);
    }

    [Fact]
    public async Task CancellingBackupRemovesPartialFileAndReleasesWriterLease()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        SqliteMigrationRunner.SqliteMigrationDefinition first = Migration(
            1,
            "one",
            """
            CREATE TABLE data_v1 (value BLOB);
            WITH RECURSIVE sequence(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM sequence WHERE n < 200)
            INSERT INTO data_v1 SELECT zeroblob(4000) FROM sequence;
            """);
        SqliteMigrationRunner.SqliteMigrationDefinition second = Migration(2, "two", "CREATE TABLE data_v2 (value TEXT);");
        await SqliteMigrationRunner.ApplyAsync(factory, [first]);
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SqliteMigrationRunner.ApplyAsync(
                factory,
                [first, second],
                cancellation.Token,
                afterBackupStep: _ =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }));

        Assert.Empty(Directory.GetFiles(directory.Path, "index.db.pre-upgrade-1-to-2-*.bak"));
        await SqliteMigrationRunner.ApplyAsync(factory, [first, second]);
    }

    [Fact]
    public async Task WaitingForWriterLeaseHonorsCancellation()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await using SqliteConnectionLease writer = await factory.OpenWriterAsync();
        using CancellationTokenSource cancellation = new();
        Task migration = SqliteMigrationRunner.ApplyAsync(factory, cancellationToken: cancellation.Token);

        Assert.False(migration.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration);
    }

    private static SqliteMigrationRunner.SqliteMigrationDefinition Migration(int version, string name, string sql)
    {
        return new SqliteMigrationRunner.SqliteMigrationDefinition(version, name, sql);
    }

    private static Process StartCrashHelper(string directory, string markerPath)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(SqliteMigrationRunnerTests).Assembly.Location);
        startInfo.ArgumentList.Add(
            "--TestCaseFilter:FullyQualifiedName~LocalFileIntelligence.IntegrationTests.SqliteMigrationRunnerTests.MigrationCrashHelper");
        startInfo.Environment["LFI_MIGRATION_CRASH_DIRECTORY"] = directory;
        startInfo.Environment["LFI_MIGRATION_CRASH_MARKER"] = markerPath;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The migration crash helper process could not be started.");
    }

    private static async Task AssertOverlappingMigrationFailsAsync(
        SqliteConnectionFactory firstFactory,
        SqliteConnectionFactory secondFactory)
    {
        SqliteMigrationRunner.SqliteMigrationDefinition migration =
            Migration(1, "one", "CREATE TABLE data_v1 (value TEXT);");
        TaskCompletionSource<bool> migrationReachedSql = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> continueMigration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task firstMigration = SqliteMigrationRunner.ApplyAsync(
            firstFactory,
            [migration],
            afterMigrationSql: async cancellationToken =>
            {
                migrationReachedSql.TrySetResult(true);
                await continueMigration.Task.WaitAsync(cancellationToken);
            });
        await migrationReachedSql.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => SqliteMigrationRunner.ApplyAsync(secondFactory, [migration]));
        }
        finally
        {
            continueMigration.TrySetResult(true);
        }

        await firstMigration.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<string[]> GetObjectNamesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type || ':' || name FROM sqlite_master
            WHERE substr(name, 1, 7) <> 'sqlite_'
            ORDER BY type, name;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<string> objectNames = [];
        while (await reader.ReadAsync())
        {
            objectNames.Add(reader.GetString(0));
        }

        return objectNames.ToArray();
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        object converted = Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("SQLite returned no value for a required test assertion.");
        return (T)converted;
    }

    private static async Task<long> FtsMatchesAsync(SqliteConnection connection, string term)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM file_contents_fts WHERE file_contents_fts MATCH $term;";
        command.Parameters.AddWithValue("$term", term);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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
