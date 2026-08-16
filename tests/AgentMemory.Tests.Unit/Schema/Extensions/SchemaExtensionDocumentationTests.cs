using AgentMemory.Neo4j.Schema.Extensions;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Schema.Extensions;

/// <summary>
/// Every extension ships a handover page, and that page cannot silently drift from the code.
/// </summary>
/// <remarks>
/// <para>
/// The page is the unit that goes to Neo4j: shape, Cypher, semantics, conformance, parity delta. A doc
/// that is merely *encouraged* is a doc that describes last quarter's schema, and this one is meant to
/// be read by people who cannot check it against the source.
/// </para>
/// <para>
/// The drift check is deliberately narrow — it asserts that every entry of the machine-readable
/// <see cref="SchemaParityDelta"/> and every declared shape is *named somewhere on the page*. It cannot
/// verify prose, and pretending otherwise would be worse than not checking: what it does guarantee is
/// that a divergence added in code cannot ship undocumented.
/// </para>
/// </remarks>
public sealed class SchemaExtensionDocumentationTests
{
    // CreateShipped(), NOT a hand-listed array. This guard was written against a literal
    // [new ProceduralSchemaExtension()] and therefore silently stopped covering each new extension the
    // moment one was added -- working-memory shipped a page that this test never once read. A guard that
    // enumerates its own subjects by hand grades whatever it was born knowing about.
    private static readonly SchemaExtensionRegistry Registry = new(SchemaExtensionRegistry.CreateShipped());

    private static string DocumentationRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
        return Path.Combine(root, "docs", "extensions");
    }

    public static TheoryData<string> ExtensionIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in Registry.KnownIds) data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(ExtensionIds))]
    public void EveryExtensionHasAHandoverPage(string id)
    {
        File.Exists(Path.Combine(DocumentationRoot(), id + ".md")).Should().BeTrue(
            $"extension '{id}' must ship docs/extensions/{id}.md — the handover unit");
    }

    [Theory]
    [MemberData(nameof(ExtensionIds))]
    public void EveryHandoverPageCarriesTheFixedSections(string id)
    {
        var page = File.ReadAllText(Path.Combine(DocumentationRoot(), id + ".md"));

        foreach (var section in new[] { "## Shape", "## Cypher", "## Semantics", "## Conformance", "## Parity delta" })
            page.Should().Contain(section, $"'{id}' page must keep the fixed section layout");
    }

    [Theory]
    [MemberData(nameof(ExtensionIds))]
    public void EveryParityDeltaEntryIsNamedOnThePage(string id)
    {
        // THE drift guard. A divergence added in code and not on the page is a divergence Neo4j reads
        // about nowhere -- the exact situation trace_kind was in before this system existed.
        var extension = Registry.Find(id)!;
        var page = File.ReadAllText(Path.Combine(DocumentationRoot(), id + ".md"));
        var delta = extension.ParityDelta;

        var entries = delta.AddNetOnlyLabels
            .Concat(delta.AddNetOnlyRelationshipTypes)
            .Concat(delta.AddNetSupersetProperties)
            .Concat(delta.RemoveUpstreamOnlyLabels)
            .Concat(delta.ReserveUpstreamPropertyNames);

        foreach (var entry in entries)
            page.Should().Contain(entry, $"'{id}' declares parity entry '{entry}', which the page must name");
    }

    [Theory]
    [MemberData(nameof(ExtensionIds))]
    public void EveryDeclaredShapeIsNamedOnThePage(string id)
    {
        var extension = Registry.Find(id)!;
        var page = File.ReadAllText(Path.Combine(DocumentationRoot(), id + ".md"));

        foreach (var shape in extension.DeclaredLabels
                     .Concat(extension.DeclaredRelationshipTypes)
                     .Concat(extension.DeclaredProperties.Values.SelectMany(p => p))
                     .Concat(extension.MigrationScripts)
                     .Concat(extension.BaseResidentMigrations))
        {
            page.Should().Contain(shape, $"'{id}' declares '{shape}', which the page must name");
        }
    }

    [Theory]
    [MemberData(nameof(ExtensionIds))]
    public void TheIndexLinksEveryExtensionPage(string id)
    {
        // A page nothing links to is a page nobody finds -- the documentation form of the
        // ship-but-unreachable defect.
        File.ReadAllText(Path.Combine(DocumentationRoot(), "README.md"))
            .Should().Contain($"({id}.md)", $"the extensions index must link '{id}'");
    }
}
