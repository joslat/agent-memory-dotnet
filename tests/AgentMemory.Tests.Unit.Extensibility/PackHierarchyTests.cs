using System.Text.Json;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Coding;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Repository;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// A pack family as a hierarchy (CODING-hierarchy): members say which members they contain, never in a cycle; a member links to
/// the types of the members it contains, and only those; a snapshot carries links into a dependency's records; a walk climbs
/// from the file in hand to the work that changed it.
/// </summary>
public sealed class PackHierarchyTests
{
    private const string Composite = """
        {"format":"agentmemory.composite/v1alpha1","id":"io.example.family","version":"1.0.0","members":[
          {"id":"io.example.intent","version":"[1,2)","contains":["io.example.decisions","io.example.work"]},
          {"id":"io.example.decisions","version":"[1,2)","contains":["io.example.work"]},
          {"id":"io.example.work","version":"[1,2)","contains":["io.example.code"]},
          {"id":"io.example.code","version":"[1,2)"}]}
        """;

    [Fact]
    public void Intent_contains_decisions_and_work_and_decisions_contain_work_as_a_hierarchy_not_a_tree()
    {
        var family = CompositeManifest.Parse(Composite);

        family.Validate().Should().BeEmpty();
        family.Roots.Should().Equal("io.example.intent");
        family.Children("io.example.intent").Should().Equal("io.example.decisions", "io.example.work");
        family.Parents("io.example.work").Should().Equal("io.example.intent", "io.example.decisions");
        family.Depth("io.example.decisions").Should().Be(1);
        family.Depth("io.example.work").Should().Be(2, "work sits under decisions, which sit under intent");
        family.Depth("io.example.code").Should().Be(3);
    }

    [Theory]
    [InlineData("""{"id":"io.example.a","version":"[1,2)","contains":["io.example.b"]},{"id":"io.example.b","version":"[1,2)","contains":["io.example.a"]}""", "cycle: io.example.a > io.example.b > io.example.a")]
    [InlineData("""{"id":"io.example.a","version":"[1,2)","contains":["io.example.a"]}""", "cannot contain itself")]
    [InlineData("""{"id":"io.example.a","version":"[1,2)","contains":["io.example.z"]}""", "'io.example.z' is not a member")]
    public void A_hierarchy_with_a_cycle_a_member_inside_itself_or_a_stranger_is_refused(string members, string problem) =>
        CompositeManifest.Parse("""{"format":"agentmemory.composite/v1alpha1","id":"io.example.c","version":"1.0.0","members":[""" + members + "]}")
            .Validate().Should().Contain(p => p.Contains(problem, StringComparison.Ordinal));

    [Fact]
    public void The_coding_family_puts_decisions_over_work_over_the_code_map()
    {
        var builder = new ModuleSetBuilder();
        builder.AddComposite<CodingPack>();

        builder.Modules.Select(m => m.Manifest.Id).Should().Equal(DecisionsPack.Id, RepositoryPack.Id, CodeMapPack.Id, WorkPack.Id);
        builder.Priorities[WorkPack.Id].Should().Be(40, "work comes after the code map's tests in the candidate budget");
        var coding = CodingPack.Manifest;
        coding.Validate().Should().BeEmpty();
        coding.Roots.Should().Equal(DecisionsPack.Id, RepositoryPack.Id);
        coding.Children(DecisionsPack.Id).Should().Equal(WorkPack.Id, CodeMapPack.Id);
        coding.Depth(WorkPack.Id).Should().Be(1);
        coding.Depth(CodeMapPack.Id).Should().Be(2);
        WorkPack.Manifest.Modules.Keys.Should().Equal(CodeMapPack.Id);
    }

    [Fact]
    public void A_member_needs_what_it_depends_on_switched_on_and_contained()
    {
        FluentActions.Invoking(() => new ModuleSetBuilder().AddComposite<CodingPack>(o => o.Disable(CodeMapPack.Id)))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{WorkPack.Id} needs {CodeMapPack.Id}, which is switched off*");

        // The same members in a family that forgot to say work contains the code map: work's links would cross the hierarchy.
        var flat = CodingPack.Manifest with { Members = [.. CodingPack.Manifest.Members.Select(m => m with { Contains = [] })] };
        var members = new CompositeMembers(new ModuleSetBuilder(), flat, new CompositeOptions());
        members.Add<DecisionsPack>().Add<RepositoryPack>().Add<CodeMapPack>().Add<WorkPack>();
        FluentActions.Invoking(members.Complete).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{WorkPack.Id} depends on {CodeMapPack.Id}, which it does not contain*");
    }

    private const string Ledger = """
        {"tasks":[
          {"id":"A-1","task":"Make the store exact","status":"Done","phase":"Phase A","notes":"Owner: \"links must be exact\". Done in two commits."},
          {"id":"B-2","task":"Read package references","status":"Doing","notes":"From the mutants."},
          {"id":"C-3","task":"Not started yet","status":"Pending"}]}
        """;

