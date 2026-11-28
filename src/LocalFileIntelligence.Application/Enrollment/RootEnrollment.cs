using LocalFileIntelligence.Application.Persistence;

namespace LocalFileIntelligence.Application.Enrollment;

public interface IVerifiedRootDirectory : IDisposable
{
    string Path { get; }
    string VolumeIdentity { get; }
    string DirectoryIdentity { get; }
}

public interface IRootDirectoryVerifier
{
    IVerifiedRootDirectory Open(string selectedPath, CancellationToken cancellationToken);
}

public sealed class RootEnrollment(IRootDirectoryVerifier verifier, IIndexRootRepository roots)
{
    public async Task<IndexRoot> RegisterAsync(string selectedPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IVerifiedRootDirectory directory = verifier.Open(selectedPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await roots.AddAsync(new IndexRootRegistration(
            directory.Path,
            directory.Path[(directory.Path.LastIndexOf('\\') + 1)..],
            directory.VolumeIdentity,
            directory.DirectoryIdentity,
            DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }
}
