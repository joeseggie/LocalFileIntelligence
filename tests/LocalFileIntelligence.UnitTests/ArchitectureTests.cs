using System.Xml.Linq;

namespace LocalFileIntelligence.UnitTests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("LocalFileIntelligence.Desktop")]
    [InlineData("LocalFileIntelligence.McpServer")]
    public void QueryClientsCannotReachStorageOrServiceProjects(string client)
    {
        Dictionary<string, XDocument> projects = Directory
            .GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Projects"), "*.csproj")
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), XDocument.Load);
        HashSet<string> visited = new(StringComparer.Ordinal);
        Queue<string> pending = new([client]);

        while (pending.TryDequeue(out string? project))
        {
            if (!visited.Add(project))
            {
                continue;
            }

            Assert.DoesNotContain(project, new[]
            {
                "LocalFileIntelligence.Storage",
                "LocalFileIntelligence.WindowsService",
                "LocalFileIntelligence.Indexer"
            });
            foreach (XElement package in projects[project].Descendants("PackageReference"))
            {
                string name = package.Attribute("Include")?.Value ?? throw new InvalidDataException("Missing package name.");
                Assert.False(name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase), "Query clients cannot depend on SQLite.");
            }

            foreach (XElement reference in projects[project].Descendants("ProjectReference"))
            {
                string path = reference.Attribute("Include")?.Value ?? throw new InvalidDataException("Missing project reference.");
                string normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar);
                pending.Enqueue(Path.GetFileNameWithoutExtension(normalizedPath));
            }
        }
    }
}
