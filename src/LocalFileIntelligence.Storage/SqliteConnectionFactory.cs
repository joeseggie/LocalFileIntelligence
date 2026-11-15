using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.Storage;

public sealed class SqliteConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 5000;
    private readonly string _connectionString;
    private readonly string _readOnlyConnectionString;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly SemaphoreSlim _readerGate;

    private SqliteConnectionFactory(string databasePath, int maxReaders)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
            Pooling = false,
        }.ToString();
        _readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
            Pooling = false,
        }.ToString();
        _readerGate = new SemaphoreSlim(maxReaders, maxReaders);
    }

    public static async Task<SqliteConnectionFactory> CreateAsync(
        string dataDirectory,
        int maxReaders = 4,
        CancellationToken cancellationToken = default)
    {
        if (maxReaders < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReaders), "At least one reader is required.");
        }

        string fullPath = ValidateDataDirectory(dataDirectory);
        Directory.CreateDirectory(fullPath);
        ValidatePathComponents(fullPath);

        string databasePath = Path.Combine(fullPath, "index.db");
        if (HasReparsePoint(databasePath))
        {
            throw new ArgumentException("The database file must not be a reparse point.", nameof(dataDirectory));
        }

        SqliteConnectionFactory factory = new(databasePath, maxReaders);
        await using SqliteConnection connection = new(factory._connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, setWal: true, readOnly: false, cancellationToken)
            .ConfigureAwait(false);

        return factory;
    }

    public async Task<SqliteConnectionLease> OpenWriterAsync(CancellationToken cancellationToken = default)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await OpenConnectionAsync(_connectionString, _writerGate, readOnly: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SqliteConnectionLease> OpenReaderAsync(CancellationToken cancellationToken = default)
    {
        await _readerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await OpenConnectionAsync(_readOnlyConnectionString, _readerGate, readOnly: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<SqliteConnectionLease> OpenConnectionAsync(
        string connectionString,
        SemaphoreSlim gate,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, setWal: false, readOnly, cancellationToken)
                .ConfigureAwait(false);
            return new SqliteConnectionLease(connection, gate);
        }
        catch
        {
            try
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }

            throw;
        }
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        bool setWal,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        if (setWal)
        {
            string journalMode = await ExecuteScalarAsync<string>(connection, "PRAGMA journal_mode=WAL;", cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SQLite did not enable WAL journal mode.");
            }
        }

        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
        if (readOnly)
        {
            await ExecuteNonQueryAsync(connection, "PRAGMA query_only=ON;", cancellationToken).ConfigureAwait(false);
        }

        string actualJournalMode = await ExecuteScalarAsync<string>(connection, "PRAGMA journal_mode;", cancellationToken)
            .ConfigureAwait(false);
        long foreignKeys = await ExecuteScalarAsync<long>(connection, "PRAGMA foreign_keys;", cancellationToken)
            .ConfigureAwait(false);
        long busyTimeout = await ExecuteScalarAsync<long>(connection, "PRAGMA busy_timeout;", cancellationToken)
            .ConfigureAwait(false);
        long synchronous = await ExecuteScalarAsync<long>(connection, "PRAGMA synchronous;", cancellationToken)
            .ConfigureAwait(false);
        long queryOnly = await ExecuteScalarAsync<long>(connection, "PRAGMA query_only;", cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(actualJournalMode, "wal", StringComparison.OrdinalIgnoreCase)
            || foreignKeys != 1
            || busyTimeout != BusyTimeoutMilliseconds
            || synchronous != 2
            || queryOnly != (readOnly ? 1 : 0))
        {
            throw new InvalidOperationException("SQLite connection settings did not match the required configuration.");
        }
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        object converted = Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("SQLite returned no value for a required connection setting.");
        return (T)converted;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateDataDirectory(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("An absolute local data directory must be supplied.", nameof(dataDirectory));
        }

        string fullPath = Path.GetFullPath(dataDirectory);
        string? pathRoot = Path.GetPathRoot(fullPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string currentDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        string relativeToCurrentDirectory = Path.GetRelativePath(currentDirectory, fullPath);
        bool isUnderCurrentDirectory = relativeToCurrentDirectory == "."
            || (!Path.IsPathRooted(relativeToCurrentDirectory)
                && relativeToCurrentDirectory != ".."
                && !relativeToCurrentDirectory.StartsWith($"..{Path.DirectorySeparatorChar}", pathComparison));
        if (pathRoot is null
            || string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(pathRoot),
                pathComparison)
            || isUnderCurrentDirectory
            || fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            || fullPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The data directory must be a dedicated, non-root local path outside the working directory tree.",
                nameof(dataDirectory));
        }

        if (OperatingSystem.IsWindows() && new DriveInfo(pathRoot).DriveType == DriveType.Network)
        {
            throw new ArgumentException("The data directory must be on a local drive.", nameof(dataDirectory));
        }

        ValidatePathComponents(fullPath);
        return fullPath;
    }

    private static void ValidatePathComponents(string fullPath)
    {
        string? pathRoot = Path.GetPathRoot(fullPath);
        if (pathRoot is null)
        {
            throw new ArgumentException("The data directory must be fully qualified.", nameof(fullPath));
        }

        string currentPath = pathRoot;
        foreach (string component in fullPath[pathRoot.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            if (HasReparsePoint(currentPath))
            {
                throw new ArgumentException("The data directory must not contain reparse points.", nameof(fullPath));
            }
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
