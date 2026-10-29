#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Review 6, the walk's cost model: bounds on the work, not only on the results. A store returns only matches, best first, at
/// most what the walk keeps (M2); the in-memory expansion stops with the walk's budget (M3); globs are compiled once per
/// pattern without changing which anchor and pattern a match names (M4).
/// </summary>
public sealed class WalkWorkBoundsTests
{
    private const string Code = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.code","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Code","types":[{"name":"CodeFile","scope":"shared","render":"{path}","properties":{"path":{"type":"string","required":true}}}],
          "relations":[{"name":"CODE_USES","from":"CodeFile","to":"CodeFile"}]},
         "traversal":{"anchors":[{"type":"CodeFile","property":"path","match":"exact"}],"relations":[{"name":"CODE_USES","direction":"out","weight":0.5,"fanOut":3}],"maxItems":3}}
        """;

    private static readonly ModuleReadContext Context = new() { Scope = MemoryScope.For("pack") };

    private static ModuleProposal File(string path, params string[] uses) => new()
    {
        Kind = ProposalKind.Assert, Type = "CodeFile", Key = path,
        Properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["path"] = JsonSerializer.SerializeToElement(path) },
        Links = [.. uses.Select(u => new ProposedLink { Relation = "CODE_USES", Target = u })],
        Source = new ProposalSource { Excerpt = path },
    };

    private static PackMemory Memory(IEnumerable<ModuleProposal> records) =>
        PackMemory.From([], [new ModuleSnapshot { Module = ModuleManifest.Parse(Code), Source = "git:abc", Records = [.. records] }]);

    [Fact]
    public async Task M2_a_short_anchor_returns_only_its_matches_best_first_at_most_the_limit()
    {
        // 3,000 files whose paths hold "e", and one named "e": the store's superset is all of them; its answer is the one match.
        var memory = Memory([.. Enumerable.Range(0, 3000).Select(i => File($"src/e{i:0000}.cs")), File("e")]);

        var found = await memory.MatchAsync(new ModuleAnchorQuery
        {
            ModuleId = "com.example.code", Type = "CodeFile", Property = "path", Match = ModuleAnchorMatch.Exact, Values = ["e"], Limit = 257,
        }, Context);

        found.Records.Select(r => r.Key).Should().Equal("e");
        // Many matches: the limit, in the format's order among equals.
        var many = await memory.MatchAsync(new ModuleAnchorQuery
        {
            ModuleId = "com.example.code", Type = "CodeFile", Property = "path", Match = ModuleAnchorMatch.Exact,
            Values = [.. Enumerable.Range(0, 400).Select(i => $"src/e{i:0000}.cs")], Limit = 257,
        }, Context);
        many.Records.Should().HaveCount(257);
        many.Records.Select(r => r.Key).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task M3_the_in_memory_expansion_stops_with_the_budget()
    {
        var memory = Memory([File("src/a.cs", "src/b.cs"), File("src/b.cs")]);
        var a = (await memory.MatchAsync(new ModuleAnchorQuery
        {
            ModuleId = "com.example.code", Type = "CodeFile", Property = "path", Match = ModuleAnchorMatch.Exact, Values = ["src/a.cs"],
        }, Context)).Records.Single();
        var query = new ModuleExpandQuery { ModuleId = "com.example.code", Relation = "CODE_USES", Direction = ModuleTraversalDirection.Out, FromIds = [a.Id], FanOut = 3 };

        (await memory.ExpandAsync(query, Context)).Edges.Should().ContainSingle().Which.To.Key.Should().Be("src/b.cs");
        using var spent = new CancellationTokenSource();
        await spent.CancelAsync();
        await FluentActions.Awaiting(() => memory.ExpandAsync(query, Context, spent.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void L1_a_path_normalizes_in_one_pass_to_what_the_repeated_strip_gave()
    {
        // The definition before review 6: every leading "./" stripped one slice at a time, then every leading "/", repeated.
        static string Before(string value)
        {
            string path = value, previous;
            do
            {
                previous = path;
                path = AnchorMatcher.Trimmed(path).Replace('\\', '/');
                while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
                path = path.TrimStart('/');
            }
            while (path != previous);
            return path;
        }
        var rng = new Random(9);
        var pieces = new[] { ".", "/", "./", " ", "\\", "a", "..", "\t" };
        for (var i = 0; i < 20_000; i++)
        {
            var value = string.Concat(Enumerable.Range(0, rng.Next(12)).Select(_ => pieces[rng.Next(pieces.Length)]));
            AnchorMatcher.Normalized(value).Should().Be(Before(value), value);
        }
        // A long run of "./" is linear now: a million characters in well under a second.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        AnchorMatcher.Normalized(string.Concat(Enumerable.Repeat("./", 500_000)) + "a.cs").Should().Be("a.cs");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void M4_a_glob_compiled_once_names_the_same_anchor_and_pattern_as_matching_each_pair()
    {
        // The value's pieces are its patterns; the first best pair in anchor order wins, as before the change.
        var rng = new Random(6);
        string[] parts = ["src", "tests", "a.cs", "b.cs", "*.cs", "**", "**/", "?.cs", "Store", "store"];
        for (var s = 0; s < 2000; s++)
        {
            var value = string.Join(rng.Next(2) == 0 ? " " : ";", Enumerable.Range(0, 1 + rng.Next(3))
                .Select(_ => string.Join("/", Enumerable.Range(0, 1 + rng.Next(3)).Select(_ => parts[rng.Next(parts.Length)]))));
            var anchors = Enumerable.Range(0, 1 + rng.Next(4)).Select(_ => string.Join("/", Enumerable.Range(0, 1 + rng.Next(3))
                .Select(_ => parts[rng.Next(4)]))).ToList();
            var pieces = value.Split([' ', ';', ',', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(AnchorMatcher.Trimmed).Where(p => p.Length > 0).ToList();
            (double Quality, string? Anchor, string? Pattern) expected = (0, null, null);
            foreach (var anchor in anchors)
                foreach (var pattern in pieces)
                    if (AnchorMatcher.Quality(ModuleAnchorMatch.Glob, pattern, anchor) is var q && q > expected.Quality) expected = (q, anchor, pattern);

            AnchorMatcher.Best(ModuleAnchorMatch.Glob, value, anchors).Should().Be(expected, value);
        }
    }
}
