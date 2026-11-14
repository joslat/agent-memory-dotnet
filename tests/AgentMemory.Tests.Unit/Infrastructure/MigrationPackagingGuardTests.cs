using System.Xml.Linq;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// How the Cypher migrations reach an installed application. The runner reads the copies embedded in AgentMemory.Neo4j,
/// which every install path gets; the content files (copied to the output, <c>PackageCopyToOutput</c>) reach only an
/// application that references AgentMemory.Neo4j directly, because NuGet does not pass a dependency's content files on,
/// and the repository's own tests read them. Repository tests use project references and cannot see either gap; these
/// guards can, and eng/verify-packages-install.sh checks the packed meta-package end to end.
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

    /// <summary>
    /// The runner reads the migrations embedded in the assembly, so every way of installing the package gets them: the
    /// meta-package AgentMemory, which the README installs, passes no content files on (NuGet replaces them with a
    /// placeholder), and 2.2.0's consumers of it got no schema at all.
    /// </summary>
    [Fact]
    public async Task Every_shipped_migration_is_embedded_in_the_assembly_word_for_word()
    {
        var folder = Path.Combine(RepoRoot(), "src", "AgentMemory.Neo4j", "Schema", "Migrations");
        var onDisk = Directory.GetFiles(folder, "*.cypher", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .OrderBy(name => name, StringComparer.Ordinal).ToList();

        EmbeddedMigrationScripts.Shipped.Names().Should().Equal(onDisk);
        foreach (var name in onDisk)
            (await EmbeddedMigrationScripts.Shipped.ReadAsync(name, default)).Should()
                .Be(await File.ReadAllTextAsync(Path.Combine(folder, name)), "the embedded copy of {0} is the shipped file", name);
    }

    [Fact]
    public void Without_a_folder_next_to_the_app_the_runner_plans_every_shipped_migration()
    {
        var folder = Path.Combine(RepoRoot(), "src", "AgentMemory.Neo4j", "Schema", "Migrations");
        var runner = new MigrationRunner(Substitute.For<INeo4jTransactionRunner>(), NullLogger<MigrationRunner>.Instance,
            EmbeddedMigrationScripts.Shipped, [new IdentityEvidenceSchemaExtension()]);

        var plan = runner.DiscoverMigrations();

        plan.Where(step => step.ExtensionId is null).Select(step => step.Version).Should().Equal(
            Directory.GetFiles(folder, "*.cypher").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));
        plan.Where(step => step.ExtensionId == "identity-evidence").Select(step => step.Version).Should().Equal(
            "ext/identity-evidence/0001_identity_evidence", "ext/identity-evidence/0002_identity_decisions",
            "ext/identity-evidence/0003_identity_owner_stamps", "ext/identity-evidence/0004_identity_relationship_observations");
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
