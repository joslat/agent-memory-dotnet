using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Services;
using AgentMemory.Inference;
using AgentMemory.Scenarios;
using AgentMemory.Scenarios.Memories;
using Microsoft.Extensions.AI;
using Testcontainers.Neo4j;

// scenarios: a scripted history, written into each memory under test, then the same questions to each. What it checks
// is deterministic: the facts the store must hold, and the facts the prompt must contain, each in one item. Answers are
// logged for a person to read and never graded. Every model call, span and log line of the run is kept beside the report.
//
//   dotnet run --project tools/AgentMemory.Scenarios -- [--scenario <file>] [--arms a,b] [--max-calls 400]
//       [--output <dir>] [--budget-from <arm>] [--no-answers] [--uri bolt://... --password ...] [--dry-run]
//
// Models come from the environment, as AgentMemory.Inference resolves them (docs/configuration/inference-providers.md).
// The Recommended arm's judge is JEV and needs TYPESAFE_API_KEY.

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var root = RepositoryRoot();
var scenarioPath = Argument("--scenario") ?? Path.Combine(AppContext.BaseDirectory, "scenarios", "bruna-go-live.json");
var scenario = Scenario.Load(scenarioPath);
var armNames = (Argument("--arms") ?? string.Join(',', Arms.All)).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
var unknown = armNames.Except(Arms.All).ToList();
if (unknown.Count > 0)
{
    Console.Error.WriteLine($"Unknown arm(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", Arms.All)}.");
    return 2;
}

var maxCalls = int.Parse(Argument("--max-calls") ?? "400", CultureInfo.InvariantCulture);
var answers = !args.Contains("--no-answers");
var graphArms = Arms.Graph.Where(arm => armNames.Contains(arm.Name)).ToList();
var budgetFrom = Argument("--budget-from") ?? graphArms.FirstOrDefault()?.Name;
var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var output = Path.GetFullPath(Argument("--output") ?? Path.Combine(root, "artifacts", "evaluation", "scenarios", $"{scenario.Name}-{stamp}"));
var turns = scenario.Turns().ToList();
var writers = graphArms.GroupBy(arm => arm.Writes).Select(group => group.First().Name).ToHashSet();

Console.WriteLine();
Console.WriteLine($"SCENARIO  {scenario.Name}: {turns.Count} statements in {scenario.Sessions.Count} sessions, {scenario.Questions.Count} questions, asked {scenario.AskedOn:yyyy-MM-dd}");
Console.WriteLine($"  arms      {string.Join(", ", armNames)}");
Console.WriteLine($"  writes    {string.Join(", ", writers)} (the others recall from the store their writer wrote)");
Console.WriteLine($"  calls     at most {maxCalls}; about {writers.Count * turns.Count * 2} to write, {graphArms.Count * scenario.Questions.Count} judge calls, {(answers ? armNames.Length * scenario.Questions.Count : 0)} answers");
Console.WriteLine($"  output    {output}");
if (args.Contains("--dry-run")) return 0;

Directory.CreateDirectory(output);
var resolution = InferenceProviderEnvironment.Resolve();
if (resolution.Settings is not { } settings)
{
    Console.Error.WriteLine($"No model is configured. {resolution.Diagnostic} {resolution.EmbeddingDiagnostic}");
    return 2;
}

if (!InferenceClientFactory.TryCreateChatClient(settings, "scenario", out var chat, out var whyNoChat, generousTimeout: true))
{
    Console.Error.WriteLine($"Could not create the chat model. {whyNoChat}");
    return 2;
}

if (!InferenceClientFactory.TryCreateEmbeddingGenerator(settings, out var embeddings, out var whyNoEmbeddings))
{
    Console.Error.WriteLine($"Could not create the embedding model. {whyNoEmbeddings}");
    return 2;
}

