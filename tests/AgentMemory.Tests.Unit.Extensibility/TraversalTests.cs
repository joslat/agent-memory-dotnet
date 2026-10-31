using System.Text.Json;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Traversal context (coding packs CMP-1): from the file or symbol in hand, through declared links, to the decisions and tests
/// that govern it, each offered with the path that reached it; hubs never offered; nothing without anchors.
/// </summary>
public sealed class TraversalTests
{
    internal const string Manifest = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Code","types":[
           {"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}},
           {"name":"CodeSymbol","scope":"shared","render":"{name}","properties":{"name":{"type":"string","required":true}}},
           {"name":"CodeTest","scope":"shared","render":"Test {name}","properties":{"name":{"type":"string","required":true}}},
           {"name":"CodeDecision","scope":"shared","render":"Decision: {title}","properties":{"title":{"type":"string","required":true},"appliesTo":{"type":"string"}}}],
          "relations":[
           {"name":"CODE_DEFINES","from":"CodeFile","to":"CodeSymbol"},
           {"name":"CODE_TESTS","from":"CodeTest","to":"CodeSymbol"},
           {"name":"CODE_SUPERSEDES","from":"CodeDecision","to":"CodeDecision","max":1}]},
         "traversal":{
           "anchors":[{"type":"CodeFile","property":"path","match":"exact"},
                      {"type":"CodeSymbol","property":"name","match":"exact","kind":"symbol"},
                      {"type":"CodeDecision","property":"appliesTo","match":"glob"}],
           "relations":[{"name":"CODE_DEFINES","direction":"out","weightMicro":900000},
                        {"name":"CODE_TESTS","direction":"in","weightMicro":800000,"fanOut":2}],
           "hubs":["CodeFile","CodeSymbol"],"maxItems":3}}
        """;

    private const string Module = "com.example.code";
    private const string StoreFile = "src/Store/ModuleStore.cs";

    private static ModuleResolution Resolution() =>
        ModuleResolver.Resolve([ModuleManifest.Parse(Manifest)], ModuleVersion.Parse("0.1.0"), ModuleHostFeatures.Known);

    private static ModuleRecord Record(string type, string id, params (string Name, string Value)[] properties) => new()
    {
        ModuleId = Module, Type = type, Id = id, Key = id, CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "import",
        Source = new() { MessageIds = [], Excerpt = "" },
        Properties = properties.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal),
    };

    /// <summary>
    /// An in-memory store: records by type, links as (from, relation, to). Like the Neo4j reader, an expansion keeps first the
    /// records whose text holds most of the question's words, then the lowest degree, then by id.
    /// </summary>
    private sealed class FakeReader : IModuleTraversalReader
    {
        public readonly List<ModuleRecord> Records = [];
        public readonly List<(string From, string Relation, string To)> Links = [];
        public readonly Dictionary<string, int> Degrees = new(StringComparer.Ordinal);
        public readonly List<IReadOnlyList<string>> Words = [];
        public readonly List<string> Calls = [];
        public ModuleReadStatus Status = ModuleReadStatus.Succeeded;

        public Task<ModuleReadResult> MatchAsync(ModuleAnchorQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"match {query.Type}.{query.Property} [{string.Join(",", query.Values)}]");
            if (Status != ModuleReadStatus.Succeeded) return Task.FromResult(new ModuleReadResult { Status = Status, Errors = ["store refused"] });
            return Task.FromResult(new ModuleReadResult
            {
                Status = ModuleReadStatus.Succeeded,
                Records = [.. Records.Where(r => r.Type == query.Type && r.Properties.ContainsKey(query.Property))],
            });
        }

        public Task<ModuleTraversalStep> ExpandAsync(ModuleExpandQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"expand {query.Relation} {query.Direction} fanOut {query.FanOut}");
            Words.Add(query.Words);
            int Hits(string id) => query.Words.Count(w => string.Join(' ', Records.Single(r => r.Id == id).Properties.Values
                .Select(v => v.GetString())).ToLowerInvariant().Contains(w, StringComparison.Ordinal));
            var edges = new List<ModuleTraversalEdge>();
            foreach (var from in query.FromIds)
            {
                var reached = query.Direction == ModuleTraversalDirection.Out
                    ? Links.Where(l => l.From == from && l.Relation == query.Relation).Select(l => l.To)
                    : Links.Where(l => l.To == from && l.Relation == query.Relation).Select(l => l.From);
                edges.AddRange(reached.OrderByDescending(Hits).ThenBy(id => Degrees.GetValueOrDefault(id, 1)).ThenBy(id => id, StringComparer.Ordinal)
                    .Take(query.FanOut).Select(id => new ModuleTraversalEdge
                    {
                        FromId = from, To = Records.Single(r => r.Id == id), Degree = Degrees.GetValueOrDefault(id, 1),
                    }));
            }
            return Task.FromResult(new ModuleTraversalStep { Status = ModuleReadStatus.Succeeded, Edges = edges });
        }
    }

    private static FakeReader Store()
    {
        var reader = new FakeReader();
        reader.Records.AddRange([
            Record("CodeFile", "file", ("path", StoreFile)),
            Record("CodeSymbol", "symbol", ("name", "ModuleStore")),
            Record("CodeTest", "test-removal", ("name", "Removal keeps history")),
            Record("CodeTest", "test-upgrade", ("name", "Downgrade is refused")),
            Record("CodeTest", "test-third", ("name", "Third test")),
            Record("CodeDecision", "decision-close", ("title", "Packs close records, never delete"), ("appliesTo", "src/Store/** ; src/Packs/*.cs")),
            Record("CodeDecision", "decision-docs", ("title", "Docs are projections"), ("appliesTo", "docs/**")),
        ]);
        reader.Links.AddRange([
            ("file", "CODE_DEFINES", "symbol"),
            ("test-removal", "CODE_TESTS", "symbol"), ("test-upgrade", "CODE_TESTS", "symbol"), ("test-third", "CODE_TESTS", "symbol"),
        ]);
        return reader;
    }

    private static (TraversalContributor Contributor, FakeReader Reader) Contributor(FakeReader? reader = null)
    {
        var resolution = Resolution();
        reader ??= Store();
        return (new TraversalContributor(resolution.Modules.Single(), new TraversalService(resolution, reader)), reader);
    }

    private static ContextRequest Request(params ContextAnchor[] anchors) => new() { SessionId = "s", Owner = "lea", Anchors = anchors };

    [Fact]
    public async Task From_the_file_in_hand_it_offers_the_decision_and_tests_that_govern_it_never_the_file_or_symbol()
    {
        var (contributor, _) = Contributor();

        var result = await contributor.ContributeResultAsync(Request(ContextAnchor.Path(StoreFile, "working-tree")));

        var items = result.Section!.Items;
        // The tests are reached file → defines symbol ← tests (1.0 × 0.9 × 0.8 = 0.72), the decision by glob (0.7); fan-out 2 keeps
        // the first two tests in id order.
        items.Select(i => i.Id).Should().Equal("test-removal", "test-third", "decision-close");
        items[0].Text.Should().Be("Test Removal keeps history (reached from the work in hand: ModuleStore.cs → defines ModuleStore → Removal keeps history tests it)");
        items[2].Provenance!.Via.Should().Be("ModuleStore.cs matches appliesTo src/Store/**");
        items.Should().NotContain(i => i.Id == "file" || i.Id == "symbol" || i.Id == "decision-docs");
    }

    [Fact]
    public async Task A_symbol_anchor_reaches_the_same_tests_and_a_dot_slash_or_backslash_path_is_the_same_file()
    {
        var (contributor, reader) = Contributor();
        var bySymbol = await contributor.ContributeResultAsync(Request(ContextAnchor.Symbol("ModuleStore", "prompt")));
        bySymbol.Section!.Items.Select(i => i.Id).Should().Equal("test-removal", "test-third");

        var byPath = await contributor.ContributeResultAsync(Request(ContextAnchor.Path(@".\src\Store\ModuleStore.cs")));
        byPath.Section!.Items.Select(i => i.Id).Should().Contain(["test-removal", "decision-close"]);
        reader.Calls.Should().Contain("expand CODE_TESTS In fanOut 2");
    }

    [Fact]
    public async Task Without_anchors_nothing_is_read_and_time_pinned_requests_do_not_walk()
    {
        var (contributor, reader) = Contributor();

        (await contributor.AppliesAsync(Request(), default)).Should().BeFalse();
        (await contributor.ContributeResultAsync(Request())).OmissionReason.Should().Be(ContextOmissionReason.NotApplicable);
        var pinned = Request(ContextAnchor.Path(StoreFile)) with { SystemAsOf = DateTimeOffset.UnixEpoch };
        (await contributor.AppliesAsync(pinned, default)).Should().BeFalse();
        (await contributor.ContributeResultAsync(pinned)).OmissionReason.Should().Be(ContextOmissionReason.NotApplicable);
        reader.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task One_walk_serves_every_contributor_of_the_same_request()
    {
        var resolution = Resolution();
        var reader = Store();
        var walks = new TraversalService(resolution, reader);
        var request = Request(ContextAnchor.Path(StoreFile));

        var first = await walks.WalkAsync(request, default);
        var second = await walks.WalkAsync(request, default);

        second.Should().BeSameAs(first);
        // Two path-anchor declarations read once each; the symbol declaration has no symbol anchor to read with.
        reader.Calls.Count(c => c.StartsWith("match", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task A_refused_read_is_reported_not_hidden_as_empty()
    {
        var reader = Store();
        reader.Status = ModuleReadStatus.Refused;
        var (contributor, _) = Contributor(reader);

        var result = await contributor.ContributeResultAsync(Request(ContextAnchor.Path(StoreFile)));

        result.OmissionReason.Should().Be(ContextOmissionReason.Denied);
        result.Detail.Should().Contain("store refused");
    }

    [Fact]
    public async Task An_anchor_that_matches_nothing_reaches_nothing()
    {
        var (contributor, reader) = Contributor();

        var result = await contributor.ContributeResultAsync(Request(ContextAnchor.Path("src/Elsewhere/Other.cs")));

        result.OmissionReason.Should().Be(ContextOmissionReason.SearchedAndEmpty);
        reader.Calls.Should().NotContain(c => c.StartsWith("expand", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ModuleAnchorMatch.Exact, "src/Store/ModuleStore.cs", "./src/Store/ModuleStore.cs", 1.0)]
    [InlineData(ModuleAnchorMatch.Exact, "SRC/store/ModuleStore.cs", "src/Store/ModuleStore.cs", 1.0)]
    [InlineData(ModuleAnchorMatch.Exact, "src/Store/ModuleStore.cs", "src/Store/ModuleStore.csx", 0.0)]
    [InlineData(ModuleAnchorMatch.Glob, "src/Store/**", "src/Store/Deep/A.cs", 0.7)]
    [InlineData(ModuleAnchorMatch.Glob, "src/*/A.cs", "src/Store/A.cs", 0.7)]
    [InlineData(ModuleAnchorMatch.Glob, "src/*/A.cs", "src/Store/Deep/A.cs", 0.0)]
    [InlineData(ModuleAnchorMatch.Glob, "**/A.cs", "A.cs", 0.7)]
    [InlineData(ModuleAnchorMatch.Glob, "docs/** src/Store/A.cs", "src/Store/A.cs", 1.0)]
    [InlineData(ModuleAnchorMatch.Prefix, "src/Store", "src/Store/A.cs", 0.7)]
    [InlineData(ModuleAnchorMatch.Prefix, "src/St", "src/Store/A.cs", 0.0)]
    [InlineData(ModuleAnchorMatch.Name, "scip-dotnet nuget Pkg . App/Store/ModuleStore#", "ModuleStore", 0.9)]
    [InlineData(ModuleAnchorMatch.Name, "scip-dotnet nuget Pkg . App/Store/ModuleStore#RemovePackAsync().", "RemovePackAsync", 0.9)]
    [InlineData(ModuleAnchorMatch.Name, "ModuleStore", "ModuleStore", 1.0)]
    [InlineData(ModuleAnchorMatch.Name, "ModuleStore", "modulestore", 0.0)]
    [InlineData(ModuleAnchorMatch.Name, "Ledger, ModuleStore; Gate", "ModuleStore", 1.0)]
    public void Anchors_match_by_path_glob_folder_or_symbol_name(ModuleAnchorMatch match, string value, string anchor, double quality) =>
        AnchorMatcher.Best(match, value, [anchor]).Quality.Should().BeApproximately(quality, 0.0001);

    [Fact]
    public void A_deeper_folder_says_more_than_a_shallow_one() =>
        AnchorMatcher.Quality(ModuleAnchorMatch.Prefix, "src/Store/Deep", "src/Store/Deep/A.cs")
            .Should().BeGreaterThan(AnchorMatcher.Quality(ModuleAnchorMatch.Prefix, "src", "src/Store/Deep/A.cs"));

    [Theory]
    // Review 3: ASCII only, in every store. A-Z fold; nothing else does, and only ASCII white space is trimmed.
    [InlineData(ModuleAnchorMatch.Exact, "SRC/Store/A.CS", "src/store/a.cs", 1.0)]
    [InlineData(ModuleAnchorMatch.Exact, "src/Ä.cs", "src/ä.cs", 0.0)]
    [InlineData(ModuleAnchorMatch.Exact, "docs/\u00B5s.cs", "docs/\u03BCs.cs", 0.0)]
    [InlineData(ModuleAnchorMatch.Exact, "src/\u0130ndex.cs", "src/index.cs", 0.0)]
    [InlineData(ModuleAnchorMatch.Exact, "src/A.cs\u00A0", "src/A.cs", 0.0)]
    [InlineData(ModuleAnchorMatch.Exact, " \tsrc/A.cs\r\n", "src/A.cs", 1.0)]
    [InlineData(ModuleAnchorMatch.Exact, " ", " ", 0.0)]
    [InlineData(ModuleAnchorMatch.Glob, "SRC/**/*.CS", "src/Store/Ä.cs", 0.7)]
    [InlineData(ModuleAnchorMatch.Glob, "src/Ä*", "src/ä.cs", 0.0)]
    [InlineData(ModuleAnchorMatch.Prefix, "SRC/Store", "src/store/A.cs", 0.7)]
    public void Matching_folds_ASCII_letters_only(ModuleAnchorMatch match, string value, string anchor, double quality) =>
        AnchorMatcher.Best(match, value, [anchor]).Quality.Should().BeApproximately(quality, 0.0001);

    [Fact]
    public void A_walk_takes_anchors_in_NFC_distinct_well_formed_and_at_most_32()
    {
        var path = ContextAnchorKind.Path;
        var anchors = TraversalWalk.Anchors(
        [
            new(path, "src/U\u0308ber.cs"), new(path, "src/Über.cs"), new(path, "src/\uD800.cs"), new(path, "\u00A0"), new(path, " \t"),
            new(path, new string('a', ContextAnchor.MaximumLength + 1)),
            .. Enumerable.Range(0, 40).Select(i => new ContextAnchor(path, $"src/F{i}.cs")),
        ]);

        anchors[0].Value.Should().Be("src/Über.cs", "the decomposed form is taken in NFC, and the composed one is then its duplicate");
        anchors.Should().HaveCount(ContextAnchor.MaximumPerRequest);
        anchors.Select(a => a.Value).Should().NotContain(v => v.Contains('\uD800'), "a lone surrogate has no NFC form");
        anchors[1].Value.Should().Be("\u00A0", "a non-breaking space is not ASCII white space: the anchor stands, and matches nothing");
        anchors[2].Value.Should().Be("src/F0.cs");
    }

    [Fact]
    public void The_traversal_manifest_parses_validates_and_the_editor_schema_accepts_it()
    {
        var manifest = ModuleManifest.Parse(Manifest);

        manifest.Validate().Should().BeEmpty();
        manifest.Traversal!.Anchors.Select(a => (a.Type, a.Match, a.Kind)).Should().Equal(
            ("CodeFile", ModuleAnchorMatch.Exact, ContextAnchorKind.Path), ("CodeSymbol", ModuleAnchorMatch.Exact, ContextAnchorKind.Symbol),
            ("CodeDecision", ModuleAnchorMatch.Glob, ContextAnchorKind.Path));
        manifest.Traversal.Relations.Select(r => (r.Name, r.Direction, r.WeightMicro, r.FanOut)).Should().Equal(
            ("CODE_DEFINES", ModuleTraversalDirection.Out, 900_000L, 8), ("CODE_TESTS", ModuleTraversalDirection.In, 800_000L, 2));
        ModuleEditorSchemaTests.EditorAccepts("module", Manifest).Should().BeTrue();
    }

    [Theory]
    [InlineData("\"traversal\":{\"anchors\":[{\"type\":\"CodeTest\",\"property\":\"missing\"}]}", "anchor must name a declared property")]
    [InlineData("\"traversal\":{\"anchors\":[{\"type\":\"OtherType\",\"property\":\"name\"}]}", "anchor must name an owned type")]
    [InlineData("\"traversal\":{\"relations\":[{\"name\":\"CODE_ELSEWHERE\"}]}", "traversal may follow owned relations only")]
    [InlineData("\"traversal\":{\"relations\":[{\"name\":\"CODE_TESTS\",\"weightMicro\":1500000}]}", "weightMicro: expected 1 to 1000000")]
    [InlineData("\"traversal\":{\"relations\":[{\"name\":\"CODE_TESTS\",\"fanOut\":33}]}", "expected 1 to 32")]
    [InlineData("\"traversal\":{\"relations\":[{\"name\":\"CODE_TESTS\"}],\"hubs\":[\"Nowhere\"]}", "hub must name an owned type")]
    [InlineData("\"traversal\":{\"relations\":[{\"name\":\"CODE_TESTS\"}],\"maxItems\":17}", "expected 1 to 16")]
    [InlineData("\"traversal\":{}", "traversal must declare anchors or relations")]
    public void A_traversal_that_reaches_beyond_its_module_or_its_bounds_is_refused(string traversal, string problem)
    {
        var json = Manifest[..Manifest.IndexOf("\"traversal\"", StringComparison.Ordinal)] + traversal + "}";

        ModuleManifest.Parse(json).Validate().Should().Contain(e => e.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_match_or_direction_does_not_parse()
    {
        var at = Manifest.IndexOf("\"traversal\"", StringComparison.Ordinal);
        var badMatch = Manifest[..at] + "\"traversal\":{\"anchors\":[{\"type\":\"CodeFile\",\"property\":\"path\",\"match\":\"regex\"}]}}";
        var badDirection = Manifest[..at] + "\"traversal\":{\"relations\":[{\"name\":\"CODE_TESTS\",\"direction\":\"both\"}]}}";

        FluentActions.Invoking(() => ModuleManifest.Parse(badMatch)).Should().Throw<FormatException>().WithMessage("*match*");
        FluentActions.Invoking(() => ModuleManifest.Parse(badDirection)).Should().Throw<FormatException>().WithMessage("*direction*");
        ModuleEditorSchemaTests.EditorAccepts("module", badMatch).Should().BeFalse();
    }

    [Fact]
    public async Task The_anchor_scope_flows_with_the_async_context_and_restores_the_outer_one()
    {
        ContextAnchors.Current.Should().BeEmpty();
        using (ContextAnchors.Begin([ContextAnchor.Path("a.cs")]))
        {
            using (ContextAnchors.Begin([ContextAnchor.Symbol("Inner")]))
                ContextAnchors.Current.Single().Value.Should().Be("Inner");
            await Task.Yield();
            ContextAnchors.Current.Single().Value.Should().Be("a.cs");
        }
        ContextAnchors.Current.Should().BeEmpty();
    }

    [Fact]
    public void A_manifest_without_traversal_keeps_its_fingerprint_shape()
    {
        var plain = ModuleManifest.Parse(Manifest[..Manifest.LastIndexOf(',', Manifest.IndexOf("\"traversal\"", StringComparison.Ordinal))] + "}");

        plain.Traversal.Should().BeNull();
        JsonSerializer.Serialize(plain).Should().NotContain("Traversal");
    }

    [Fact]
    public async Task The_question_orders_what_the_anchors_reach()
    {
        var (contributor, reader) = Contributor();

        var result = await contributor.ContributeResultAsync(Request(ContextAnchor.Path(StoreFile)) with { Query = "Refuse a downgrade of the store" });

        // "Downgrade is refused" holds two of the question's words: the expansion keeps it and the walk offers it first
        // (0.72 x 3), ahead of the other kept test (0.72) and the decision (0.7). Without the question it was cut by id.
        reader.Words.Should().AllSatisfy(w => w.Should().Equal("refuse", "downgrade", "store"));
        result.Section!.Items.Select(i => i.Id).Should().Equal("test-upgrade", "test-removal", "decision-close");
    }

    [Fact]
    public async Task Among_equal_paths_the_most_specific_link_comes_first()
    {
        var reader = Store();
        reader.Degrees["test-removal"] = 5;   // a test linked to five types says less about this one
        reader.Degrees["test-upgrade"] = 3;
        var (contributor, _) = Contributor(reader);

        var result = await contributor.ContributeResultAsync(Request(ContextAnchor.Path(StoreFile)));

        result.Section!.Items.Select(i => i.Id).Should().Equal("test-third", "test-upgrade", "decision-close");
    }

    [Fact]
    public void A_question_s_words_split_identifiers_and_drop_what_every_record_shares()
    {
        TraversalWords.Of("Make SupersedeReplacedFacts honour the as-of path for tests")
            .Should().Equal("supersede", "replaced", "facts", "honour", "path");
        TraversalWords.Of(null).Should().BeEmpty();
        TraversalWords.Of(string.Join(' ', Enumerable.Range(0, 40).Select(i => "word" + i))).Should().HaveCount(ModuleExpandQuery.MaximumWords);
    }

    [Fact]
    public void A_type_s_text_is_the_string_properties_its_template_names()
    {
        var properties = new Dictionary<string, ModuleProperty>
        {
            ["name"] = new() { Type = "string" }, ["file"] = new() { Type = "string" }, ["line"] = new() { Type = "int" },
            ["framework"] = new() { Type = "string" },
        };
        new ModuleType { Name = "CodemapTest", Render = "Test {name} ({file}) line {line}", Properties = properties }.TextProperties()
            .Should().Equal("name", "file");
        new ModuleType { Name = "CodemapTest", Properties = properties }.TextProperties().Should().Equal("file", "framework", "name");
    }

    [Fact]
    public async Task A_module_s_walk_follows_only_the_hops_it_declares()
    {
        var resolution = ModuleResolver.Resolve([ModuleManifest.Parse(Manifest.Replace("\"maxItems\":3", "\"maxItems\":3,\"hops\":1", StringComparison.Ordinal))],
            ModuleVersion.Parse("0.1.0"), ModuleHostFeatures.Known);
        var contributor = new TraversalContributor(resolution.Modules.Single(), new TraversalService(resolution, Store()));

        var result = await contributor.ContributeResultAsync(Request(ContextAnchor.Path(StoreFile)));

        // One hop reaches the symbol the file defines (a hub, never offered), not the tests one hop further.
        result.Section!.Items.Select(i => i.Id).Should().Equal("decision-close");
        ModuleManifest.Parse(Manifest.Replace("\"maxItems\":3", "\"maxItems\":3,\"hops\":4", StringComparison.Ordinal)).Validate()
            .Should().Contain(e => e.Contains("hops"));
    }
}
