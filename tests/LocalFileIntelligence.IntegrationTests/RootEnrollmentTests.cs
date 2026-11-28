using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LocalFileIntelligence.Application.Enrollment;
using LocalFileIntelligence.Application.Persistence;
using LocalFileIntelligence.Core.Configuration;
using LocalFileIntelligence.Infrastructure.Enrollment;
using LocalFileIntelligence.Storage;
using static LocalFileIntelligence.IntegrationTests.SqliteConnectionFactoryTests;

namespace LocalFileIntelligence.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class RootEnrollmentTests : IDisposable
{
    private readonly string _tree = Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"lfi-{Guid.NewGuid():N}")).FullName;

    [WindowsFact]
    public async Task OnlyExplicitVerifiedSelectionIsPersistedAndReplacementIsRejected()
    {
        string selected = Directory.CreateDirectory(Path.Combine(_tree, "selected")).FullName;
        using SqliteConnectionFactory db = await CreateDatabaseAsync();
        SqliteIndexRootRepository roots = new(db);
        RootEnrollment enrollment = new(new WindowsRootDirectoryVerifier(), roots);

        _ = IndexingConfiguration.Parse(JsonSerializer.Serialize(new { schemaVersion = 1, selectedFolders = new[] { selected } }));
        Assert.Empty(await roots.ListAsync());
        IndexRoot root = await enrollment.RegisterAsync(selected.ToUpperInvariant());
        Assert.Equal(selected, root.Path, ignoreCase: true);
        Assert.Equal("selected", root.DisplayName);
        Assert.False(root.Enabled);
        Assert.Equal(IndexRootStatus.Pending, root.Status);
        Assert.Equal(1, root.PolicyRevision);
        Assert.Equal(root, await enrollment.RegisterAsync(selected));
        Assert.Equal(root, Assert.Single(await roots.ListAsync()));

        string child = Directory.CreateDirectory(Path.Combine(selected, "child")).FullName;
        await Assert.ThrowsAsync<InvalidOperationException>(() => enrollment.RegisterAsync(child));
        string sibling = Directory.CreateDirectory(selected + "-sibling").FullName;
        IndexRoot second = await enrollment.RegisterAsync(sibling);
        Assert.Equal(root.VolumeIdentity, second.VolumeIdentity);
        Assert.NotEqual(root.DirectoryIdentity, second.DirectoryIdentity);

        Directory.Move(selected, selected + "-old");
        Directory.CreateDirectory(selected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => enrollment.RegisterAsync(selected));
        Assert.Equal(root, await roots.GetByIdAsync(root.Id));
    }

    [WindowsFact]
    public async Task CancellationWhileWaitingForPersistenceReleasesPinnedAncestorsWithoutInserting()
    {
        string parent = Directory.CreateDirectory(Path.Combine(_tree, "parent")).FullName;
        string selected = Directory.CreateDirectory(Path.Combine(parent, "selected")).FullName;
        using SqliteConnectionFactory db = await CreateDatabaseAsync();
        SqliteIndexRootRepository roots = new(db);
        RootEnrollment enrollment = new(new WindowsRootDirectoryVerifier(), roots);
        using CancellationTokenSource cancellation = new();
        await using (SqliteConnectionLease writer = await db.OpenWriterAsync())
        {
            Task registration = enrollment.RegisterAsync(selected, cancellation.Token);
            Assert.False(registration.IsCompleted);
            Assert.ThrowsAny<IOException>(() => Directory.Move(parent, parent + "-moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(selected, selected + "-moved"));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Directory.Move(parent, parent + "-moved");
        Assert.Empty(await roots.ListAsync());
    }

    [WindowsFact]
    public async Task MissingNonDirectoryAndDeniedListAccessNeverEnroll()
    {
        using SqliteConnectionFactory db = await CreateDatabaseAsync();
        SqliteIndexRootRepository roots = new(db);
        RootEnrollment enrollment = new(new WindowsRootDirectoryVerifier(), roots);
        string missing = Path.Combine(_tree, "missing");
        await Assert.ThrowsAsync<IOException>(() => enrollment.RegisterAsync(missing));
        await File.WriteAllTextAsync(missing, "synthetic");
        await Assert.ThrowsAsync<IOException>(() => enrollment.RegisterAsync(missing));

        DirectoryInfo selected = Directory.CreateDirectory(Path.Combine(_tree, "denied"));
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        DirectorySecurity denied = selected.GetAccessControl();
        FileSystemAccessRule denyList = new(identity.User ?? throw new InvalidOperationException("Missing owner SID."),
            FileSystemRights.ListDirectory, AccessControlType.Deny);
        denied.AddAccessRule(denyList);
        selected.SetAccessControl(denied);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => enrollment.RegisterAsync(selected.FullName));
        }
        finally
        {
            denied.RemoveAccessRuleSpecific(denyList);
            selected.SetAccessControl(denied);
        }

        Assert.Empty(await roots.ListAsync());
    }

    [WindowsTheory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\synthetic\..\outside")]
    [InlineData(@"\\synthetic\share")]
    [InlineData(@"\\?\C:\synthetic")]
    public void UnsupportedNamesAreRejectedBeforeOpeningAnyDirectory(string path)
    {
        Assert.Throws<FormatException>(() => new WindowsRootDirectoryVerifier().Open(path, CancellationToken.None));
    }

    [WindowsFact]
    public async Task ReparseSelectionAndIntermediateReparseDirectoryAreRejected()
    {
        string target = Directory.CreateDirectory(Path.Combine(_tree, "target")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "child"));
        string link = Path.Combine(_tree, "link");
        // Unlike symbolic links, junctions do not require Developer Mode or elevation.
        ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c mklink /J link target")
        {
            WorkingDirectory = _tree,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using Process mklink = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the synthetic junction fixture.");
        await mklink.WaitForExitAsync();
        Assert.Equal(0, mklink.ExitCode);
        try
        {
            using SqliteConnectionFactory db = await CreateDatabaseAsync();
            SqliteIndexRootRepository roots = new(db);
            RootEnrollment enrollment = new(new WindowsRootDirectoryVerifier(), roots);
            await Assert.ThrowsAsync<IOException>(() => enrollment.RegisterAsync(link));
            await Assert.ThrowsAsync<IOException>(() => enrollment.RegisterAsync(Path.Combine(link, "child")));
            Assert.Empty(await roots.ListAsync());
        }
        finally
        {
            Directory.Delete(link, recursive: false);
        }
    }

    [WindowsFact]
    public async Task FullSelectionAllowsRepeatButRejectsAdditionalRoot()
    {
        using SqliteConnectionFactory db = await CreateDatabaseAsync();
        SqliteIndexRootRepository roots = new(db);
        RootEnrollment enrollment = new(new WindowsRootDirectoryVerifier(), roots);
        string first = Directory.CreateDirectory(Path.Combine(_tree, "group", "first")).FullName;
        IndexRoot initial = await enrollment.RegisterAsync(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => enrollment.RegisterAsync(Path.Combine(_tree, "group")));
        for (int i = 1; i < IndexingConfiguration.MaximumSelectedFolders; i++)
        {
            await enrollment.RegisterAsync(Directory.CreateDirectory(Path.Combine(_tree, $"sibling-{i}")).FullName);
        }

        Assert.Equal(initial, await enrollment.RegisterAsync(first));
        string extra = Directory.CreateDirectory(Path.Combine(_tree, "extra")).FullName;
        await Assert.ThrowsAsync<InvalidOperationException>(() => enrollment.RegisterAsync(extra));
        Assert.Equal(IndexingConfiguration.MaximumSelectedFolders, (await roots.ListAsync()).Count);
    }

    private async Task<SqliteConnectionFactory> CreateDatabaseAsync()
    {
        SqliteConnectionFactory db = await SqliteConnectionFactory.CreateAsync(Path.Combine(_tree, "db"));
        await SqliteMigrationRunner.ApplyAsync(db);
        return db;
    }

    public void Dispose() => Directory.Delete(_tree, recursive: true);
}
