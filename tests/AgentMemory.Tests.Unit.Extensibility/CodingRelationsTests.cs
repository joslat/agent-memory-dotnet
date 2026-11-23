using System.Text.Json;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.Architecture;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.CodeMap.Roslyn;
using AgentMemory.Packs.Coding;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Intent;
using AgentMemory.Packs.Repository;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The coding ontology's slice 1 relations (CO-2b, from the CO-2 design the owner approved on 22 Nov 2026). A link is stored by
/// the pack whose source says it, whichever way it points in meaning (storage follows evidence): a requirement names its
/// parent, a test names the requirement it validates, a decision the requirement it addresses, a work item the requirement it
/// implements. The family's layer order (intent, decisions, architecture, work, code) carries meaning only: a walk labels a hop
/// why or what by it, as data beside the reason, which does not change.
/// </summary>
public sealed class CodingRelationsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coding-relations-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"));
    }

    private static (string Relation, string Target)[] Links(ModuleProposal record) => [.. record.Links.Select(l => (l.Relation, l.Target))];

    // ---- INTENT_DECOMPOSES -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_requirement_that_names_its_parent_decomposes_it_and_the_link_is_stored_on_the_child()
    {
        Write("REQUIREMENTS.md", """
            - REQ-1: The system shall keep every order.
            - REQ-2: The system shall keep an order for seven years. (parent: REQ-1)
            - REQ-3: The system shall delete an order on request. [parent REQ-1] (accepted)
            - REQ-4: The system shall log each deletion. (parent: REQ-99)
            """);

        var snapshot = IntentReader.Read(_root, ["REQUIREMENTS.md"], "scan", out var skipped);

        skipped.Should().BeEmpty();
        snapshot.Problems().Should().BeEmpty();
        var byId = snapshot.Records.Where(r => r.Type == IntentPack.RequirementType).ToDictionary(r => r.Properties["reference"].GetString()!);
        Links(byId["REQ-2"]).Should().Equal((IntentPack.Decomposes, "REQ-1"));
        Links(byId["REQ-3"]).Should().Equal((IntentPack.Decomposes, "REQ-1"));
        byId["REQ-2"].Properties["text"].GetString().Should().Be("The system shall keep an order for seven years.", "the parent mark is not part of the statement");
        byId["REQ-3"].Properties["text"].GetString().Should().Be("The system shall delete an order on request.");
        byId["REQ-3"].Properties["status"].GetString().Should().Be("accepted");
        byId["REQ-1"].Links.Should().BeEmpty("a parent's own record does not change when a child names it");
        byId["REQ-4"].Links.Should().BeEmpty("a parent this read does not hold is not linked: the store would refuse a link inside its own pack to nothing");

        var relation = IntentPack.Manifest.Ontology!.Relations.Single(r => r.Name == IntentPack.Decomposes);
        (relation.From, relation.To).Should().Be((IntentPack.RequirementType, IntentPack.RequirementType));
        IntentPack.Manifest.Modules.Should().BeEmpty("intent depends on nothing");
        IntentPack.Manifest.ContainableBy.Should().BeEquivalentTo([CodeMapPack.Id, DecisionsPack.Id, WorkPack.Id]);
    }

    // ---- CODEMAP_VALIDATES -------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_test_that_names_a_requirement_in_its_attributes_validates_it_in_xunit_nunit_and_mstest()
    {
        Write("src/Store/Store.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>Acme.Store</AssemblyName></PropertyGroup></Project>""");
        Write("src/Store/ModuleStore.cs", "namespace Acme.Store; public sealed class ModuleStore { public void Remove() { } public int Count => 0; }");
        Write("tests/Store.Tests/Store.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\..\src\Store\Store.csproj" /></ItemGroup></Project>
            """);
        Write("tests/Store.Tests/StoreTests.cs", """
            using Acme.Store;
            namespace Acme.Store.Tests;
            [Trait("Requirement", "REQ-15")]
            public class StoreTests
            {
                [Fact, Trait("Requirement", "REQ-12")] public void Remove_keeps_history() { new ModuleStore().Remove(); }
                [Fact] [Trait("Category", "Slow")] public void Counts() { _ = new ModuleStore().Count; }
            }
            public class NUnitStyle { [Test, NUnit.Framework.Property("Requirement", "REQ-13")] public void Opens() { new ModuleStore().Remove(); } }
            public class MsTestStyle { [TestMethod] [TestProperty("requirement", "REQ-14, REQ-16")] public void Closes() { new ModuleStore().Remove(); } }
            public class Untraced { [Fact] public void Plain() { new ModuleStore().Remove(); } }
            """);

        var snapshot = RoslynCodeMapReader.Read(_root, RoslynCodeMapReader.TrackedFiles(_root)).Build("git:one");

        snapshot.Problems().Should().BeEmpty();
        snapshot.Dependencies.Select(d => d.Id).Should().Equal(IntentPack.Id);
        string[] Validated(string test) => [.. snapshot.Records.Single(r => r.Type == CodeMapPack.TestType && r.Properties["name"].GetString() == test)
            .Links.Where(l => l.Relation == CodeMapPack.Validates).Select(l => l.Target)];
        Validated("StoreTests.Remove_keeps_history").Should().Equal(["REQ-12", "REQ-15"], "the method's own requirement first, then its class's");
        Validated("StoreTests.Counts").Should().Equal(["REQ-15"], "a trait of another name is not a requirement");
        Validated("NUnitStyle.Opens").Should().Equal("REQ-13");
        Validated("MsTestStyle.Closes").Should().Equal(["REQ-14", "REQ-16"], "the name is matched without case, and a value may list several ids");
        Validated("Untraced.Plain").Should().BeEmpty();

        var relation = CodeMapPack.Manifest.Ontology!.Relations.Single(r => r.Name == CodeMapPack.Validates);
        (relation.From, relation.To).Should().Be((CodeMapPack.TestType, IntentPack.Id + ":" + IntentPack.RequirementType));
        CodeMapPack.Manifest.Modules.Keys.Should().Equal(IntentPack.Id);
    }

    [Fact]
    public void The_builder_keeps_each_validated_requirement_once_and_ignores_a_test_it_does_not_hold()
    {
        var builder = new CodeMapBuilder();
        builder.AddFile("tests/A.cs", null);
        builder.AddTest("scip . A#T().", "A.T", "tests/A.cs", 3, "xunit", []);
        builder.AddValidates("scip . A#T().", ["REQ-1", " REQ-2 ", "REQ-1", "", "  "]);
        builder.AddValidates("scip . Missing#T().", ["REQ-9"]);

        var test = builder.Build("git:one").Records.Single(r => r.Type == CodeMapPack.TestType);

        Links(test).Should().Equal(("CODEMAP_DECLARED_IN", "tests/A.cs"), (CodeMapPack.Validates, "REQ-1"), (CodeMapPack.Validates, "REQ-2"));
    }

    // ---- DECISIONS_ADDRESSES -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_decision_addresses_the_requirements_its_addresses_line_or_drivers_name_and_a_superseded_one_none()
    {
        Write("docs/adr/0001-keep-orders.md", """
            # 1. Keep orders for seven years

            * Status: accepted
            * Addresses: REQ-2, REQ-3

            ## Context

            Auditors ask for it.

            ## Decision

            Orders go to cold storage after a year.
            """);
        Write("docs/decisions/0002-soft-delete.md", """
            # Soft delete

            ## Context and Problem Statement

            People ask us to delete their orders.

            ## Decision Drivers

            * REQ-3 asks for deletion on request
            * Storage cost

            ## Decision Outcome

            Chosen option: "Soft delete", because REQ-9 keeps the audit trail.
            """);
        Write("docs/adr/0003-hard-delete.md", """
            # 3. Hard delete

            Status: superseded by ADR-1

            Addresses: REQ-7

            ## Decision

            Delete rows.
            """);
        string[] files = ["docs/adr/0001-keep-orders.md", "docs/decisions/0002-soft-delete.md", "docs/adr/0003-hard-delete.md"];

        var snapshot = DecisionsReader.Read(_root, DecisionsReader.DecisionFiles(files), CodeIndex.Scan(_root, files), "git:abc");

        snapshot.Problems().Should().BeEmpty();
        snapshot.Dependencies.Select(d => d.Id).Should().Equal(IntentPack.Id);
        string[] Addressed(string key) => [.. snapshot.Records.Single(r => r.Key == key).Links.Where(l => l.Relation == DecisionsPack.Addresses).Select(l => l.Target)];
        Addressed("docs/adr/0001-keep-orders.md#decision").Should().Equal("REQ-2", "REQ-3");
        Addressed("docs/decisions/0002-soft-delete.md#decision").Should().Equal(["REQ-3"], "an id the outcome mentions is not one the decision says it addresses");
        Addressed("docs/adr/0003-hard-delete.md#decision").Should().BeEmpty("a superseded decision addresses nothing, as it applies to nothing");

        var relation = DecisionsPack.Manifest.Ontology!.Relations.Single(r => r.Name == DecisionsPack.Addresses);
        (relation.From, relation.To).Should().Be((DecisionsPack.Type, IntentPack.Id + ":" + IntentPack.RequirementType));
    }

    // ---- WORK_IMPLEMENTS ---------------------------------------------------------------------------------------------------------

    private const string Ledger = """
        {"tasks":[
          {"id":"A-1","task":"Keep orders seven years (REQ-2)","status":"Done"},
          {"id":"B-2","task":"Delete on request","status":"Doing","implements":["REQ-3","REQ-404"]},
          {"id":"C-3","task":"Unrelated","status":"Pending","notes":"Touches REQ-2 in passing."}]}
        """;

    private static IReadOnlyList<WorkCommit> LedgerCommits() =>
    [
        new("c1", "A-1: keep orders", ["src/Orders.cs"]),
        new("c2", "Delete orders on request (B-2)", ["src/Delete.cs"]),
    ];

    [Fact]
    public void A_work_item_implements_the_requirements_its_title_or_implements_field_name_and_its_notes_name_none()
    {
        var snapshot = WorkLedgerReader.Read(Ledger, "tasks.json", LedgerCommits(), "git:abc",
            new WorkLedgerOptions { Requirements = new HashSet<string> { "REQ-2", "REQ-3" } });

        snapshot.Problems().Should().BeEmpty();
        snapshot.Dependencies.Select(d => d.Id).Should().Equal(CodeMapPack.Id, IntentPack.Id);
        var items = snapshot.Records.Where(r => r.Type == WorkPack.ItemType).ToDictionary(r => r.Key);
        Links(items["A-1"]).Should().Contain((WorkPack.Implements, "REQ-2"));
        items["B-2"].Links.Where(l => l.Relation == WorkPack.Implements).Select(l => l.Target).Should().Equal(["REQ-3"], "REQ-404 is no requirement");
        items["C-3"].Links.Should().NotContain(l => l.Relation == WorkPack.Implements, "notes name related rows as context, not what the item implements");
        items["B-2"].Links.Should().NotContain(l => l.Relation == WorkPack.Changed, "by default a commit names its ids before the first colon");

        var relation = WorkPack.Manifest.Ontology!.Relations.Single(r => r.Name == WorkPack.Implements);
        (relation.From, relation.To).Should().Be((WorkPack.ItemType, IntentPack.Id + ":" + IntentPack.RequirementType));
        WorkPack.Manifest.Modules.Keys.Should().BeEquivalentTo([CodeMapPack.Id, IntentPack.Id]);
    }

    [Fact]
    public void A_ledger_read_as_intent_makes_each_item_implement_its_own_row_and_its_commits_may_name_it_anywhere()
    {
        var snapshot = WorkLedgerReader.Read(Ledger, "tasks.json", LedgerCommits(), "git:abc",
            new WorkLedgerOptions { Requirements = new HashSet<string> { "A-1", "B-2", "C-3" }, CommitIds = WorkCommitIds.Anywhere });

        snapshot.Problems().Should().BeEmpty();
        var items = snapshot.Records.Where(r => r.Type == WorkPack.ItemType).ToDictionary(r => r.Key);
        foreach (var id in new[] { "A-1", "B-2", "C-3" })
            items[id].Links.Where(l => l.Relation == WorkPack.Implements).Select(l => l.Target).Should().Equal(id);
        items["B-2"].Links.Should().Contain(l => l.Relation == WorkPack.Changed && l.Target == "src/Delete.cs");
        items["B-2"].Properties["commits"].GetInt32().Should().Be(1);
    }

    [Fact]
    public void The_ledger_read_without_options_implements_nothing_and_names_ids_before_the_colon_as_before()
    {
        var plain = WorkLedgerReader.Read(Ledger, "tasks.json", LedgerCommits(), "git:abc", null);

        plain.Problems().Should().BeEmpty();
        plain.Records.Should().NotContain(r => r.Links.Any(l => l.Relation == WorkPack.Implements));
        plain.Records.Single(r => r.Key == "B-2").Properties["commits"].GetInt32().Should().Be(0);
        plain.Dependencies.Select(d => d.Id).Should().Equal(CodeMapPack.Id, IntentPack.Id);
    }

    [Theory]
    [InlineData("CMP-2a, CMP-3 and KP-composite: code map", new[] { "CMP-2a", "CMP-3", "KP-composite" })]
    [InlineData("A pack is removed by its id (PACK-REMOVE, D-06)", new[] { "PACK-REMOVE", "D-06" })]
    [InlineData("CMP-2: not CMP-2a's sibling CMP-2ab or XCMP-3, nor CMP-3-extra", new[] { "CMP-2", "CMP-2a" })]
    [InlineData("40.28: a numbered item, and 40.28 again", new[] { "40.28" })]
    public void Anywhere_a_commit_names_a_ledger_id_as_a_whole_token(string subject, string[] expected) =>
        WorkLedgerReader.IdsAnywhere(subject, new HashSet<string> { "CMP-2", "CMP-2a", "CMP-3", "KP-composite", "PACK-REMOVE", "D-06", "40.28" })
            .Should().Equal(expected);

    // ---- The family: dependencies follow evidence, layers carry meaning ---------------------------------------------------------

    [Fact]
    public void The_coding_family_lets_decisions_work_and_the_code_map_link_into_intent_and_declares_its_layers()
    {
        var builder = new ModuleSetBuilder();
        builder.AddComposite<CodingPack>();

        builder.Modules.Select(m => m.Manifest.Id).Should().Equal(DecisionsPack.Id, RepositoryPack.Id, CodeMapPack.Id, WorkPack.Id, IntentPack.Id);
        builder.Priorities[IntentPack.Id].Should().Be(50, "intent comes after the members a host had, so their candidate order stays");
        var coding = CodingPack.Manifest;
        coding.Validate().Should().BeEmpty();
        coding.Children(DecisionsPack.Id).Should().Equal(WorkPack.Id, CodeMapPack.Id, IntentPack.Id);
        coding.Children(WorkPack.Id).Should().Equal(CodeMapPack.Id, IntentPack.Id);
        coding.Children(CodeMapPack.Id).Should().Equal(IntentPack.Id);
        coding.Children(IntentPack.Id).Should().BeEmpty("intent depends on nothing");
        coding.Members.Single(m => m.Id == ArchitecturePack.Id).Enabled.Should().BeFalse("architecture joins off until its first links (slice 2)");
        builder.Layers.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [IntentPack.Id] = 1, [DecisionsPack.Id] = 2, [ArchitecturePack.Id] = 3, [WorkPack.Id] = 4, [CodeMapPack.Id] = 5,
        });
        new ModuleSetBuilder().AddComposite<CodingPack>(o => o.Enable(ArchitecturePack.Id)).Modules.Select(m => m.Manifest.Id).Should().Contain(ArchitecturePack.Id);

        FluentActions.Invoking(() => new ModuleSetBuilder().AddComposite<CodingPack>(o => o.Disable(IntentPack.Id)))
            .Should().Throw<InvalidOperationException>().WithMessage($"*needs {IntentPack.Id}, which is switched off*");
    }

    private static ModuleManifest Bare(string id) => ModuleManifest.Parse(
        $$"""{"format":"agentmemory.module/v1alpha1","id":"{{id}}","version":"1.0.0","sdk":"[0.1,0.2)"}""");

    private static CompositeManifest Family(string id, params string[] members) => CompositeManifest.Parse(
        $$"""{"format":"agentmemory.composite/v1alpha1","id":"{{id}}","version":"1.0.0","members":[{{string.Join(",", members.Select(m => $$"""{"id":"{{m}}","version":"[1,2)"}"""))}}]}""");

    [Fact]
    public void A_layer_order_names_members_once_and_a_host_has_only_one()
    {
        FluentActions.Invoking(() => new ModuleSetBuilder().AddComposite(Family("io.example.f", "io.example.a"),
                m => m.Add(Bare("io.example.a"), _ => { }).Layers("io.example.a", "io.example.z")))
            .Should().Throw<InvalidOperationException>().WithMessage("*io.example.z*not a member*");
        FluentActions.Invoking(() => new ModuleSetBuilder().AddComposite(Family("io.example.f", "io.example.a"),
                m => m.Add(Bare("io.example.a"), _ => { }).Layers("io.example.a", "io.example.a")))
            .Should().Throw<InvalidOperationException>().WithMessage("*io.example.a*twice*");

        var builder = new ModuleSetBuilder().AddComposite(Family("io.example.f", "io.example.a", "io.example.b"),
            m => m.Add(Bare("io.example.a"), _ => { }).Add(Bare("io.example.b"), _ => { }).Layers("io.example.b", "io.example.a"));
        builder.Layers.Should().BeEquivalentTo(new Dictionary<string, int> { ["io.example.b"] = 1, ["io.example.a"] = 2 });
        FluentActions.Invoking(() => builder.AddComposite(Family("io.example.g", "io.example.c"),
                m => m.Add(Bare("io.example.c"), _ => { }).Layers("io.example.c")))
            .Should().Throw<InvalidOperationException>().WithMessage("*one layer order*");
    }

    // ---- The walk: why and what, as data -----------------------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, int> CodingLayers = new Dictionary<string, int>
    {
        [IntentPack.Id] = 1, [DecisionsPack.Id] = 2, [ArchitecturePack.Id] = 3, [WorkPack.Id] = 4, [CodeMapPack.Id] = 5,
    };

    private static ModuleRecord Record(string module, string type, string id, string key, params (string Name, string Value)[] properties) => new()
    {
        ModuleId = module, Type = type, Id = id, Key = key, CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "import",
        Source = new() { MessageIds = [], Excerpt = "" },
        Properties = properties.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal),
    };

    // A file, a test declared in it that validates REQ-12, a work item that changed the file and implements REQ-12, and REQ-12's
    // parent REQ-1.
    private static readonly ModuleRecord[] Records =
    [
        Record(CodeMapPack.Id, CodeMapPack.FileType, "f-store", "src/Store.cs", ("path", "src/Store.cs")),
        Record(CodeMapPack.Id, CodeMapPack.TestType, "t-remove", "scip . StoreTests#Remove().", ("name", "StoreTests.Remove"), ("file", "tests/StoreTests.cs"), ("scip", "scip . StoreTests#Remove().")),
        Record(IntentPack.Id, IntentPack.RequirementType, "r-12", "REQ-12", ("reference", "REQ-12"), ("text", "The store shall keep history."), ("file", "REQUIREMENTS.md"), ("pattern", "ubiquitous"), ("status", "accepted"), ("source", "REQUIREMENTS.md:1")),
        Record(IntentPack.Id, IntentPack.RequirementType, "r-1", "REQ-1", ("reference", "REQ-1"), ("text", "The store shall be exact."), ("file", "REQUIREMENTS.md"), ("pattern", "ubiquitous"), ("status", "accepted"), ("source", "REQUIREMENTS.md:1")),
        Record(WorkPack.Id, WorkPack.ItemType, "w-a1", "A-1", ("item", "A-1"), ("title", "Keep history"), ("status", "Done"), ("source", "tasks.json#A-1")),
    ];

    private static readonly (string From, string Relation, string To)[] Edges =
    [
        ("t-remove", "CODEMAP_DECLARED_IN", "f-store"), ("t-remove", CodeMapPack.Validates, "r-12"),
        ("w-a1", WorkPack.Changed, "f-store"), ("w-a1", WorkPack.Implements, "r-12"), ("r-12", IntentPack.Decomposes, "r-1"),
    ];

    private sealed class FamilyReader : IModuleTraversalReader
    {
        public Task<ModuleReadResult> MatchAsync(ModuleAnchorQuery query, ModuleReadContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleReadResult
            {
                Status = ModuleReadStatus.Succeeded,
                Records = [.. Records.Where(r => r.ModuleId == query.ModuleId && r.Type == query.Type && r.Properties.TryGetValue(query.Property, out var v)
                    && query.Values.Contains(v.GetString(), StringComparer.Ordinal))],
            });

        public Task<ModuleTraversalStep> ExpandAsync(ModuleExpandQuery query, ModuleReadContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleTraversalStep
            {
                Status = ModuleReadStatus.Succeeded,
                Edges = [.. query.FromIds.SelectMany(from => Edges.Where(e => e.Relation == query.Relation
                        && (query.Direction == ModuleTraversalDirection.In ? e.To == from : e.From == from))
                    .Select(e => new ModuleTraversalEdge
                    {
                        FromId = from, Degree = 1, To = Records.Single(r => r.Id == (query.Direction == ModuleTraversalDirection.In ? e.From : e.To)),
                    }))],
            });
    }

    private static ModuleResolution Resolution()
    {
        var resolution = ModuleResolver.Resolve([IntentPack.Manifest, DecisionsPack.Manifest, CodeMapPack.Manifest, WorkPack.Manifest],
            ModuleSet.SdkVersion, ModuleHostFeatures.Known);
        resolution.Succeeded.Should().BeTrue(string.Join("; ", resolution.Errors));
        return resolution;
    }

    private static IEnumerable<(string, string, long, long, int, int, int, string)> Offers(TraversalResult walk) =>
        walk.Items.Select(i => (i.Record.Type, i.Record.Key, i.Score, i.Rank, i.Hops, i.SharedBy, i.Matches, i.Via));

    [Fact]
    public async Task From_a_file_the_walk_climbs_to_the_requirement_its_test_validates_and_labels_the_hops_why_with_the_reasons_unchanged()
    {
        var resolution = Resolution();
        var request = new ContextRequest { SessionId = "s", Owner = "lea", Query = "", Anchors = [ContextAnchor.Path("src/Store.cs")] };

        var labelled = await TraversalWalk.RunAsync(resolution, new FamilyReader(), request, CancellationToken.None, layers: CodingLayers);
        var plain = await TraversalWalk.RunAsync(resolution, new FamilyReader(), request, CancellationToken.None);

        labelled.Problems.Should().BeEmpty();
        var requirement = labelled.Items.Single(i => i.Record.Key == "REQ-12");
        requirement.Via.Should().Be("Store.cs → StoreTests.Remove declared in it → validates REQ-12", "the reason path stays as R-WALK-066 has it");
        requirement.Toward.Should().Be(TraversalMeaning.Why, "code to intent leads to an earlier layer");
        labelled.Items.Single(i => i.Record.Key == "A-1").Toward.Should().Be(TraversalMeaning.Why, "code to work leads to an earlier layer");
        labelled.Items.Single(i => i.Record.Key == "scip . StoreTests#Remove().").Toward.Should().BeNull("a hop within the code layer is neither");
        labelled.Items.Single(i => i.Record.Key == "src/Store.cs").Toward.Should().BeNull("an anchor's own record was reached by no hop");
        // Data only: the same records, scores, ranks, hops, specificity, matches, reasons and order with and without layers.
        Offers(labelled).Should().Equal(Offers(plain));
        plain.Items.Should().OnlyContain(i => i.Toward == null);
    }

    [Fact]
    public async Task From_a_requirement_file_the_walk_goes_down_to_the_tests_and_work_that_serve_it_and_labels_them_what()
    {
        var request = new ContextRequest { SessionId = "s", Owner = "lea", Query = "", Anchors = [ContextAnchor.Path("REQUIREMENTS.md")] };

        var walk = await TraversalWalk.RunAsync(Resolution(), new FamilyReader(), request, CancellationToken.None, layers: CodingLayers);

        walk.Problems.Should().BeEmpty();
        var test = walk.Items.Single(i => i.Record.Key == "scip . StoreTests#Remove().");
        test.Toward.Should().Be(TraversalMeaning.What);
        test.Via.Should().Be("REQUIREMENTS.md → StoreTests.Remove validates it");
        walk.Items.Single(i => i.Record.Key == "A-1").Toward.Should().Be(TraversalMeaning.What);
    }

    [Fact]
    public async Task The_traversal_contributor_carries_the_label_on_each_item_s_provenance()
    {
        var resolution = Resolution();
        var intent = new TraversalContributor(resolution.Modules.Single(m => m.Id == IntentPack.Id),
            new TraversalService(resolution, new FamilyReader(), layers: CodingLayers));

        var result = await intent.ContributeResultAsync(new ContextRequest
        {
            SessionId = "s", Owner = "lea", Query = "", Anchors = [ContextAnchor.Path("src/Store.cs")],
        });

        var item = result.Section!.Items.Should().ContainSingle().Subject;
        item.Provenance!.RecordKey.Should().Be("REQ-12");
        item.Provenance.ViaMeaning.Should().Be(TraversalMeaning.Why);
        item.Provenance.Via.Should().Be("Store.cs → StoreTests.Remove declared in it → validates REQ-12");
        item.Text.Should().EndWith("(reached from the work in hand: Store.cs → StoreTests.Remove declared in it → validates REQ-12)", "the label is data, not text");
    }
}
