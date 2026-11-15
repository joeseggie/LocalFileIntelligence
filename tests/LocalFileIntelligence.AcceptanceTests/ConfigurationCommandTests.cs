using System.Text;
using LocalFileIntelligence.WindowsService;

namespace LocalFileIntelligence.AcceptanceTests;

public sealed class ConfigurationCommandTests
{
    [Fact]
    public async Task ValidConfigurationChecksSyntaxWithoutRequiringSelectedFoldersToExist()
    {
        using ConfigurationFile file = new(
            """{"schemaVersion":1,"selectedFolders":["Z:\\Synthetic\\NotEnrolled"]}""");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigurationCommand.RunAsync(["--validate-config", file.Path], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("Selected folders: 1. No indexing was performed.", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("{", "CONFIG_JSON_ERROR")]
    [InlineData("""{"schemaVersion":2,"selectedFolders":[]}""", "CONFIG_POLICY_ERROR")]
    [InlineData("""{"schemaVersion":1,"selectedFolders":["private-synthetic-value"]}""", "CONFIG_POLICY_ERROR")]
    public async Task InvalidConfigurationReturnsAnExplicitRedactedError(string json, string code)
    {
        using ConfigurationFile file = new(json);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigurationCommand.RunAsync(["--validate-config", file.Path], output, error);

        Assert.Equal(2, exitCode);
        Assert.Contains(code, error.ToString());
        Assert.DoesNotContain("private-synthetic-value", error.ToString());
        Assert.DoesNotContain(file.Path, error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task MissingConfigurationIsAnIoFailureNotAnEmptySelection()
    {
        string missingPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lfi-missing-{Guid.NewGuid():N}.json");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigurationCommand.RunAsync(["--validate-config", missingPath], output, error);

        Assert.Equal(3, exitCode);
        Assert.Contains("CONFIG_IO_ERROR", error.ToString());
        Assert.DoesNotContain(missingPath, error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OversizedConfigurationIsRejectedBeforeParsing()
    {
        using ConfigurationFile file = new(new string(' ', 65537));
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigurationCommand.RunAsync(["--validate-config", file.Path], output, error);

        Assert.Equal(2, exitCode);
        Assert.Contains("CONFIG_TOO_LARGE", error.ToString());
    }

    [Fact]
    public async Task AcceptsAnExplicitUtf8Bom()
    {
        using ConfigurationFile file = new("""{"schemaVersion":1,"selectedFolders":[]}""", emitBom: true);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigurationCommand.RunAsync(["--validate-config", file.Path], output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RejectsInvalidUtf8InsideAPathWithoutCrashing()
    {
        using ConfigurationFile file = new("{}");
        byte[] json = Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"selectedFolders":["C:\\Synthetic\\badX"]}""");
        json[Array.IndexOf(json, (byte)'X')] = 0xFF;
        File.WriteAllBytes(file.Path, json);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigurationCommand.RunAsync(["--validate-config", file.Path], output, error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"selectedFolders":["C:\\Synthetic\\\uD800"]}""")]
    [InlineData("""{"schemaVersion":1,"selectedFolders":[],"\uD800":true}""")]
    public async Task RejectsInvalidUnicodeEscapesWithoutCrashing(string json)
    {
        using ConfigurationFile file = new(json);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigurationCommand.RunAsync(["--validate-config", file.Path], output, error);

        Assert.Equal(2, exitCode);
        Assert.NotEmpty(error.ToString());
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("--scan")]
    [InlineData("--install-service")]
    [InlineData("--validate-config")]
    public async Task UnsupportedOrIncompleteCommandsCannotStartIndexing(string argument)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await ConfigurationCommand.RunAsync([argument], output, error);

        Assert.Equal(2, exitCode);
        Assert.Contains("USAGE_ERROR", error.ToString());
    }

    private sealed class ConfigurationFile : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"lfi-config-test-{Guid.NewGuid():N}");

        public ConfigurationFile(string json, bool emitBom = false)
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "config.json");
            File.WriteAllText(Path, json, new UTF8Encoding(emitBom));
        }

        public string Path { get; }

        public void Dispose()
        {
            File.Delete(Path);
            Directory.Delete(directory);
        }
    }
}
