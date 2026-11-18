using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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

    [ReparsePointFact]
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
    [SupportedOSPlatform("windows")]
    public void RestrictedAccessAllowsOwnerSystemAndAdministrators()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User
            ?? throw new InvalidOperationException("The current Windows owner SID could not be determined.");
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

        foreach (bool isDirectory in new[] { true, false })
        {
            FileSystemSecurity security = CreateSecurityDescriptor(
                isDirectory,
                owner,
                owner,
                system,
                administrators);
            SqliteStorageGuard.VerifyRestrictedAccess(security, owner, tokenOwner: null);
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void RestrictedAccessRejectsUnexpectedOwnerAndOtherSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User
            ?? throw new InvalidOperationException("The current Windows owner SID could not be determined.");
        SecurityIdentifier unexpectedOwner = new("S-1-5-21-0-0-0-1");
        SecurityIdentifier everyone = new(WellKnownSidType.WorldSid, null);

        foreach (bool isDirectory in new[] { true, false })
        {
            FileSystemSecurity wrongOwner = CreateSecurityDescriptor(isDirectory, unexpectedOwner, owner);
            Assert.Throws<UnauthorizedAccessException>(
                () => SqliteStorageGuard.VerifyRestrictedAccess(wrongOwner, owner, tokenOwner: null));

            FileSystemSecurity shared = CreateSecurityDescriptor(isDirectory, owner, owner, everyone);
            Assert.Throws<UnauthorizedAccessException>(
                () => SqliteStorageGuard.VerifyRestrictedAccess(shared, owner, tokenOwner: null));
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task RejectsUnsafeDirectoryBeforeCreatingDatabase()
    {
        using TemporaryDirectory directory = new();
        AddAccessForEveryone(directory.Path, isDirectory: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => SqliteConnectionFactory.CreateAsync(directory.Path));
        Assert.False(File.Exists(Path.Combine(directory.Path, "index.db")));
    }

    [WindowsTheory]
    [InlineData("", true)]
    [InlineData("index.db", false)]
    [InlineData("index.db-wal", false)]
    [InlineData("index.db-shm", false)]
    [InlineData("index.db-journal", false)]
    [SupportedOSPlatform("windows")]
    public async Task RejectsStorageAclChangesBeforeOpeningLease(string name, bool isDirectory)
    {
        using TemporaryDirectory directory = new();
        using SqliteConnectionFactory factory = await SqliteConnectionFactory.CreateAsync(directory.Path);

        string path = Path.Combine(directory.Path, name);
        if (!isDirectory && name != "index.db")
        {
            await File.WriteAllTextAsync(path, "synthetic");
        }

        AddAccessForEveryone(path, isDirectory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => factory.OpenWriterAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => factory.OpenReaderAsync());
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void PreservesNativeMissingFileAndPathErrors()
    {
        using TemporaryDirectory directory = new();

        Assert.Throws<FileNotFoundException>(
            () => SqliteStorageGuard.OpenHandle(Path.Combine(directory.Path, "missing.db"), isDirectory: false));
        Assert.Throws<DirectoryNotFoundException>(
            () => SqliteStorageGuard.OpenHandle(
                Path.Combine(directory.Path, "missing", "index.db"),
                isDirectory: false));
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

    [SupportedOSPlatform("windows")]
    private static FileSystemSecurity CreateSecurityDescriptor(
        bool isDirectory,
        SecurityIdentifier owner,
        params SecurityIdentifier[] allowedSids)
    {
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(owner);
        foreach (SecurityIdentifier sid in allowedSids)
        {
            FileSystemAccessRule rule = isDirectory
                ? new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow)
                : new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow);
            if (isDirectory)
            {
                ((DirectorySecurity)security).AddAccessRule(rule);
            }
            else
            {
                ((FileSecurity)security).AddAccessRule(rule);
            }
        }

        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void AddAccessForEveryone(string path, bool isDirectory)
    {
        SecurityIdentifier everyone = new(WellKnownSidType.WorldSid, null);
        FileSystemAccessRule rule = isDirectory
            ? new FileSystemAccessRule(
                everyone,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow)
            : new FileSystemAccessRule(everyone, FileSystemRights.FullControl, AccessControlType.Allow);
        if (isDirectory)
        {
            DirectoryInfo directory = new(path);
            DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(rule);
            directory.SetAccessControl(security);
        }
        else
        {
            FileInfo file = new(path);
            FileSecurity security = file.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(rule);
            file.SetAccessControl(security);
        }
    }

    private static bool IsSymbolicLinkPrivilegeUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception nativeError && nativeError.NativeErrorCode == 1314)
            {
                return true;
            }

            if ((current.HResult & 0xFFFF) == 1314)
            {
                return true;
            }
        }

        return false;
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

    public sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "The Windows storage identity guard is platform-specific.";
            }
        }
    }

    public sealed class ReparsePointFactAttribute : FactAttribute
    {
        public ReparsePointFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            string probeDirectory = Path.Combine(Path.GetTempPath(), $"lfi-link-check-{Guid.NewGuid():N}");
            string targetPath = Path.Combine(probeDirectory, "target");
            string linkPath = Path.Combine(probeDirectory, "link");
            try
            {
                Directory.CreateDirectory(targetPath);
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (Exception exception) when (IsSymbolicLinkPrivilegeUnavailable(exception))
            {
                Skip = "Creating symbolic links requires Developer Mode or the Windows symbolic-link privilege.";
            }
            finally
            {
                if (Directory.Exists(linkPath))
                {
                    Directory.Delete(linkPath);
                }

                if (Directory.Exists(probeDirectory))
                {
                    Directory.Delete(probeDirectory, recursive: true);
                }
            }
        }
    }
}
