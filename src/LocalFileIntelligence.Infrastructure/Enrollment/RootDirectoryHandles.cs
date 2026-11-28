using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalFileIntelligence.Core.Configuration;
using Microsoft.Win32.SafeHandles;

namespace LocalFileIntelligence.Infrastructure.Enrollment;

[SupportedOSPlatform("windows")]
internal static class RootDirectoryHandles
{
    private const uint DriveFixed = 3;
    private const uint FileOpenReparsePoint = 0x200000, FileOpenNoRecall = 0x400000;
    private const uint ReadAttributes = 0x80, Traverse = 0x20, ListDirectory = 0x1;
    private const uint Synchronize = 0x100000, SynchronousIoNonAlert = 0x20;
    private const uint ObjectCaseInsensitive = 0x40;
    private const int FileAttributeTagInfo = 9, FileIdInfo = 18, FileCaseSensitiveInfo = 23;
    private const uint Directory = 0x10, Reparse = 0x400, Offline = 0x1000, RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;

    internal static string GetFixedVolume(string driveRoot)
    {
        char[] buffer = new char[50];
        if (GetDriveTypeW(driveRoot) != DriveFixed)
        {
            throw new IOException("Select a directory on a local fixed volume.");
        }

        if (!GetVolumeNameForVolumeMountPointW(driveRoot, buffer, buffer.Length))
        {
            throw OpenError(Marshal.GetLastPInvokeError());
        }

        string volume = new(buffer, 0, Array.IndexOf(buffer, '\0'));
        if (!volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
            || !volume.EndsWith(@"}\", StringComparison.Ordinal)
            || GetDriveTypeW(volume) != DriveFixed)
        {
            throw new IOException("The drive does not identify a local fixed volume.");
        }

        return volume;
    }

    internal static unsafe SafeFileHandle OpenDirectory(string name, SafeFileHandle? parent, bool list)
    {
        // NtOpenFile allows a directory open without requesting backup intent or following a link.
        fixed (char* buffer = name)
        {
            ushort bytes = checked((ushort)(name.Length * sizeof(char)));
            UnicodeString nativeName = new() { Length = bytes, MaximumLength = bytes, Buffer = buffer };
            ObjectAttributes attributes = new()
            {
                Length = sizeof(ObjectAttributes),
                RootDirectory = parent?.DangerousGetHandle() ?? 0,
                ObjectName = &nativeName,
                Attributes = ObjectCaseInsensitive,
            };
            int status = NtOpenFile(out SafeFileHandle handle, Synchronize | ReadAttributes | Traverse | (list ? ListDirectory : 0),
                ref attributes, out _, FileShare.Read, SynchronousIoNonAlert | FileOpenReparsePoint | FileOpenNoRecall);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                handle.Dispose();
                throw OpenError(RtlNtStatusToDosError(status));
            }

            return handle;
        }
    }

    internal static void RequireNtfs(SafeFileHandle root)
    {
        char[] name = new char[32];
        if (!GetVolumeInformationByHandleW(root, 0, 0, out _, out _, out _, name, name.Length))
        {
            throw OpenError(Marshal.GetLastPInvokeError());
        }

        if (!name.AsSpan(0, Array.IndexOf(name, '\0')).Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Root enrollment supports NTFS directories only.");
        }
    }

    internal static void CheckDirectory(SafeFileHandle handle)
    {
        AttributeTag tag = ReadInfo<AttributeTag>(handle, FileAttributeTagInfo);
        if ((tag.Attributes & (Directory | Reparse | Offline | RecallOnOpen | RecallOnDataAccess)) != Directory
            || ReadInfo<uint>(handle, FileCaseSensitiveInfo) != 0)
        {
            throw new IOException("Root enrollment requires online, case-insensitive directories without reparse points.");
        }
    }

    internal static string GetDrivePath(SafeFileHandle handle)
    {
        char[] buffer = new char[32768];
        uint length = GetFinalPathNameByHandleW(handle, buffer, buffer.Length, 0);
        if (length == 0)
        {
            throw OpenError(Marshal.GetLastPInvokeError());
        }

        if (length >= buffer.Length || !buffer.AsSpan(0, (int)length).StartsWith(@"\\?\"))
        {
            throw new IOException("The opened directory has no supported canonical path.");
        }

        return SelectedFolderPath.Normalize(new string(buffer, 4, (int)length - 4));
    }

    internal static string GetFileId(SafeFileHandle handle) =>
        ReadInfo<FileIdentity>(handle, FileIdInfo).Id.ToString("N");

    private static unsafe T ReadInfo<T>(SafeFileHandle handle, int infoClass) where T : unmanaged
    {
        T info;
        if (!GetFileInformationByHandleEx(handle, infoClass, &info, sizeof(T)))
        {
            throw OpenError(Marshal.GetLastPInvokeError());
        }

        return info;
    }

    private static Exception OpenError(int error) => error == 5
        ? new UnauthorizedAccessException("Access to the selected directory was denied.", new Win32Exception(error))
        : new IOException("The selected directory could not be verified.", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public nint Status, Information; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes, ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong VolumeSerial; public Guid Id; }

    [DllImport("ntdll.dll")]
    private static extern int NtOpenFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock result, FileShare share, uint options);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string path, [Out] char[] volume, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, nint volumeName, int nameLength,
        out uint serial, out uint componentLength, out uint flags, [Out] char[] fileSystem, int fsLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, int length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, void* data, int length);
}
