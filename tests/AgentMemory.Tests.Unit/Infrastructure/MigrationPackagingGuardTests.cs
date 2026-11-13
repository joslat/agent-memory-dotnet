using System.Xml.Linq;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// The Cypher migrations ship as NuGet content files. Without <c>PackageCopyToOutput</c> a consumer's build never copies
/// them next to its assembly, so <c>MigrationRunner</c> finds no files and the schema, the identity-evidence extension
/// included, is silently skipped. Repository tests use project references and cannot see this; this guard can.
/// </summary>
public sealed class MigrationPackagingGuardTests
{
    [Fact]
    public void Every_migration_content_item_is_copied_to_the_consumers_output()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot(), "src", "AgentMemory.Neo4j", "AgentMemory.Neo4j.csproj"));
        var migrations = project.Descendants()
            .Where(item => item.Name.LocalName == "Content" &&
                           ((string?)item.Attribute("Include"))?.Replace('/', '\\').StartsWith(@"Schema\Migrations", StringComparison.Ordinal) == true)
            .ToList();

        migrations.Should().HaveCount(2, "base and extension migrations are packed as two content items");
        migrations.Should().OnlyContain(item => string.Equals((string?)item.Attribute("PackageCopyToOutput"), "true", StringComparison.OrdinalIgnoreCase),
            "a NuGet consumer must receive Schema/Migrations in its output, or every migration is skipped");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentMemory.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run within the repository (AgentMemory.slnx not found above {0})", AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
