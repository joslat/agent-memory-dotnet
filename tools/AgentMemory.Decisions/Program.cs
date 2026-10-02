using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Decisions;
using AgentMemory.Inference;

// PLAN 40.16: the decision-recall check. Pre-registered in strategy/performance/prereg/PREREG-DECISION-RECALL-2026-10-01.md.
//   check --fixture <chains.json> --out <dir> [--systems keyword,vector,kernel] [--vector-repeats 2] [--top 8]
//         [--limit-chains N] [--today 2026-10-01] [--dry-run] [--kernel-build]
// kernel needs Neo4j (Neo4j__Uri/__Username/__Password) and replays each corpus into memory once (cached by manifest).
// --dry-run retrieves only (no answer model): where each answer decision sits on the corpus, and whether retrieval finds it.
if (args.Length == 0 || args[0] != "check")
{
    Console.Error.WriteLine("usage: check --fixture <chains.json> --out <dir> [--systems keyword,vector] [--vector-repeats 2] [--top 8] [--limit-chains N] [--today yyyy-mm-dd] [--dry-run]");
    return 2;
}

string? Value(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var fixturePath = Value("--fixture") ?? throw new ArgumentException("--fixture is required");
var outDir = Directory.CreateDirectory(Value("--out") ?? throw new ArgumentException("--out is required")).FullName;
var systems = (Value("--systems") ?? "keyword,vector").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var vectorRepeats = int.Parse(Value("--vector-repeats") ?? "2", CultureInfo.InvariantCulture);
var top = int.Parse(Value("--top") ?? "8", CultureInfo.InvariantCulture);
var limitChains = Value("--limit-chains") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : int.MaxValue;
var today = Value("--today") ?? "2026-10-01";
var dryRun = args.Contains("--dry-run");
var onlySets = Value("--sets")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var fixture = DecisionFixtureFile.Load(fixturePath);
var corpusSpec = JsonNode.Parse(File.ReadAllText(fixturePath))!["corpus"]!.AsObject();

// The corpora at their pinned revisions, read through git (the working trees are not touched).
var chunksBySet = new Dictionary<string, IReadOnlyList<DecisionChunk>>(StringComparer.Ordinal);
foreach (var (set, node) in corpusSpec)
{
    if (onlySets is not null && !onlySets.Contains(set)) continue;
    var local = node!["local"]!.GetValue<string>();
    var pinned = node["pinned"]!.GetValue<string>().Split(' ')[0];
    if (set == "C")
    {
        chunksBySet[set] = DecisionCorpus.FromChangelog(DecisionCorpus.GitShow(local, pinned, "CHANGELOG.md"), "CHANGELOG.md", set);
        continue;
    }
    var chunks = new List<DecisionChunk>();
    foreach (var file in GitLines(local, "ls-tree", "--name-only", pinned, "docs/decisions/").Where(f => f.EndsWith(".md", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
    {
        var name = Path.GetFileName(file);
        if (DecisionCorpus.IsTemplate(name) || name.Equals("README.md", StringComparison.OrdinalIgnoreCase)) continue;
        chunks.AddRange(DecisionCorpus.FromAdr(DecisionCorpus.GitShow(local, pinned, file), file, set, chunks.Count));
    }
    chunksBySet[set] = chunks;
}
var allChunks = chunksBySet.Values.SelectMany(chunks => chunks).ToList();
// Only the sets being run: with --sets B the other sets' decisions are not "unplaced", they are out of scope.
var inScope = fixture with { Chains = [.. fixture.Chains.Where(chain => chunksBySet.ContainsKey(chain.Set))] };
var located = DecisionLabels.Locate(inScope, allChunks);
var unplaced = located.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToList();
Console.WriteLine($"decisions: corpus {string.Join(", ", chunksBySet.Select(p => $"{p.Key} {p.Value.Count} chunks"))}; "
    + $"{located.Count - unplaced.Count}/{located.Count} labelled decisions placed on a chunk");
if (unplaced.Count > 0) Console.WriteLine($"  NOT PLACED (fix before any paid run): {string.Join(", ", unplaced)}");
foreach (var id in unplaced)
{
    var decision = inScope.Chains.SelectMany(c => c.Decisions).First(d => d.Id == id);
    var same = allChunks.Where(c => c.File.EndsWith(Path.GetFileName(decision.SourceFile), StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"    {id}: wants '{decision.SourceFile}' / '{decision.Heading}'; that file has {same.Count} chunks: "
        + string.Join(" | ", same.Take(12).Select(c => $"{c.Id}:{c.Heading}")));
}

var questions = DecisionLabels.Questions(fixture)
    .Where(q => chunksBySet.ContainsKey(q.Set))
    .Where(q => q.ChainId is null || fixture.Chains.Select(c => c.Id).Take(limitChains).Contains(q.ChainId))
    .Where(q => limitChains == int.MaxValue || q.ChainId is not null)
    .ToList();

InferenceProviderSettings? settings = InferenceProviderEnvironment.Resolve().Settings;
var retrievers = new Dictionary<string, Func<string, IDecisionRetriever>>(StringComparer.Ordinal);
if (systems.Contains("keyword"))
{
    var bySet = chunksBySet.ToDictionary(p => p.Key, p => (IDecisionRetriever)new KeywordRetriever(p.Value));
    retrievers["keyword"] = set => bySet[set];
}
if (systems.Contains("vector"))
{
    if (settings is null || !InferenceClientFactory.TryCreateEmbeddingGenerator(settings, out var embedder, out var why))
        throw new InvalidOperationException("No embedding model: set the inference provider (see docs/configuration/inference-providers.md).");
    var bySet = new Dictionary<string, IDecisionRetriever>(StringComparer.Ordinal);
    foreach (var (set, chunks) in chunksBySet)
        bySet[set] = await VectorRetriever.CreateAsync(chunks, embedder, Path.Combine(outDir, $"embeddings-{set}.json"), CancellationToken.None);
    retrievers["vector"] = set => bySet[set];
}
if (systems.Contains("kernel"))
{
    // S2: the corpora replayed into AgentMemory (one owner per set). A built store is reused when its manifest exists, so
    // answering again costs no extraction. --kernel-build forces a fresh replay under new owners.
    var services = DecisionKernelHost.Build(settings ?? throw new InvalidOperationException("No inference provider for the kernel."));
    // The corpora are independent owners: replayed side by side.
    var built = await Task.WhenAll(chunksBySet.Select(async pair => (pair.Key, Kernel: await DecisionKernelHost.BuildOrLoadAsync(
        services, pair.Key, pair.Value, Path.Combine(outDir, $"kernel-{pair.Key}.json"), args.Contains("--kernel-build"), dryRun,
        Console.WriteLine, CancellationToken.None))));
    var bySet = built.ToDictionary(b => b.Key, b => b.Kernel, StringComparer.Ordinal);
    retrievers["kernel"] = set => bySet[set];
}

// Retrieval alone: is an answering decision among the top k? Free; it bounds what any answer can do.
foreach (var (system, retriever) in retrievers)
{
    var answerable = questions.Where(q => q.Kind != "abstention").ToList();
    var found = 0;
    foreach (var q in answerable)
    {
        var got = await Retrieve(retriever(q.Set), q, top);
        if (q.Answers.Any(id => located[id].Intersect(got.Select(c => c.Id)).Any())) found++;
    }
    Console.WriteLine($"  {system}: an answering decision in the top {top} for {found}/{answerable.Count} questions");
}
if (dryRun) return unplaced.Count == 0 ? 0 : 1;
if (unplaced.Count > 0) { Console.Error.WriteLine("refusing a paid run with unplaced decisions"); return 1; }

if (settings is null || !InferenceClientFactory.TryCreateChatClient(settings, "answer", out var chat, out var chatWhy))
    throw new InvalidOperationException("No chat model: set the inference provider.");
var answerer = new DecisionAnswerer(chat, today);
var summary = new JsonObject
{
    ["fixture"] = Path.GetFileName(fixturePath),
    ["answerModel"] = settings.Model,
    ["embeddingModel"] = settings.EmbeddingModel,
    ["top"] = top,
    ["today"] = today,
    ["questions"] = questions.Count,
};
var runs = new List<(string Name, string System)>();
foreach (var system in retrievers.Keys)
    for (var r = 1; r <= (system == "vector" ? vectorRepeats : 1); r++) runs.Add(($"{system}{(system == "vector" && vectorRepeats > 1 ? $"-r{r}" : "")}", system));

using var gate = new SemaphoreSlim(6);
foreach (var (name, system) in runs)
{
    var watch = Stopwatch.StartNew();
    var answers = await Task.WhenAll(questions.Select(async q =>
    {
        await gate.WaitAsync();
        try
        {
            var sources = await Retrieve(retrievers[system](q.Set), q, top);
            var (abstained, cited, answer) = await answerer.AnswerAsync(q.Text, sources, CancellationToken.None);
            return DecisionGrading.Grade(q, abstained, cited, answer, located);
        }
        finally { gate.Release(); }
    }));
    await File.WriteAllLinesAsync(Path.Combine(outDir, $"answers-{name}.jsonl"), answers.Select(a => JsonSerializer.Serialize(a)));
    var metrics = DecisionGrading.Summarize(answers);
    summary[name] = JsonSerializer.SerializeToNode(metrics.ToDictionary(p => p.Key, p => double.IsNaN(p.Value) ? (double?)null : Math.Round(p.Value, 1)));
    Console.WriteLine($"  {name} ({watch.Elapsed.TotalSeconds:0}s): " + string.Join(", ", metrics.Select(p => $"{p.Key} {p.Value:0.0}")));
}
await File.WriteAllTextAsync(Path.Combine(outDir, "summary.json"), summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"decisions: written to {outDir}");
return 0;

// The kernel reads a dated question as of its date; the baselines read the text.
static Task<IReadOnlyList<DecisionChunk>> Retrieve(IDecisionRetriever retriever, DecisionQuestion question, int top) =>
    retriever is DecisionKernel kernel && question.Kind == "in_force_on_date"
        ? kernel.RetrieveAsync(question.Text, question.Date, top, CancellationToken.None)
        : retriever.RetrieveAsync(question.Text, top, CancellationToken.None);

static IEnumerable<string> GitLines(string repository, params string[] arguments)
{
    var start = new ProcessStartInfo("git", ["-C", repository, .. arguments]) { RedirectStandardOutput = true };
    using var process = Process.Start(start)!;
    var lines = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    process.WaitForExit();
    return lines;
}
