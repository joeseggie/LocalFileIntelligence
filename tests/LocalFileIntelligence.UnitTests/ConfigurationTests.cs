using System.Text.Json;
using LocalFileIntelligence.Core.Configuration;

namespace LocalFileIntelligence.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void EmptySelectionDoesNotAddImplicitRoots()
    {
        IndexingConfiguration configuration = IndexingConfiguration.Parse(
            """{"schemaVersion":1,"selectedFolders":[]}""");

        Assert.Empty(configuration.SelectedFolders);
    }

    [Fact]
    public void NormalizesDriveAndTrailingSeparatorWithoutOpeningFolders()
    {
        IndexingConfiguration configuration = ParseFolders(@"z:\Synthetic\NotEnrolled\");

        Assert.Equal([@"Z:\Synthetic\NotEnrolled"], configuration.SelectedFolders);
    }

    [Fact]
    public void PrefixSiblingsAreNotOverlappingRoots()
    {
        IndexingConfiguration configuration = ParseFolders(@"C:\Synthetic\Docs", @"C:\Synthetic\Docs2");

        Assert.Equal(2, configuration.SelectedFolders.Count);
    }

    [Theory]
    [InlineData(@"C:\Synthetic\Docs", @"c:\synthetic\DOCS")]
    [InlineData(@"C:\Synthetic\Docs", @"C:\Synthetic\Docs\Nested")]
    [InlineData(@"C:\Synthetic\Docs\Nested", @"C:\Synthetic\Docs")]
    public void RejectsDuplicateOrOverlappingRoots(string first, string second)
    {
        Assert.Throws<FormatException>(() => ParseFolders(first, second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(@"C:\")]
    [InlineData(@"C:Relative")]
    [InlineData(@"\Rooted")]
    [InlineData(@"\\server\share\folder")]
    [InlineData(@"\\?\C:\Synthetic")]
    [InlineData(@"\\.\C:\Synthetic")]
    [InlineData("C:/Synthetic")]
    [InlineData(@"C:\Synthetic\\Docs")]
    [InlineData(@"C:\Synthetic\..\Other")]
    [InlineData(@"C:\Synthetic\.\Docs")]
    [InlineData(@"C:\Synthetic\Docs:stream")]
    [InlineData(@"C:\Synthetic\Docs.")]
    [InlineData(@"C:\Synthetic\Docs ")]
    [InlineData(@"C:\Synthetic\A?B")]
    [InlineData("C:\\Synthetic\\A\nB")]
    [InlineData(@"C:\Synthetic\CON")]
    [InlineData(@"C:\Synthetic\nul.txt")]
    [InlineData(@"C:\Synthetic\COM1")]
    [InlineData(@"C:\Synthetic\LPT9.log")]
    [InlineData("C:\\Synthetic\\COM\u00B9")]
    [InlineData(@"C:\Synthetic\CONIN$")]
    public void RejectsUnsupportedWindowsPathSyntax(string path)
    {
        Assert.Throws<FormatException>(() => ParseFolders(path));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{}""")]
    [InlineData("""{"selectedFolders":[]}""")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":2,"selectedFolders":[]}""")]
    [InlineData("""{"schemaVersion":"1","selectedFolders":[]}""")]
    [InlineData("""{"schemaVersion":1.5,"selectedFolders":[]}""")]
    [InlineData("""{"schemaVersion":1,"selectedFolders":null}""")]
    [InlineData("""{"schemaVersion":1,"selectedFolders":[null]}""")]
    [InlineData("""{"schemaVersion":1,"selectedFolders":[42]}""")]
    [InlineData("""{"schemaVersion":1,"selectedFolders":[],"upload":true}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"selectedFolders":[]}""")]
    [InlineData("""{"schemaVersion":1,"selectedFolders":[],"selectedFolders":[]}""")]
    [InlineData("""{"SchemaVersion":1,"selectedFolders":[]}""")]
    public void RejectsInvalidConfigurationShapes(string json)
    {
        Assert.Throws<FormatException>(() => IndexingConfiguration.Parse(json));
    }

    [Fact]
    public void RejectsMalformedJsonRatherThanUsingEmptyDefaults()
    {
        Assert.ThrowsAny<JsonException>(() => IndexingConfiguration.Parse("{"));
    }

    [Fact]
    public void BoundsFolderCountAndPathComponents()
    {
        string[] roots = Enumerable.Range(0, 33).Select(index => $@"C:\Synthetic\Root{index}").ToArray();

        Assert.Throws<FormatException>(() => ParseFolders(roots));
        Assert.Throws<FormatException>(() => ParseFolders(@"C:\Synthetic\" + new string('a', 256)));
        Assert.Throws<FormatException>(() => ParseFolders(@"C:\" + new string('a', 32768)));
    }

    private static IndexingConfiguration ParseFolders(params string[] folders)
    {
        return IndexingConfiguration.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            selectedFolders = folders
        }));
    }
}