if (graphArms.Any(arm => arm.Judge) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")))
{
    Console.Error.WriteLine("A Recommended arm needs its judge: set TYPESAFE_API_KEY.");
    return 2;
}

Console.WriteLine($"  models    {settings.Summary}");
using var ledger = new CallLedger(Path.Combine(output, "calls.jsonl"), maxCalls);
using var spans = new SpanRecorder(Path.Combine(output, "spans.jsonl"));
using var log = new FileLog(Path.Combine(output, "agentmemory.log"));
var memoryChat = new RecordingChatClient(chat, "memory", ledger);
var answerChat = new RecordingChatClient(chat, "answer", ledger);
var dimensions = settings.EmbeddingDimensions
    ?? (await embeddings.GenerateAsync(["ping"]))[0].Vector.Length;

// ── Neo4j: a container of the run's own, unless one is given ───────────────
Neo4jContainer? container = null;
Neo4jTarget neo4j;
if (Argument("--uri") is { } uri)
{
    neo4j = new Neo4jTarget(uri, Argument("--user") ?? "neo4j", Argument("--password") ?? "password", dimensions);
}
else
{
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    container = new Neo4jBuilder("neo4j:5.26")
        .WithEnvironment("NEO4J_AUTH", $"neo4j/{password}")
        .WithEnvironment("NEO4J_server_memory_heap_initial__size", "256m")
        .WithEnvironment("NEO4J_server_memory_heap_max__size", "512m")
        .WithEnvironment("NEO4J_server_memory_pagecache_size", "64m")
        .Build();
    await container.StartAsync();
    neo4j = new Neo4jTarget(container.GetConnectionString(), "neo4j", password, dimensions);
}

var failures = new List<string>();
try
{
    // ── The memories ───────────────────────────────────────────────────────
    var clock = new ScenarioClock { UtcNow = turns[0].At };
    var run = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    var memories = new List<IMemory>();
    if (armNames.Contains("file")) memories.Add(new FileMemory());
    if (armNames.Contains("vector")) memories.Add(new VectorMemory(embeddings));
    foreach (var arm in graphArms)
    {
        var owner = $"{scenario.Owner}-{arm.Writes}-{run}";
        memories.Add(await GraphMemory.CreateAsync(arm, memoryChat, embeddings, clock, neo4j, owner, writers.Contains(arm.Name), log));
    }

    // ── Writing: every memory hears the same statements, each at its moment ─
    Console.WriteLine();
    Console.WriteLine("WRITING");
    var took = memories.ToDictionary(memory => memory.Name, _ => TimeSpan.Zero);
    for (var i = 0; i < turns.Count; i++)
    {
        clock.UtcNow = turns[i].At;
        foreach (var memory in memories)
        {
            spans.Scope = log.Scope = $"write {memory.Name} {i + 1}";
            var watch = Stopwatch.StartNew();
            try
            {
                await memory.RememberAsync(turns[i]);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"write {memory.Name} turn {i + 1}: {exception.Message}");
            }

            took[memory.Name] += watch.Elapsed;
        }

        Console.Write($"\r  {i + 1}/{turns.Count}  {turns[i].At:yyyy-MM-dd}  model calls so far: {ledger.Calls}   ");

        // The library catches a refused call and writes on (a writer failure falls back to the extractors), so the cap
        // is read from the ledger, not from an exception: past it, nothing more is written or asked.
        if (ledger.Exhausted)
        {
            failures.Add($"the run reached its cap of {maxCalls} model calls at statement {i + 1} of {turns.Count}, and stopped");
            break;
        }
    }

    Console.WriteLine();
    foreach (var memory in memories)
        Console.WriteLine($"  {memory.Name,-22} {took[memory.Name].TotalSeconds,7:0.0} s   {memory.How}");

    // ── What each written store holds ──────────────────────────────────────
    var stores = new Dictionary<string, object>();
    foreach (var graph in memories.OfType<GraphMemory>().Where(g => g.Writes))
    {
        var snapshot = await graph.SnapshotAsync();
        var held = Checks.Store(scenario.Store ?? [], snapshot.Facts);
        var firstPerson = snapshot.Facts.Count(f => f.Subject.Trim().ToLowerInvariant() is "user" or "i" or "me" or "the user");
        stores[graph.Arm.Writes] = new { owner = graph.Owner, held, firstPersonSubjects = firstPerson, snapshot };
        Console.WriteLine($"  {graph.Arm.Writes} store: {snapshot.Facts.Count} live facts, {snapshot.Closed.Count} closed, {snapshot.Entities.Count} entities; " +
                          $"holds {held.Count(h => h.Held)}/{held.Count} checked facts; {firstPerson} facts with 'user' as subject");
        foreach (var miss in held.Where(h => !h.Held))
            Console.WriteLine($"      ✗ {miss.Fact}{(miss.SameWords.Count > 0 ? $"  (same words, other dates: {string.Join(" | ", miss.SameWords.Take(2))})" : "")}");
    }

    // ── Asking: a new conversation on the scenario's day ───────────────────
    clock.UtcNow = scenario.AskedAt;
    Console.WriteLine();
    Console.WriteLine($"ASKING  on {scenario.AskedOn:dddd, MMMM d, yyyy}");
    var asked = new List<object>();
    var scoreboard = new Dictionary<string, List<string>>();
    foreach (var question in scenario.Questions)
    {
        if (ledger.Exhausted)
        {
            if (!failures.Any(f => f.Contains("cap of", StringComparison.Ordinal)))
                failures.Add($"the run reached its cap of {maxCalls} model calls before question {question.Id}, and stopped");
            break;
        }

        var prompts = new Dictionary<string, string>();
        var explained = new Dictionary<string, object>();
        foreach (var graph in memories.OfType<GraphMemory>())
        {
            spans.Scope = log.Scope = $"ask {question.Id} {graph.Name}";
            try
            {
                prompts[graph.Name] = await graph.RecallAsync(question.Text, null);
                explained[graph.Name] = await graph.ExplainAsync(question.Text);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"ask {question.Id} {graph.Name}: {exception.Message}");
                prompts[graph.Name] = "";
            }
        }

        // The line memories get exactly the room the budget arm's prompt took, so nobody wins by a bigger prompt.
        var budget = budgetFrom is not null && prompts.TryGetValue(budgetFrom, out var budgetPrompt) && budgetPrompt.Length > 0
            ? Checks.Tokens(budgetPrompt)
            : 600;
        foreach (var line in memories.OfType<LineMemory>())
            prompts[line.Name] = await line.RecallAsync(question.Text, budget);

        var attempts = new List<object>();
        Console.WriteLine();
        Console.WriteLine($"  {question.Kind.ToUpperInvariant()}: \"{question.Text}\"  (line memories get {budget} tokens)");
        foreach (var memory in memories)
        {
            var prompt = prompts[memory.Name];
            var needs = Checks.Needs(question, prompt);
            string? answer = null;
            if (answers)
            {
                spans.Scope = log.Scope = $"answer {question.Id} {memory.Name}";
                try
                {
                    answer = await AnswerAsync(answerChat, scenario, prompt, question.Text);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add($"answer {question.Id} {memory.Name}: {exception.Message}");
                }
            }

            var smallest = memory is LineMemory lines ? await lines.SmallestBudgetThatWorksAsync(question) : null;
            var verdict = needs.All(n => n.Found) ? "✓" : "✗";
            (scoreboard.TryGetValue(memory.Name, out var row) ? row : scoreboard[memory.Name] = []).Add(verdict);
            Console.WriteLine($"    {verdict} {memory.Name,-22} {(needs.All(n => n.Found) ? "every needed fact reached the prompt" : "missing: " + string.Join("; ", needs.Where(n => !n.Found).Select(n => n.Fact)))}");
            if (answer is not null) Console.WriteLine($"        → {Flatten(answer, 150)}");
            attempts.Add(new
            {
                memory = memory.Name,
                tokens = Checks.Tokens(prompt),
                needs,
                smallestBudgetThatWorks = smallest,
                prompt,
                explained = explained.GetValueOrDefault(memory.Name),
                answer,
            });
        }

        asked.Add(new { question = question.Id, question.Kind, question.Text, question.RightAnswer, budget, attempts });
    }

    // ── The report ─────────────────────────────────────────────────────────
    Console.WriteLine();
    Console.WriteLine("RESULT  ✓ every needed fact reached the prompt, each in one item · ✗ one did not");
    Console.WriteLine($"  {"",-24}{string.Join("", scenario.Questions.Select(q => $"{q.Id,-11}"))}");
    foreach (var (name, verdicts) in scoreboard)
        Console.WriteLine($"  {name,-24}{string.Join("", verdicts.Select(v => $"{v,-11}"))}");

    var report = new
    {
        format = "agentmemory-scenario/1",
        complete = failures.Count == 0,
        failures,
        fingerprint = new
        {
            scenario = scenario.Name, scenarioSha256 = scenario.Sha256, source = scenario.Source,
            models = settings.Summary, judge = graphArms.Any(a => a.Judge) ? "jev (api.typesafe.ai/v1/systemone)" : null,
            neo4j = container is null ? neo4j.Uri : "neo4j:5.26 (Testcontainers)", commit = GitCommit(root), harness = "scenarios/1",
        },
        calls = new { total = ledger.Calls, byPurpose = ledger.ByPurpose, cap = maxCalls },
        warnings = log.Warnings,
        arms = memories.Select(m => new { m.Name, m.How, seconds = Math.Round(took[m.Name].TotalSeconds, 1), writes = m is not GraphMemory g || g.Writes }),
        stores,
        asked,
        scoreboard,
    };
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"),
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine();
    Console.WriteLine($"  calls {ledger.Calls} ({string.Join(", ", ledger.ByPurpose.Select(p => $"{p.Key} {p.Value}"))}), library warnings {log.Warnings}");
    Console.WriteLine($"  report, prompts, explained recalls: {Path.Combine(output, "report.json")}");
    Console.WriteLine($"  every model call: calls.jsonl · every span: spans.jsonl · the library's log: agentmemory.log");
    if (failures.Count > 0)
    {
        Console.WriteLine($"  INCOMPLETE: {failures.Count} failure(s); the first: {failures[0]}");
        return 1;
    }

    foreach (var memory in memories) await memory.DisposeAsync();
    return 0;
}
finally
{
    if (container is not null) await container.DisposeAsync();
}

