#pragma warning disable AMEXT001 // The trial composes Extensibility preview packs.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Core.Stubs;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Packs.Coding;
using AgentMemory.Packs.Repository;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// The traversal-context trial (coding packs CMP-4): for each task of a fixture (eng/evaluation/traversal-fixture.json), what the
/// Coding composite offers before the gate with the task's anchors (arm A) and with its question alone (arm B), and how long each
/// compile takes. Deterministic: no model runs, search views are full-text. The repository pack is left out: a fresh store has no
/// lessons, and its writer needs a model. <c>evaluate --traversal &lt;fixture&gt; [--rounds 3] [--warmup pass|one] [--output report.json]</c>.
/// Warm-up <c>pass</c> (protocol 2, the default) compiles every task in both arms once, untimed, so a freshly started store is
/// warm for both arms alike; <c>one</c> (protocol 1) compiles only the first task.
/// </summary>
internal sealed class TraversalTrialCommand(Action<Neo4jOptions> configureNeo4j, TextWriter output)
{
    private const string CodeMap = "io.agentmemory.packs.codemap";
    private const string Decisions = "io.agentmemory.packs.decisions";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<int> ExecuteAsync(string? fixturePath, string? outputPath, string? roundsValue, string? warmup = null)
    {
        if (warmup is not (null or "pass" or "one"))
        {
            output.WriteLine("error: evaluate --warmup is pass or one.");
            return 2;
        }
        if (fixturePath is null || !File.Exists(fixturePath))
        {
            output.WriteLine("error: evaluate --traversal needs a fixture file (eng/evaluation/traversal-fixture.json).");
            return 2;
        }
        var rounds = roundsValue is null ? 3 : int.TryParse(roundsValue, out var r) && r is >= 1 and <= 20 ? r : -1;
        if (rounds < 0)
        {
            output.WriteLine("error: evaluate --rounds must be an integer between 1 and 20.");
            return 2;
        }
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(fixturePath).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("The fixture is empty.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(_ => { }, configureNeo4j);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), 1536));
        services.AddAgentMemoryModules(m => m.UseNeo4jStore().AddComposite<CodingPack>(o => o.Disable(RepositoryPack.Id)));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Every composed member's schema before any read, as a host applies it: a member the trial imports nothing into (work,
        // without a ledger) would otherwise leave migrations pending, and traversal reads refuse while any are.
        await using (var migrations = provider.CreateAsyncScope())
        {
            var set = migrations.ServiceProvider.GetRequiredService<AgentMemory.Extensibility.Modules.ModuleSet>();
            var applied = await new AgentMemory.Extensibility.Neo4j.Migrations.ModuleMigrator(
                migrations.ServiceProvider.GetRequiredService<INeo4jTransactionRunner>(), set.Resolution).ApplyAsync("traversal trial").ConfigureAwait(false);
            if (applied.Problems.Count != 0)
            {
                output.WriteLine("error: module migrations failed: " + string.Join("; ", applied.Problems));
                return 1;
            }
        }

        // Warm-up: connections, the module schema check, query plans; with "pass", the store's own warm-up after a start too.
        foreach (var task in warmup == "one" ? fixture.Tasks.Take(1) : fixture.Tasks)
            foreach (var anchored in new[] { true, false })
                await CompileAsync(provider, task, anchored).ConfigureAwait(false);

        var results = fixture.Tasks.Select(t => new TaskResult(t)).ToArray();
        for (var round = 0; round < rounds; round++)
            foreach (var result in results)
                foreach (var anchored in new[] { true, false })
                {
                    var (envelope, milliseconds) = await CompileAsync(provider, result.Task, anchored).ConfigureAwait(false);
                    (anchored ? result.TraversalMs : result.SearchMs).Add(milliseconds);
                    if (round == 0) result.Record(envelope, anchored);
                }

        var report = Report(fixture, results, rounds, warmup ?? "pass");
        var destination = outputPath ?? Path.Combine("artifacts", "evaluation", $"traversal-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, Json)).ConfigureAwait(false);
        output.WriteLine(JsonSerializer.Serialize(report.Summary, Json));
        output.WriteLine($"traversal trial report: {destination}");
        return 0;
    }

    private static async Task<(ContextEnvelope Envelope, double Milliseconds)> CompileAsync(IServiceProvider provider, FixtureTask task, bool anchored)
    {
        await using var scope = provider.CreateAsyncScope();
        var compiler = scope.ServiceProvider.GetRequiredService<IContextCompiler>();
        var request = new ContextRequest
        {
            SessionId = "traversal-trial", Owner = "traversal-trial", Query = task.Question,
            Anchors = anchored ? [.. task.Anchors.Select(a => a.Kind == "symbol" ? ContextAnchor.Symbol(a.Value) : ContextAnchor.Path(a.Value))] : [],
        };
        var watch = Stopwatch.StartNew();
        var envelope = await compiler.CompileAsync(request).ConfigureAwait(false);
        return (envelope, watch.Elapsed.TotalMilliseconds);
    }

    private static TrialReport Report(Fixture fixture, TaskResult[] results, int rounds, string warmup)
    {
        var impact = results.Where(r => r.Task.Kind == "test-impact").ToArray();
        var offered = impact.Sum(r => r.Tests.Count);
        var inExpected = impact.Sum(r => r.Tests.Count(t => r.Expected(t.File)));
        var perTask = impact.Where(r => r.Tests.Count > 0).Select(r => r.Tests.Count(t => r.Expected(t.File)) / (double)r.Tests.Count).ToArray();
        var decisions = results.Where(r => r.Task.Kind == "decision").ToArray();
        var direct = decisions.Where(r => r.Task.Indirect != true).ToArray();
        double P95(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
        }
        var traversalP95 = P95(results.SelectMany(r => r.TraversalMs));
        var searchP95 = P95(results.SelectMany(r => r.SearchMs));
        var summary = new TrialSummary
        {
            Head = fixture.Head, Rounds = rounds, Warmup = warmup,
            TestImpactTasks = impact.Length, TestImpactHits = impact.Count(r => r.TestHit),
            TestImpactHitRate = Math.Round(impact.Count(r => r.TestHit) / (double)Math.Max(1, impact.Length), 3),
            TestsOffered = offered, TestsInExpectedFiles = inExpected,
            TestPrecisionPooled = Math.Round(inExpected / (double)Math.Max(1, offered), 3),
            TestPrecisionPerTask = perTask.Length == 0 ? 0 : Math.Round(perTask.Average(), 3),
            TasksOfferingNoTest = impact.Count(r => r.Tests.Count == 0),
            DirectDecisionTasks = direct.Length,
            DirectDecisionHitsTraversal = direct.Count(r => r.DecisionHitTraversal),
            DirectDecisionHitsSearch = direct.Count(r => r.DecisionHitSearch),
            IndirectDecisionHitsTraversal = decisions.Count(r => r.Task.Indirect == true && r.DecisionHitTraversal),
            TraversalP95Ms = Math.Round(traversalP95, 1), SearchP95Ms = Math.Round(searchP95, 1),
            AddedP95Ms = Math.Round(traversalP95 - searchP95, 1),
            TraversalMedianMs = Math.Round(results.SelectMany(r => r.TraversalMs).Order().ElementAt(results.Sum(r => r.TraversalMs.Count) / 2), 1),
        };
        return new TrialReport(summary, [.. results.Select(r => r.Detail())]);
    }

    private sealed class TaskResult(FixtureTask task)
    {
        public FixtureTask Task { get; } = task;
        public List<double> TraversalMs { get; } = [];
        public List<double> SearchMs { get; } = [];
        public List<OfferedTest> Tests { get; } = [];
        public List<OfferedItem> TraversalItems { get; } = [];
        public List<OfferedItem> SearchItems { get; } = [];
        public List<string> Omissions { get; } = [];
        public bool TestHit => Tests.Any(t => Expected(t.File));
        public bool DecisionHitTraversal => DecisionOffered(TraversalItems);
        public bool DecisionHitSearch => DecisionOffered(SearchItems);

        public bool Expected(string? file) => file is not null && Task.Expect.TestFiles?.Contains(file, StringComparer.OrdinalIgnoreCase) == true;

        private bool DecisionOffered(List<OfferedItem> items) => Task.Expect.DecisionTitles?.Any(title =>
            items.Any(i => i.Module == Decisions && i.Text.Contains(title, StringComparison.OrdinalIgnoreCase))) == true;

        public void Record(ContextEnvelope envelope, bool anchored)
        {
            var items = anchored ? TraversalItems : SearchItems;
            foreach (var section in envelope.Sections)
                foreach (var item in section.Items)
                {
                    var module = section.Binding?.ModuleId ?? item.Provenance?.RecordModuleId ?? section.ContributorId;
                    items.Add(new OfferedItem(section.ContributorId, module, item.Provenance?.RecordType, item.Text, item.Provenance?.Via));
                    if (anchored && module == CodeMap && item.Provenance?.RecordType == "CodemapTest")
                        Tests.Add(new OfferedTest(item.Text, TestFile(item.Text)));
                }
            if (anchored)
                Omissions.AddRange(envelope.Omissions.Where(o => o.Reason != ContextOmissionReason.NotApplicable)
                    .Select(o => $"{o.ContributorId}: {o.Reason}{(o.Detail is null ? "" : " " + o.Detail)}"));
        }

        // The code map renders a test as "Test {name} ({file})"; the traversal contributor then adds "(reached from …)".
        private static string? TestFile(string text) =>
            System.Text.RegularExpressions.Regex.Match(text, @"\(([^()]+\.cs)\)") is { Success: true } match ? match.Groups[1].Value : null;

        public object Detail() => new
        {
            Task.Id, Task.Kind, Task.Question, TestHit = Task.Kind == "test-impact" ? TestHit : (bool?)null,
            DecisionHitTraversal = Task.Kind == "decision" ? DecisionHitTraversal : (bool?)null,
            DecisionHitSearch = Task.Kind == "decision" ? DecisionHitSearch : (bool?)null,
            Expected = (object?)Task.Expect.TestFiles ?? Task.Expect.DecisionTitles,
            Traversal = TraversalItems, Search = SearchItems,
            TraversalMs = TraversalMs.Select(m => Math.Round(m, 1)), SearchMs = SearchMs.Select(m => Math.Round(m, 1)),
            Omissions = Omissions.Count == 0 ? null : Omissions,
        };
    }

    private sealed record OfferedTest(string Text, string? File);
    private sealed record OfferedItem(string Contributor, string Module, string? Type, string Text, string? Via);

    private sealed record TrialReport(TrialSummary Summary, IReadOnlyList<object> Tasks);

    private sealed record TrialSummary
    {
        public string? Head { get; init; }
        public int Rounds { get; init; }
        public string? Warmup { get; init; }
        public int TestImpactTasks { get; init; }
        public int TestImpactHits { get; init; }
        public double TestImpactHitRate { get; init; }
        public int TestsOffered { get; init; }
        public int TestsInExpectedFiles { get; init; }
        public double TestPrecisionPooled { get; init; }
        public double TestPrecisionPerTask { get; init; }
        public int TasksOfferingNoTest { get; init; }
        public int DirectDecisionTasks { get; init; }
        public int DirectDecisionHitsTraversal { get; init; }
        public int DirectDecisionHitsSearch { get; init; }
        public int IndirectDecisionHitsTraversal { get; init; }
        public double TraversalP95Ms { get; init; }
        public double SearchP95Ms { get; init; }
        public double AddedP95Ms { get; init; }
        public double TraversalMedianMs { get; init; }
    }

    private sealed record Fixture(string? Head, IReadOnlyList<FixtureTask> Tasks);
    private sealed record FixtureTask(string Id, string Kind, string Question, IReadOnlyList<FixtureAnchor> Anchors, FixtureExpect Expect, bool? Indirect);
    private sealed record FixtureAnchor(string Kind, string Value);
    private sealed record FixtureExpect(IReadOnlyList<string>? TestFiles, IReadOnlyList<string>? DecisionTitles);
}
