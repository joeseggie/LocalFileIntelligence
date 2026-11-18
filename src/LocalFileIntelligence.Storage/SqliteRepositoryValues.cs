using System.Globalization;

namespace LocalFileIntelligence.Storage;

internal static class SqliteRepositoryValues
{
    private const int MaximumErrorMessageLength = 2048;

    public static string FormatTimestamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    public static void ValidateErrorMessage(string? errorMessage)
    {
        if (errorMessage?.Length > MaximumErrorMessageLength)
        {
            throw new ArgumentOutOfRangeException(nameof(errorMessage));
        }
    }

    public static void ValidateRelativePath(string? relativePath)
    {
        if (relativePath is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath[0] is '/' or '\\'
            || (relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':')
            || relativePath.Split(['/', '\\']).Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The scan path must be a non-empty root-relative path.", nameof(relativePath));
        }
    }
}
