using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LocalFileIntelligence.Storage;

internal sealed class SqliteStorageGuard : IDisposable
{
    private const uint FileReadData = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    private readonly string _directoryPath;
    private readonly string _databasePath;
    private readonly List<SafeFileHandle> _directoryHandles = [];
    private readonly List<FileIdentity> _directoryIdentities = [];
    private SafeFileHandle? _databaseHandle;
    private FileIdentity? _databaseIdentity;

    private SqliteStorageGuard(string directoryPath, string databasePath)
    {
        _directoryPath = directoryPath;
        _databasePath = databasePath;
    }

    public static SqliteStorageGuard Create(string directoryPath, string databasePath)
    {
        SqliteStorageGuard guard = new(directoryPath, databasePath);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                guard.OpenAndPinDirectoryChain();
                VerifyRestrictedAccess(directoryPath, isDirectory: true);
            }

            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    public void ValidateBeforeOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            ValidateDirectoryChain();
            VerifyRestrictedAccess(_directoryPath, isDirectory: true);
        }

        ValidateDatabaseObjects();
    }

    public void PinDatabaseFile()
    {
        ValidateDatabaseObject(_databasePath);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SafeFileHandle handle = OpenHandle(_databasePath, isDirectory: false);
        try
        {
            FileIdentity identity = GetIdentity(handle, isDirectory: false);
            if (_databaseIdentity is FileIdentity expected && identity != expected)
            {
                throw new IOException("The SQLite database file changed while the factory was being created.");
            }

            _databaseIdentity = identity;
            _databaseHandle?.Dispose();
            _databaseHandle = handle;
            handle = null!;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private void OpenAndPinDirectoryChain()
    {
        string? pathRoot = Path.GetPathRoot(_directoryPath);
        if (pathRoot is null)
        {
            throw new ArgumentException("The data directory must be fully qualified.", nameof(_directoryPath));
        }

        string currentPath = pathRoot;
        AddDirectoryHandle(currentPath);
        foreach (string component in _directoryPath[pathRoot.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            AddDirectoryHandle(currentPath);
        }
    }

    private void AddDirectoryHandle(string path)
    {
        SafeFileHandle handle = OpenHandle(path, isDirectory: true);
        try
        {
            FileIdentity identity = GetIdentity(handle, isDirectory: true);
            _directoryHandles.Add(handle);
            _directoryIdentities.Add(identity);
            handle = null!;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private void ValidateDirectoryChain()
    {
        string? pathRoot = Path.GetPathRoot(_directoryPath);
        if (pathRoot is null)
        {
            throw new IOException("The SQLite data directory is no longer available.");
        }

        int index = 0;
        string currentPath = pathRoot;
        ValidateDirectoryHandle(currentPath, index++);
        foreach (string component in _directoryPath[pathRoot.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            ValidateDirectoryHandle(currentPath, index++);
        }
    }

    private void ValidateDirectoryHandle(string path, int index)
    {
        using SafeFileHandle handle = OpenHandle(path, isDirectory: true);
        if (index >= _directoryIdentities.Count
            || GetIdentity(handle, isDirectory: true) != _directoryIdentities[index])
        {
            throw new IOException("The SQLite data directory identity changed after factory creation.");
        }
    }

    private void ValidateDatabaseObjects()
    {
        ValidateDatabaseObject(_databasePath);
        if (OperatingSystem.IsWindows() && _databaseIdentity is FileIdentity expected)
        {
            using SafeFileHandle handle = OpenHandle(_databasePath, isDirectory: false);
            if (GetIdentity(handle, isDirectory: false) != expected)
            {
                throw new IOException("The SQLite database file identity changed after factory creation.");
            }
        }

        ValidateDatabaseObject($"{_databasePath}-wal");
        ValidateDatabaseObject($"{_databasePath}-shm");
        ValidateDatabaseObject($"{_databasePath}-journal");
    }

    private static void ValidateDatabaseObject(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("SQLite database files must not be reparse points.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException("SQLite database and sidecar paths must be files, not directories.");
            }

            if (OperatingSystem.IsWindows())
            {
                VerifyRestrictedAccess(path, isDirectory: false);
                using SafeFileHandle handle = OpenHandle(path, isDirectory: false);
                _ = GetIdentity(handle, isDirectory: false);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void VerifyRestrictedAccess(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User
            ?? throw new UnauthorizedAccessException("The current Windows owner SID could not be determined.");
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        VerifyRestrictedAccess(security, owner, identity.Owner);
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyRestrictedAccess(
        FileSystemSecurity security,
        SecurityIdentifier owner,
        SecurityIdentifier? tokenOwner)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier actualOwner
            || (!actualOwner.Equals(owner) && (tokenOwner is null || !actualOwner.Equals(tokenOwner))))
        {
            throw new UnauthorizedAccessException("SQLite storage must be owned by the current Windows account.");
        }

        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier sid
                && !sid.Equals(owner)
                && !sid.Equals(system)
                && !sid.Equals(administrators))
            {
                throw new UnauthorizedAccessException(
                    "SQLite storage must not grant access to accounts other than its owner, SYSTEM, or Administrators.");
            }
        }
    }

    internal static SafeFileHandle OpenHandle(string path, bool isDirectory)
    {
        uint flags = FileFlagOpenReparsePoint | (isDirectory ? FileFlagBackupSemantics : 0);
        // Attribute-only file handles do not enforce the delete-sharing restriction.
        SafeFileHandle handle = CreateFile(
            path,
            FileReadAttributes | (isDirectory ? 0 : FileReadData),
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            Win32Exception nativeError = new(error);
            if (error == ErrorFileNotFound)
            {
                throw new FileNotFoundException("The SQLite storage file could not be opened safely.", path, nativeError);
            }

            if (error == ErrorPathNotFound)
            {
                throw new DirectoryNotFoundException("The SQLite storage path could not be opened safely.", nativeError);
            }

            throw new IOException(
                "The SQLite storage path could not be opened safely.",
                nativeError);
        }

        return handle;
    }

    private static FileIdentity GetIdentity(SafeFileHandle handle, bool isDirectory)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new IOException(
                "The SQLite storage path identity could not be verified.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        bool isReparsePoint = (information.FileAttributes & FileAttributeReparsePoint) != 0;
        bool isDirectoryAttribute = (information.FileAttributes & FileAttributeDirectory) != 0;
        if (isReparsePoint
            || isDirectoryAttribute != isDirectory
            || (!isDirectory && information.NumberOfLinks != 1))
        {
            throw new IOException(
                "The SQLite storage path contains an unexpected file type, reparse point, or hard link.");
        }

        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow);
    }

    public void Dispose()
    {
        _databaseHandle?.Dispose();
        _databaseHandle = null;
        foreach (SafeFileHandle handle in _directoryHandles)
        {
            handle.Dispose();
        }

        _directoryHandles.Clear();
        _directoryIdentities.Clear();
    }

    private readonly record struct FileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
