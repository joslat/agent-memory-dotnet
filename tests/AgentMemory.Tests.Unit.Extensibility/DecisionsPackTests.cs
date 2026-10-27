using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Decisions;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The decisions pack (coding packs CMP-3): decisions read deterministically from ADR files and Squad's log, each applying to
/// the files and types its text mentions, so the code in hand brings it up.
/// </summary>
public sealed class DecisionsPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "decisions-" + Guid.NewGuid().ToString("N"));

    private const string Squad = """
        # Squad Decisions

        ## Active Decisions

        ### 2026-05-08T09:26:42.925+02:00: User directive
        **By:** Owner
        Never delete pack records: `Neo4jModuleStore` closes them, history kept.

        ### 2026-04-30: Round 4 — DELETE_SESSION_DATA Gap review
        Reviewed the change; all good.

        ### D-WAVE2: Pipeline SRP Split (Roy, 2026-07-18)

        #### Context
        The pipeline did two jobs.

        #### Decision: Split MemoryExtractionPipeline into stages
        Extraction and persistence become separate stages in src/Pipeline/.

        #### Rationale
        Each stage is testable alone.

        ### 2026-03-01: Old storage rule
        **Status:** Superseded by the user directive above.
        Delete records on removal in ModuleStore.cs.
        """;

    private const string Adr = """
        # ADR-007: Pack removal closes records

        * Status: accepted
        * Date: 2026-10-20

        ## Context
        Removing a pack must keep as-of reads working.

        ## Decision
        Removal closes every record of the pack; nothing is deleted. See src/Store/ModuleStore.cs.

        ## Consequences
        Stores grow; history stays.
        """;

    public DecisionsPackTests()
    {
        Write(".squad/decisions.md", Squad);
        Write("docs/adr/0007-pack-removal.md", Adr);
        Write("docs/adr/README.md", "# Decisions\nAn index.");
        Write("src/Store/ModuleStore.cs", "namespace S; public sealed class Neo4jModuleStore { }");
        Write("src/Pipeline/Stages.cs", "namespace S; public sealed class MemoryExtractionPipeline { }");
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly string[] Files =
        [".squad/decisions.md", "docs/adr/0007-pack-removal.md", "docs/adr/README.md", "src/Store/ModuleStore.cs", "src/Pipeline/Stages.cs"];

    [Fact]
    public void Decisions_are_read_from_adr_files_and_the_squad_log_and_apply_to_the_code_they_mention()
    {
        var code = CodeIndex.Scan(_root, Files);
        var sources = DecisionsReader.DecisionFiles(Files);
        sources.Should().Equal(".squad/decisions.md", "docs/adr/0007-pack-removal.md");

        var snapshot = DecisionsReader.Read(_root, sources, code, "git:abc");

        snapshot.Problems().Should().BeEmpty();
        var byTitle = snapshot.Records.ToDictionary(r => r.Properties["title"].GetString()!);
        byTitle.Keys.Should().BeEquivalentTo(["User directive", "Pipeline SRP Split", "Old storage rule", "Pack removal closes records"],
            "a review that decides nothing is not a decision");

        var directive = byTitle["User directive"];
        directive.Properties["mentions"].GetString().Should().Be("Neo4jModuleStore");
        directive.Properties["appliesTo"].GetString().Should().Be("src/Store/ModuleStore.cs");
        directive.Properties["by"].GetString().Should().Be("Owner");
        directive.Properties["status"].GetString().Should().Be("accepted");

        var split = byTitle["Pipeline SRP Split"];
        split.Properties["decision"].GetString().Should().StartWith("Extraction and persistence become separate stages");
        split.Properties["context"].GetString().Should().Be("The pipeline did two jobs.");
        split.Properties["consequences"].GetString().Should().Be("Each stage is testable alone.");
        split.Properties["date"].GetString().Should().Be("2026-07-18");
        split.Properties["appliesTo"].GetString().Should().Be("src/Pipeline/** src/Pipeline/Stages.cs");

        var old = byTitle["Old storage rule"];
        old.Properties["status"].GetString().Should().Be("superseded");
        old.Properties.Should().NotContainKey("appliesTo", "a superseded decision must not come up with the code");

        var adr = byTitle["Pack removal closes records"];
        adr.Key.Should().Be("docs/adr/0007-pack-removal.md#decision");
        adr.Properties["decision"].GetString().Should().StartWith("Removal closes every record");
        adr.Properties["appliesTo"].GetString().Should().Be("src/Store/ModuleStore.cs");
        adr.Properties["date"].GetString().Should().Be("2026-10-20");
    }

    [Fact]
    public void A_decision_comes_up_from_the_file_it_applies_to_or_a_type_it_mentions()
    {
        var snapshot = DecisionsReader.Read(_root, DecisionsReader.DecisionFiles(Files), CodeIndex.Scan(_root, Files), "git:abc");
        var directive = snapshot.Records.Single(r => r.Properties["title"].GetString() == "User directive");

        AnchorMatcher.Best(ModuleAnchorMatch.Glob, directive.Properties["appliesTo"].GetString(), ["src/Store/ModuleStore.cs"]).Quality.Should().Be(1.0);
        AnchorMatcher.Best(ModuleAnchorMatch.Name, directive.Properties["mentions"].GetString(), ["Neo4jModuleStore"]).Quality.Should().Be(1.0);
        DecisionsPack.Manifest.Validate().Should().BeEmpty();
        DecisionsPack.Manifest.Traversal!.Anchors.Select(a => (a.Property, a.Kind)).Should().Equal(
            ("appliesTo", ContextAnchorKind.Path), ("mentions", ContextAnchorKind.Symbol));
    }

    [Fact]
    public void A_decision_that_supersedes_another_links_to_it_and_the_older_one_reads_as_superseded()
    {
        // CP-acceptance: the manifest declared DECISIONS_SUPERSEDES; the reader now writes it, from either side.
        var folder = Path.Combine(_root, "supersession");
        Directory.CreateDirectory(Path.Combine(folder, "docs", "adr", "archive"));
        string Adr(string status, string title, string extra = "") =>
            $"# {title}\n\n## Status\n\n{status}\n\n## Context\n\nWhy.\n\n## Decision\n\nWe use src/Store.cs for this.{extra}\n";
        File.WriteAllText(Path.Combine(folder, "docs", "adr", "0001-cache-per-owner.md"), Adr("Accepted", "1. Cache per owner"));
        File.WriteAllText(Path.Combine(folder, "docs", "adr", "0002-cache-per-store.md"), Adr("Accepted", "2. Cache per store", "\n\nSupersedes ADR-0001."));
        File.WriteAllText(Path.Combine(folder, "docs", "adr", "archive", "0003-old-index.md"), Adr("Superseded by [the new index](../0004-new-index.md)", "3. Old index"));
        File.WriteAllText(Path.Combine(folder, "docs", "adr", "0004-new-index.md"), Adr("Accepted", "4. New index"));
        File.WriteAllText(Path.Combine(folder, "docs", "adr", "0005-unrelated.md"), Adr("Accepted", "5. Unrelated", "\n\nSupersedes ADR-0099, which this repository never had."));
        string[] files = ["docs/adr/0001-cache-per-owner.md", "docs/adr/0002-cache-per-store.md", "docs/adr/archive/0003-old-index.md", "docs/adr/0004-new-index.md", "docs/adr/0005-unrelated.md"];

        var snapshot = DecisionsReader.Read(folder, files, CodeIndex.Scan(folder, ["src/Store.cs"]), "git:abc");

        snapshot.Problems().Should().BeEmpty();
        var byKey = snapshot.Records.ToDictionary(r => r.Key);
        byKey["docs/adr/0002-cache-per-store.md#decision"].Links.Should().ContainSingle(l => l.Relation == DecisionsPack.Supersedes && l.Target == "docs/adr/0001-cache-per-owner.md#decision");
        byKey["docs/adr/0004-new-index.md#decision"].Links.Should().ContainSingle(l => l.Relation == DecisionsPack.Supersedes && l.Target == "docs/adr/archive/0003-old-index.md#decision");
        byKey["docs/adr/0001-cache-per-owner.md#decision"].Properties["status"].GetString().Should().Be("superseded", "a later decision replaced it, whatever its file still says");
        byKey["docs/adr/0001-cache-per-owner.md#decision"].Properties.Should().NotContainKey("appliesTo");
        byKey["docs/adr/0005-unrelated.md#decision"].Links.Should().BeEmpty("a reference to nothing this read holds is left alone");
    }
}
