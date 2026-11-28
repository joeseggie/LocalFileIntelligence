using System.Runtime.Versioning;
using System.Security.Principal;
using LocalFileIntelligence.Application.Enrollment;
using LocalFileIntelligence.Core.Configuration;
using Microsoft.Win32.SafeHandles;

namespace LocalFileIntelligence.Infrastructure.Enrollment;

[SupportedOSPlatform("windows")]
public sealed class WindowsRootDirectoryVerifier : IRootDirectoryVerifier
{
    public IVerifiedRootDirectory Open(string selectedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = SelectedFolderPath.Normalize(selectedPath);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem || identity.User is null)
        {
            throw new UnauthorizedAccessException("Root enrollment requires a Windows owner account.");
        }

        string[] components = path[3..].Split('\\');
        if (components.Length > 256)
        {
            throw new IOException("Root enrollment supports at most 256 directory components.");
        }

        string volume = RootDirectoryHandles.GetFixedVolume(path[..3]);
        List<SafeFileHandle> handles = new(components.Length + 1);
        try
        {
            // Use the local volume name, then single names relative to verified, pinned parents.
            SafeFileHandle current = RootDirectoryHandles.OpenDirectory(@"\??\" + volume[4..], null, list: false);
            handles.Add(current);
            RootDirectoryHandles.RequireNtfs(current);
            RootDirectoryHandles.CheckDirectory(current);
            for (int i = 0; i < components.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = RootDirectoryHandles.OpenDirectory(components[i], current, list: i == components.Length - 1);
                handles.Add(current);
                RootDirectoryHandles.CheckDirectory(current);
            }

            string canonicalPath = RootDirectoryHandles.GetDrivePath(current);
            if (!path.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The opened directory does not match the selected drive path.");
            }

            string directoryId = RootDirectoryHandles.GetFileId(current);
            return new VerifiedDirectory(canonicalPath, volume, directoryId, handles);
        }
        catch
        {
            Close(handles);
            throw;
        }
    }

    private static void Close(List<SafeFileHandle> handles)
    {
        for (int i = handles.Count - 1; i >= 0; i--)
        {
            handles[i].Dispose();
        }
    }

    private sealed class VerifiedDirectory(string path, string volume, string directoryId, List<SafeFileHandle> handles)
        : IVerifiedRootDirectory
    {
        public string Path => path;
        public string VolumeIdentity => volume;
        public string DirectoryIdentity => directoryId;
        public void Dispose() => Close(handles);
    }
}
