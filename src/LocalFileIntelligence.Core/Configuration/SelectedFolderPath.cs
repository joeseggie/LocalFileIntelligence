namespace LocalFileIntelligence.Core.Configuration;

internal static class SelectedFolderPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"
    };

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767)
        {
            throw new FormatException("A folder path must contain 1-32767 characters.");
        }

        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            throw new FormatException("Select a local drive-letter directory, not a drive root or network/device path.");
        }

        string normalized = path.EndsWith('\\') ? path[..^1] : path;
        foreach (string component in normalized[3..].Split('\\'))
        {
            if (component.Length is 0 or > 255
                || component is "." or ".."
                || component.EndsWith(' ')
                || component.EndsWith('.')
                || component.Any(static character => char.IsControl(character) || "\"<>|:?*/".Contains(character))
                || IsReserved(component))
            {
                throw new FormatException("A folder contains an unsupported Windows path component.");
            }
        }

        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    public static bool Overlaps(string first, string second)
    {
        return first.Equals(second, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(second + '\\', StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + '\\', StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReserved(string component)
    {
        string stem = component.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (stem[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3');
    }
}
