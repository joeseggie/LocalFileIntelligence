using System.Globalization;

namespace LocalFileIntelligence.Application.Persistence;

public enum IndexRootStatus
{
    Pending,
    Scanning,
    Ready,
    Paused,
    Offline,
    AccessDenied,
    Error,
}

public sealed record IndexRoot(
    long Id,
    string Path,
    string PathKey,
    string DisplayName,
    bool Enabled,
    IndexRootStatus Status,
    string VolumeIdentity,
    string DirectoryIdentity,
    long PolicyRevision,
    long IndexRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastScanAt,
    string? LastError);

public sealed record IndexRootRegistration(
    string Path,
    string DisplayName,
    string VolumeIdentity,
    string DirectoryIdentity,
    DateTimeOffset CreatedAt);

public interface IIndexRootRepository
{
    Task<IndexRoot> AddAsync(IndexRootRegistration registration, CancellationToken cancellationToken = default);

    Task<IndexRoot?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexRoot>> ListAsync(CancellationToken cancellationToken = default);

    Task<IndexRoot?> UpdateDetailsAsync(
        long id,
        string displayName,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<IndexRoot?> UpdateStateAsync(
        long id,
        bool enabled,
        IndexRootStatus status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default);
}

public static class UtcTimestamp
{
    private const string FormatString = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString(FormatString, CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!DateTimeOffset.TryParseExact(
                value,
                FormatString,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestamp))
        {
            throw new FormatException("A timestamp must be a valid UTC ISO 8601 value with millisecond precision.");
        }

        return timestamp;
    }
}
