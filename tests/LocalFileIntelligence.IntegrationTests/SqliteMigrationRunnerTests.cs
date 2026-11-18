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
            await ExecuteAsync(writer.Connection, "CREATE TABLE unknown_data (value TEXT);");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteMigrationRunner.ApplyAsync(factory));

        await using SqliteConnectionLease reader = await factory.OpenReaderAsync();
        Assert.Equal(1, await ScalarAsync<long>(
            reader.Connection,
            "SELECT count(*) FROM sqlite_master WHERE name = 'unknown_data';"));
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

    private static async Task<string[]> GetObjectNamesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type || ':' || name FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%'
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
