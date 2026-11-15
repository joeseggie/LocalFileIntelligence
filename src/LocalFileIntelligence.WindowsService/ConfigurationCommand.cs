using System.Text.Json;
using LocalFileIntelligence.Core.Configuration;

namespace LocalFileIntelligence.WindowsService;

public static class ConfigurationCommand
{
    private const int MaximumConfigurationBytes = 64 * 1024;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count == 0 || (arguments.Count == 1 && arguments[0] == "--help"))
        {
            await output.WriteLineAsync(
                """
                Local File Intelligence - Phase 0 configuration validator
                Usage: --validate-config <UTF-8 JSON file>
                No folders are opened or indexed. No service is installed.
                """);
            return 0;
        }

        if (arguments.Count != 2 || arguments[0] != "--validate-config")
        {
            await error.WriteLineAsync("USAGE_ERROR: Use --validate-config <file> or --help.");
            return 2;
        }

        try
        {
            await using FileStream stream = new(
                arguments[1], FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] bytes = new byte[MaximumConfigurationBytes + 1];
            int count = await stream.ReadAtLeastAsync(
                bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken);
            if (count > MaximumConfigurationBytes)
            {
                await error.WriteLineAsync("CONFIG_TOO_LARGE: Configuration must not exceed 64 KiB.");
                return 2;
            }

            int offset = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            IndexingConfiguration configuration = IndexingConfiguration.Parse(bytes.AsMemory(offset, count - offset));
            await output.WriteLineAsync(
                $"Configuration is valid. Selected folders: {configuration.SelectedFolders.Count}. No indexing was performed.");
            return 0;
        }
        catch (JsonException)
        {
            await error.WriteLineAsync("CONFIG_JSON_ERROR: Configuration must be valid UTF-8 JSON.");
            return 2;
        }
        catch (FormatException exception)
        {
            await error.WriteLineAsync($"CONFIG_POLICY_ERROR: {exception.Message}");
            return 2;
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("CONFIG_PATH_ERROR: Supply a valid configuration file path.");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await error.WriteLineAsync("CONFIG_IO_ERROR: Cannot read the configuration file. Check its path and permissions.");
            return 3;
        }
    }
}
