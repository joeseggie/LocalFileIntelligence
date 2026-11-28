namespace LocalFileIntelligence.Application.Persistence;

public enum IndexEntryType
{
    File,
    Directory,
}

public enum FileEntryExtractionStatus
{
    Pending,
    Extracted,
    Unsupported,
    Failed,
}

public enum FileEntryIndexingStatus
{
    Pending,
    Indexed,
    Excluded,
    Inaccessible,
    Failed,
}

public sealed record FileEntry(
    long Id,
    long RootId,
    string RelativePath,
    string PathKey,
    string ParentPathKey,
    string Name,
    string Extension,
    IndexEntryType EntryType,
    long SizeBytes,
    DateTimeOffset CreatedAtFs,
    DateTimeOffset ModifiedAtFs,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastIndexedAt,
    string? Sha256,
    FileEntryExtractionStatus ExtractionStatus,
    FileEntryIndexingStatus IndexingStatus,
    bool IsDeleted,
    DateTimeOffset? DeletedAt,
    string? VolumeFileIdentity,
    long Revision,
    long LastSeenGeneration);

public sealed record FileEntryObservation(
    string RelativePath,
    IndexEntryType EntryType,
    long SizeBytes,
    DateTimeOffset CreatedAtFs,
    DateTimeOffset ModifiedAtFs,
    DateTimeOffset LastSeenAt,
    string? VolumeFileIdentity = null,
    long LastSeenGeneration = 0);

public interface IFileEntryRepository
{
    Task<FileEntry?> GetByIdAsync(long rootId, long id, CancellationToken cancellationToken = default);

    Task<FileEntry?> GetByPathAsync(
        long rootId,
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileEntry>> ListChildrenAsync(
        long rootId,
        string parentRelativePath,
        string? afterRelativePath,
        int limit,
        CancellationToken cancellationToken = default);

    Task<FileEntry> UpsertAsync(
        long rootId,
        FileEntryObservation observation,
        CancellationToken cancellationToken = default);

    Task<FileEntry?> RenameAsync(
        long rootId,
        string oldRelativePath,
        FileEntryObservation observation,
        CancellationToken cancellationToken = default);

    Task<FileEntry?> TombstoneAsync(
        long rootId,
        string relativePath,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default);
}
