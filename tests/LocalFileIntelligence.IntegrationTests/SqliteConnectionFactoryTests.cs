using System.ComponentModel;
using System.Runtime.InteropServices;
using LocalFileIntelligence.Storage;
using Microsoft.Data.Sqlite;

namespace LocalFileIntelligence.IntegrationTests;

public sealed class SqliteConnectionFactoryTests
{
    [Fact]
    public async Task ConnectionsHaveRequiredSettingsAndReadersAreReadOnly()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);

        await using SqliteConnectionLease writer = await factory.OpenWriterAsync();
        Assert.Equal("wal", await ScalarAsync<string>(writer.Connection, "PRAGMA journal_mode;"));
        Assert.Equal(1, await ScalarAsync<long>(writer.Connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5000, await ScalarAsync<long>(writer.Connection, "PRAGMA busy_timeout;"));
        Assert.Equal(2, await ScalarAsync<long>(writer.Connection, "PRAGMA synchronous;"));

        await using SqliteConnectionLease reader = await factory.OpenReaderAsync();
        Assert.Equal(1, await ScalarAsync<long>(reader.Connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5000, await ScalarAsync<long>(reader.Connection, "PRAGMA busy_timeout;"));
        Assert.Equal(2, await ScalarAsync<long>(reader.Connection, "PRAGMA synchronous;"));
        Assert.Equal(1, await ScalarAsync<long>(reader.Connection, "PRAGMA query_only;"));
        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(reader.Connection, "CREATE TABLE forbidden (id INTEGER);"));
        Assert.Equal(8, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task WriterLeaseSerializesWritersAndHonorsCancellation()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        await using SqliteConnectionLease writer = await factory.OpenWriterAsync();

        using CancellationTokenSource cancellation = new();
        Task<SqliteConnectionLease> waitingWriter = factory.OpenWriterAsync(cancellation.Token);
        Assert.False(waitingWriter.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingWriter);

        Task<SqliteConnectionLease> nextWriter = factory.OpenWriterAsync();
        Assert.False(nextWriter.IsCompleted);
        await writer.DisposeAsync();
        await using SqliteConnectionLease acquiredWriter = await nextWriter;
    }

    [Fact]
    public async Task ReaderLeasesRespectConfiguredBound()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path, maxReaders: 1);
        await using SqliteConnectionLease reader = await factory.OpenReaderAsync();

        Task<SqliteConnectionLease> nextReader = factory.OpenReaderAsync();
        Assert.False(nextReader.IsCompleted);
        await reader.DisposeAsync();
        await using SqliteConnectionLease acquiredReader = await nextReader;
    }

    [Fact]
    public async Task RejectsRelativeRootAndNetworkPaths()
    {
        using TemporaryDirectory directory = new();

        await Assert.ThrowsAsync<ArgumentException>(() => SqliteConnectionFactory.CreateAsync("relative-data"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => SqliteConnectionFactory.CreateAsync(Path.GetPathRoot(directory.Path)!));
        await Assert.ThrowsAsync<ArgumentException>(
            () => SqliteConnectionFactory.CreateAsync(Environment.CurrentDirectory));
        string workingDirectoryChild = Path.Combine(Environment.CurrentDirectory, $"lfi-{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<ArgumentException>(
            () => SqliteConnectionFactory.CreateAsync(workingDirectoryChild));
        Assert.False(Directory.Exists(workingDirectoryChild));
        await Assert.ThrowsAsync<ArgumentException>(
            () => SqliteConnectionFactory.CreateAsync(@"\\server\share\lfi"));
    }

    [Fact]
    public async Task RejectsReparsePointDataDirectory()
    {
        using TemporaryDirectory directory = new();
        string targetPath = Path.Combine(directory.Path, "target");
        string linkPath = Path.Combine(directory.Path, "link");
        Directory.CreateDirectory(targetPath);
        Directory.CreateSymbolicLink(linkPath, targetPath);

        await Assert.ThrowsAsync<ArgumentException>(() => SqliteConnectionFactory.CreateAsync(linkPath));
    }

    [WindowsFact]
    public async Task DataDirectoryCannotBeReplacedAfterFactoryCreation()
    {
        using TemporaryDirectory directory = new();
        string dataDirectory = Path.Combine(directory.Path, "data");
        string replacementPath = Path.Combine(directory.Path, "replacement");
        Directory.CreateDirectory(dataDirectory);
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(dataDirectory);

        Assert.ThrowsAny<IOException>(() => Directory.Move(dataDirectory, replacementPath));
        await using SqliteConnectionLease writer = await factory.OpenWriterAsync();
    }

    [WindowsFact]
    public async Task RejectsDatabaseReplacementBeforeOpeningLease()
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);
        string databasePath = Path.Combine(directory.Path, "index.db");
        string replacementPath = Path.Combine(directory.Path, "replacement.db");

        Assert.ThrowsAny<IOException>(() => File.Move(databasePath, replacementPath));
        await using SqliteConnectionLease writer = await factory.OpenWriterAsync();
    }

    [WindowsFact]
    public async Task RejectsDatabaseHardLinks()
    {
        using TemporaryDirectory directory = new();
        string dataDirectory = Path.Combine(directory.Path, "data");
        string externalFile = Path.Combine(directory.Path, "outside.db");
        string databasePath = Path.Combine(dataDirectory, "index.db");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(externalFile, "synthetic");
        if (!CreateHardLink(databasePath, externalFile, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        await Assert.ThrowsAsync<IOException>(() => SqliteConnectionFactory.CreateAsync(dataDirectory));
    }

    [Fact]
    public async Task SurfacesDataDirectoryAccessFailure()
    {
        using TemporaryDirectory directory = new();
        string filePath = Path.Combine(directory.Path, "not-a-directory");
        await File.WriteAllTextAsync(filePath, "synthetic");

        await Assert.ThrowsAnyAsync<IOException>(
            () => SqliteConnectionFactory.CreateAsync(Path.Combine(filePath, "database")));
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

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

    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "The Windows storage identity guard is platform-specific.";
            }
        }
    }
}
