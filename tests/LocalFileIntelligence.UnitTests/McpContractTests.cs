using System.Text.Json;
using Json.Schema;

namespace LocalFileIntelligence.UnitTests;

public sealed class McpContractTests
{
    [Fact]
    public void PublishedExamplesConformToTheirActualInputAndOutputSchemas()
    {
        using JsonDocument catalogue = ReadFixture("tools.json");
        using JsonDocument examples = ReadFixture("examples.json");
        JsonElement[] tools = catalogue.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        HashSet<string> exercised = new(StringComparer.Ordinal);

        foreach (JsonElement example in examples.RootElement.GetProperty("examples").EnumerateArray())
        {
            string name = example.GetProperty("tool").GetString() ?? throw new InvalidDataException("Missing tool name.");
            JsonElement tool = Assert.Single(tools, tool => tool.GetProperty("name").GetString() == name);
            JsonSchema input = BuildSchema(tool.GetProperty("inputSchema"));
            JsonSchema output = BuildSchema(tool.GetProperty("outputSchema"));

            Assert.True(input.Evaluate(example.GetProperty("arguments")).IsValid, $"{name} input example is invalid.");
            Assert.True(output.Evaluate(example.GetProperty("structuredContent")).IsValid, $"{name} output example is invalid.");
            exercised.Add(name);
        }

        foreach (JsonElement tool in tools)
        {
            string name = tool.GetProperty("name").GetString() ?? throw new InvalidDataException("Missing tool name.");
            Assert.Contains(name, exercised);
        }
    }

    [Theory]
    [InlineData("search_files", """{"query":"notes","limit":101}""")]
    [InlineData("search_files", """{"query":"notes","offset":-1}""")]
    [InlineData("search_files", """{"query":"notes","rootIds":[]}""")]
    [InlineData("search_files", """{"query":"notes","extensions":["*.txt"]}""")]
    [InlineData("search_content", """{"query":"notes","offset":10001}""")]
    [InlineData("get_file_metadata", """{"fileId":"C:\\Synthetic\\notes.txt"}""")]
    [InlineData("read_file", """{"fileId":1,"path":"C:\\Synthetic\\notes.txt"}""")]
    [InlineData("read_file", """{"fileId":1,"maxBytes":65537}""")]
    [InlineData("list_directory", """{"rootId":1,"relativePath":"..\\Outside"}""")]
    [InlineData("list_directory", """{"rootId":1,"relativePath":"C:\\Synthetic"}""")]
    [InlineData("get_directory_tree", """{"rootId":1,"depth":9}""")]
    [InlineData("find_duplicates", """{"minimumSize":-1}""")]
    [InlineData("list_index_roots", """{"includeAllUsers":true}""")]
    [InlineData("get_index_status", """{"rootPath":"C:\\Synthetic"}""")]
    public void RejectsOutOfContractArguments(string name, string json)
    {
        using JsonDocument catalogue = ReadFixture("tools.json");
        JsonElement tool = Assert.Single(catalogue.RootElement.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == name);
        using JsonDocument arguments = JsonDocument.Parse(json);

        Assert.False(BuildSchema(tool.GetProperty("inputSchema")).Evaluate(arguments.RootElement).IsValid);
    }

    [Fact]
    public void AllToolsAcceptExplicitErrorsButRejectMixedSuccessAndError()
    {
        using JsonDocument catalogue = ReadFixture("tools.json");
        using JsonDocument error = JsonDocument.Parse(
            """
            {"ok":false,"error":{"code":"SERVICE_UNAVAILABLE","message":"Service is not ready.","retryable":true,
             "correlationId":"33333333-3333-4333-8333-333333333333"}}
            """);
        using JsonDocument mixed = JsonDocument.Parse(
            """
            {"ok":false,"data":{},"error":{"code":"SERVICE_UNAVAILABLE","message":"Service is not ready.","retryable":true,
             "correlationId":"33333333-3333-4333-8333-333333333333"}}
            """);

        foreach (JsonElement tool in catalogue.RootElement.GetProperty("tools").EnumerateArray())
        {
            JsonSchema schema = BuildSchema(tool.GetProperty("outputSchema"));
            Assert.True(schema.Evaluate(error.RootElement).IsValid);
            Assert.False(schema.Evaluate(mixed.RootElement).IsValid);
        }
    }

    private static JsonDocument ReadFixture(string name)
    {
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
    }

    [Fact]
    public void RecentActivityContractRejectsRawFileContent()
    {
        using JsonDocument definition = ReadFixture("recent-activity.schema.json");
        using JsonDocument activity = JsonDocument.Parse(
            """
            {"events":[{"occurredAt":"2026-01-01T00:00:00.000Z","operation":"read_file","outcome":"allowed",
            "errorCode":null,"correlationId":"33333333-3333-4333-8333-333333333333","fileContents":"private"}]}
            """);

        Assert.False(BuildSchema(definition.RootElement).Evaluate(activity.RootElement).IsValid);
    }

    private static JsonSchema BuildSchema(JsonElement schema)
    {
        return JsonSchema.Build(schema.Clone(), new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new()
        });
    }
}