    private static IReadOnlyList<WorkCommit> Commits() =>
    [
        new("c1", "A-1: exact links", ["src/Store.cs", "docs/store.md"]),
        new("c2", "A-1, B-2 and Z-9: both at once", ["src\\Store.cs", "src/Reader.cs"]),
        new("c3", "Merge pull request #7 from somewhere", ["src/Other.cs"]),
        new("c4", "B-2: packages", ["src/Reader.cs", "src/Gone.cs"]),
    ];

    [Fact]
    public void The_ledger_s_items_link_to_the_source_files_their_commits_changed_most_changed_first()
    {
        var snapshot = WorkLedgerReader.Read(Ledger, "tasks.json", Commits(), "git:abc ledger:1", new HashSet<string> { "src/Store.cs", "src/Reader.cs" });

        snapshot.Problems().Should().BeEmpty();
        snapshot.Dependencies.Select(d => d.Id).Should().Equal(CodeMapPack.Id);
        var items = snapshot.Records.ToDictionary(r => r.Key);
        items.Keys.Should().Equal("A-1", "B-2", "C-3");
        items["A-1"].Links.Select(l => l.Target).Should().Equal("src/Store.cs", "src/Reader.cs");
        items["B-2"].Links.Select(l => l.Target).Should().Equal(["src/Reader.cs", "src/Store.cs"], "c2 is both items' commit; a file no longer tracked is left out");
        items["C-3"].Links.Should().BeEmpty();
        items["A-1"].Properties["commits"].GetInt32().Should().Be(2);
        items["A-1"].Properties["item"].GetString().Should().Be("A-1");
        items["A-1"].Properties["summary"].GetString().Should().StartWith("Owner: \"links must be exact\"");
        items["A-1"].Properties["source"].GetString().Should().Be("tasks.json#A-1");
    }

    [Theory]
    [InlineData("CMP-2a, CMP-3 and KP-composite: code map, decisions", new[] { "CMP-2a", "CMP-3", "KP-composite" })]
    [InlineData("40.28: a numbered item", new[] { "40.28" })]
    [InlineData("Phase 33 Slice A: the module SDK", new string[0])]
    [InlineData("A commit without a colon names nothing", new string[0])]
    public void A_commit_belongs_to_the_ledger_ids_its_subject_names_before_the_first_colon(string subject, string[] expected) =>
        WorkLedgerReader.Ids(subject, new HashSet<string> { "CMP-2a", "CMP-3", "KP-composite", "40.28" }).Should().Equal(expected);

    [Fact]
    public void A_snapshot_names_exactly_the_dependencies_its_module_declares()
    {
        var snapshot = WorkLedgerReader.Read(Ledger, "tasks.json", Commits(), "git:abc", null);

        (snapshot with { Dependencies = [] }).Problems().Should().ContainSingle(p => p.Contains($"needs {CodeMapPack.Id}", StringComparison.Ordinal));
        (snapshot with { Dependencies = [CodeMapPack.Manifest, DecisionsPack.Manifest] }).Problems()
            .Should().ContainSingle(p => p.Contains($"names {DecisionsPack.Id}, which the module does not depend on", StringComparison.Ordinal));
        // A link into the code map's records is the store's to resolve: the snapshot neither holds nor checks its target.
        snapshot.Problems().Should().BeEmpty();
        snapshot.ImportOrder(selfCycles: true).Select(r => r.Key).Should().Equal("A-1", "B-2", "C-3");
        snapshot.Resolve().Modules.Select(m => m.Id).Should().BeEquivalentTo([CodeMapPack.Id, WorkPack.Id]);
    }

