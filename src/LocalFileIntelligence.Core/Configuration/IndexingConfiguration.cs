using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace LocalFileIntelligence.Core.Configuration;

public sealed class IndexingConfiguration
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private IndexingConfiguration(ReadOnlyCollection<string> selectedFolders)
    {
        SelectedFolders = selectedFolders;
    }

    public IReadOnlyList<string> SelectedFolders { get; }

    public static IndexingConfiguration Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return ParseRoot(document.RootElement);
    }

    public static IndexingConfiguration Parse(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            return Parse(StrictUtf8.GetString(utf8Json.Span));
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonException("Configuration must be valid UTF-8.", exception);
        }
    }

    private static IndexingConfiguration ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Configuration must be a JSON object.");
        }

        int? version = null;
        JsonElement folders = default;
        HashSet<string> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            string name = ReadPropertyName(property);
            if (!properties.Add(name))
            {
                throw new FormatException("Configuration contains a duplicate property.");
            }

            switch (name)
            {
                case "schemaVersion":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int value))
                    {
                        throw new FormatException("schemaVersion must be integer 1.");
                    }

                    version = value;
                    break;
                case "selectedFolders":
                    folders = property.Value;
                    break;
                default:
                    throw new FormatException("Configuration contains an unsupported property.");
            }
        }

        if (version != 1)
        {
            throw new FormatException("schemaVersion is required and must be integer 1.");
        }

        if (folders.ValueKind != JsonValueKind.Array || folders.GetArrayLength() > 32)
        {
            throw new FormatException("selectedFolders is required and must be an array of at most 32 paths.");
        }

        List<string> selected = [];
        foreach (JsonElement folder in folders.EnumerateArray())
        {
            if (folder.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("Every selectedFolders entry must be a path string.");
            }

            string path = SelectedFolderPath.Normalize(
                folder.Deserialize<string>() ?? throw new FormatException("A folder path cannot be null."));
            if (selected.Any(existing => SelectedFolderPath.Overlaps(existing, path)))
            {
                throw new FormatException("Selected folders must not duplicate or overlap each other.");
            }

            selected.Add(path);
        }

        return new IndexingConfiguration(selected.AsReadOnly());
    }

    private static string ReadPropertyName(JsonProperty property)
    {
        try
        {
            return property.Name;
        }
        catch (InvalidOperationException exception)
        {
            throw new FormatException("Configuration contains an invalid Unicode property name.", exception);
        }
    }
}