// The same model and instructions for every memory; only the MEMORY block changes (Pablo Piovano's prompt, unchanged).
static async Task<string> AnswerAsync(IChatClient chat, Scenario scenario, string memory, string question)
{
    var today = scenario.AskedOn.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
    var instructions = $"""
        {scenario.AssistantBrief}
        Today is {today}.
        Answer only from the MEMORY below. If two commitments in it fall on the same dates in
        different places, say so first; otherwise do not mention conflicts.
        Keep the answer under 90 words.

        MEMORY
        {(string.IsNullOrWhiteSpace(memory) ? "(nothing)" : memory)}
        """;
    var response = await chat.GetResponseAsync(
        [new ChatMessage(ChatRole.System, instructions), new ChatMessage(ChatRole.User, question)],
        new ChatOptions { Temperature = 0 });
    return response.Text.Trim();
}

string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string Flatten(string text, int length)
{
    var line = string.Join(' ', text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
    return line.Length <= length ? line : line[..(length - 1)] + "…";
}

static string RepositoryRoot()
{
    for (var folder = new DirectoryInfo(Environment.CurrentDirectory); folder is not null; folder = folder.Parent)
        if (File.Exists(Path.Combine(folder.FullName, "AgentMemory.slnx"))) return folder.FullName;
    return Environment.CurrentDirectory;
}

static string? GitCommit(string root)
{
    try
    {
        using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD") { WorkingDirectory = root, RedirectStandardOutput = true });
        var head = git?.StandardOutput.ReadToEnd().Trim();
        git?.WaitForExit();
        using var status = Process.Start(new ProcessStartInfo("git", "status --porcelain") { WorkingDirectory = root, RedirectStandardOutput = true });
        var dirty = status?.StandardOutput.ReadToEnd().Trim().Length > 0;
        status?.WaitForExit();
        return dirty ? $"{head}+uncommitted" : head;
    }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return null;
    }
}

/// <summary>The script's clock: three months are replayed in minutes, so "now" is wherever the script has reached.</summary>
internal sealed class ScenarioClock : TimeProvider
{
        public override DateTimeOffset GetUtcNow() => UtcNow;
    public DateTimeOffset UtcNow { get; set; }
}