    // A two-pack family in miniature: work contains code, and a walk from a file climbs to the work that changed it.
    private const string Code = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}}]},
         "traversal":{"anchors":[{"type":"CodeFile","property":"path","match":"exact"}],"hubs":["CodeFile"],"maxItems":3},
         "containableBy":["com.example.work"]}
        """;
    private const string Work = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.work","version":"1.0.0","sdk":"[0.1,0.2)",
         "requires":{"modules":{"com.example.code":"[1,2)"}},
         "ontology":{"labelPrefix":"Work","types":[{"name":"WorkItem","scope":"shared","render":"Work item {item}: {title}","properties":{"item":{"type":"string","required":true},"title":{"type":"string","required":true}}}],
          "relations":[{"name":"WORK_CHANGED","from":"WorkItem","to":"com.example.code:CodeFile"}]},
         "traversal":{"relations":[{"name":"WORK_CHANGED","direction":"in","weight":0.6,"fanOut":3}],"maxItems":2,"hops":1}}
        """;

    private sealed class TwoPackReader(IReadOnlyList<ModuleRecord> records, IReadOnlyList<(string From, string Relation, string To)> links) : IModuleTraversalReader
    {
        public readonly List<string> Calls = [];

        public Task<ModuleReadResult> MatchAsync(ModuleAnchorQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"match {query.ModuleId} {query.Type}");
            return Task.FromResult(new ModuleReadResult
            {
                Status = ModuleReadStatus.Succeeded,
                Records = [.. records.Where(r => r.ModuleId == query.ModuleId && r.Type == query.Type
                    && query.Values.Contains(r.Properties[query.Property].GetString(), StringComparer.OrdinalIgnoreCase))],
            });
        }

        public Task<ModuleTraversalStep> ExpandAsync(ModuleExpandQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"expand {query.ModuleId} {query.Relation} {query.Direction}");
            var edges = query.FromIds.SelectMany(from => links.Where(l => l.Relation == query.Relation
                    && (query.Direction == ModuleTraversalDirection.In ? l.To == from : l.From == from))
                .Select(l => new ModuleTraversalEdge
                {
                    FromId = from, Degree = 1,
                    To = records.Single(r => r.Id == (query.Direction == ModuleTraversalDirection.In ? l.From : l.To)),
                })).ToList();
            return Task.FromResult(new ModuleTraversalStep { Status = ModuleReadStatus.Succeeded, Edges = edges });
        }
    }

    private static ModuleRecord Record(string module, string type, string id, params (string Name, string Value)[] properties) => new()
    {
        ModuleId = module, Type = type, Id = id, Key = id, CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "import",
        Source = new() { MessageIds = [], Excerpt = "" },
        Properties = properties.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal),
    };

    [Fact]
    public async Task A_pack_cannot_walk_into_another_that_does_not_consent()
    {
        // The same two packs, but the code pack names no container: the work pack's link no longer attaches it to code anchors.
        var unconsenting = System.Text.RegularExpressions.Regex.Replace(Code, @",\s*""containableBy"":\[[^\]]*\]", "");
        unconsenting.Should().NotContain("containableBy");
        var resolution = ModuleResolver.Resolve([ModuleManifest.Parse(unconsenting), ModuleManifest.Parse(Work)], ModuleVersion.Parse("0.1.0"), ModuleHostFeatures.Known);
        resolution.Succeeded.Should().BeTrue(string.Join("; ", resolution.Errors));
        var reader = new TwoPackReader(
            [
                Record("com.example.code", "CodeFile", "file-store", ("path", "src/Store.cs")),
                Record("com.example.work", "WorkItem", "work-a1", ("item", "A-1"), ("title", "Make the store exact")),
            ],
            [("work-a1", "WORK_CHANGED", "file-store")]);
        var work = new TraversalContributor(resolution.Modules.Single(m => m.Id == "com.example.work"), new TraversalService(resolution, reader));

        var result = await work.ContributeResultAsync(new ContextRequest
        {
            SessionId = "s", Owner = "lea", Query = "why is the store exact", Anchors = [ContextAnchor.Path("src/Store.cs")],
        });

        result.Section.Should().BeNull();
        result.Detail.Should().Contain("does not list it in containableBy");
        reader.Calls.Should().NotContain(c => c.StartsWith("expand com.example.work", StringComparison.Ordinal));
    }

    [Fact]
    public async Task From_the_file_in_hand_the_walk_climbs_to_the_work_that_changed_it()
    {
        var resolution = ModuleResolver.Resolve([ModuleManifest.Parse(Code), ModuleManifest.Parse(Work)], ModuleVersion.Parse("0.1.0"), ModuleHostFeatures.Known);
        resolution.Succeeded.Should().BeTrue(string.Join("; ", resolution.Errors));
        var reader = new TwoPackReader(
            [
                Record("com.example.code", "CodeFile", "file-store", ("path", "src/Store.cs")),
                Record("com.example.work", "WorkItem", "work-a1", ("item", "A-1"), ("title", "Make the store exact")),
                Record("com.example.work", "WorkItem", "work-c3", ("item", "C-3"), ("title", "Unrelated")),
            ],
            [("work-a1", "WORK_CHANGED", "file-store")]);
        var work = new TraversalContributor(resolution.Modules.Single(m => m.Id == "com.example.work"), new TraversalService(resolution, reader));

        var result = await work.ContributeResultAsync(new ContextRequest
        {
            SessionId = "s", Owner = "lea", Query = "why is the store exact", Anchors = [ContextAnchor.Path("src/Store.cs")],
        });

        result.Section!.Items.Select(i => i.Id).Should().Equal("work-a1");
        // The record renders its title; its reason names it by key only, never by free text (red-team M6).
        result.Section.Items[0].Text.Should().Be("Work item A-1: Make the store exact (reached from the work in hand: Store.cs → work-a1 changed it)");
        reader.Calls.Should().Contain("expand com.example.work WORK_CHANGED In");
    }
}
