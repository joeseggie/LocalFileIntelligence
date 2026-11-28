using System.Buffers;
using System.Text;

namespace LocalFileIntelligence.Core.Configuration;

public static class IndexEntryPath
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"
    };

    public static string Normalize(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 32767)
        {
            throw new FormatException("An entry path must contain 1-32767 characters.");
        }

        ReadOnlySpan<char> remaining = relativePath.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out int charactersConsumed) != OperationStatus.Done)
            {
                throw new FormatException("An entry path must contain valid Unicode.");
            }

            remaining = remaining[charactersConsumed..];
        }

        string normalized = relativePath.Replace('/', '\\');
        if (normalized[0] == '\\' || (normalized.Length >= 2 && normalized[1] == ':'))
        {
            throw new FormatException("An entry path must be relative to its selected root.");
        }

        foreach (string component in normalized.Split('\\'))
        {
            if (component.Length is 0 or > 255
                || component is "." or ".."
                || component.EndsWith(' ')
                || component.EndsWith('.')
                || component.Any(static character =>
                    char.IsControl(character) || "\"<>|:?*".Contains(character))
                || IsReserved(component))
            {
                throw new FormatException("An entry path contains an unsupported Windows path component.");
            }
        }

        return normalized;
    }

    public static string NormalizeParent(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath.Length == 0 ? string.Empty : Normalize(relativePath);
    }

    public static string NormalizeKey(string relativePath)
    {
        return Normalize(relativePath).ToUpperInvariant();
    }

    public static string NormalizeParentKey(string relativePath)
    {
        return NormalizeParent(relativePath).ToUpperInvariant();
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
