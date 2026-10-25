using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using AgentMemory.Inference;
using AgentMemory.RouterArena.Data;
using Microsoft.Extensions.AI;

namespace AgentMemory.RouterArena.Ask;

/// <summary>
/// <c>ask-chat</c> (R-chat-judge): the library's chat-model gate asked about every recorded turn of a set, once. It is shown
/// exactly what JEV was shown (the set's <c>gate-pairs-all.jsonl</c>: every memory found, with its dates and kind, and the
/// door's meaning), in one call per turn instead of one per door. Answers are recorded as <c>jev-answers/1</c> so
/// <c>cv_jar.py</c> scores them like every other judge.
/// The three-stage protocol: <c>--dry-run</c> prints a real request and the projected cost and spends nothing;
/// <c>--limit 1</c> asks one turn; then every turn not yet answered. A failed turn is asked again on the next run.
/// </summary>
internal sealed class ChatGateAsk(TextWriter output)
{
    /// <summary>Bitdeer's listed GLM-5.3-Flash price (USD per million tokens), as recorded in the strategy runs.</summary>
    private const double InPrice = 0.07, OutPrice = 0.25;

    private sealed record Turn(string Id, string Text, IReadOnlyList<MemoryGateTurn> Prior, IReadOnlyList<MemoryGateCandidate> Candidates);

    internal static IReadOnlyList<(string Id, MemoryGateRequest Request)> Requests(string pairsPath, DateTimeOffset now)
    {
        var turns = new List<Turn>();
        var byId = new Dictionary<string, (Turn Turn, HashSet<string> Keys, List<MemoryGateCandidate> Candidates)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(pairsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = JsonNode.Parse(line)!;
            var id = (string)row["turn"]!;
            if (!byId.TryGetValue(id, out var entry))
            {
                var prior = (row["prior"] as JsonArray ?? []).Select(p => (string)p!).Select(p =>
                {
                    var at = p.IndexOf(": ", StringComparison.Ordinal);
                    return at < 0 ? new MemoryGateTurn("user", p) : new MemoryGateTurn(p[..at], p[(at + 2)..]);
                }).ToList();
                var candidates = new List<MemoryGateCandidate>();
                entry = (new Turn(id, (string)row["text"]!, prior, candidates), new HashSet<string>(StringComparer.Ordinal), candidates);
                byId[id] = entry;
                turns.Add(entry.Turn);
            }
            var key = $"{(string)row["door"]!}|{(string)row["memory"]!}";
            if (entry.Keys.Add(key)) entry.Candidates.Add(new MemoryGateCandidate(key, (string)row["door"]!, (string)row["memoryText"]!));
        }
        return [.. turns.Select(t => (t.Id, new MemoryGateRequest(t.Text, t.Prior, now, t.Candidates)))];
    }

    /// <summary>The meaning of every door, as JEV read it (<see cref="Doors.Meaning"/>).</summary>
    internal static IReadOnlyDictionary<string, string> Meanings() =>
        Doors.All.ToDictionary(Doors.Name, Doors.Meaning, StringComparer.Ordinal);

    public async Task<int> RunAsync(string pairsPath, string outPath, DateTimeOffset now, bool dryRun, int? limit, int workers,
        string? model, ReasoningEffort? effort = null, CancellationToken cancellationToken = default)
    {
        var requests = Requests(pairsPath, now);
        var cache = File.Exists(outPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(outPath, cancellationToken).ConfigureAwait(false))!.AsObject()
            : new JsonObject
            {
                ["format"] = "jev-answers/1", ["mode"] = "items", ["judge"] = "chat-model gate (AgentMemory.Gate.ChatModelMemoryGate)",
                ["pairs"] = Path.GetFileName(pairsPath), ["pairsSha256"] = Sha256(pairsPath), ["now"] = now.ToString("O"),
                ["context"] = true, ["examples"] = 0, ["single"] = false, ["effort"] = effort?.ToString() ?? "provider default",
                ["answers"] = new JsonObject(),
            };
        if (((string?)cache["effort"] ?? "provider default") != (effort?.ToString() ?? "provider default"))
        {
            output.WriteLine($"error: ask-chat: {outPath} holds answers at effort '{cache["effort"]}'; use another --out");
            return 1;
        }
        var answers = cache["answers"]!.AsObject();
        var todo = requests.Where(r => answers[r.Id]?["yes"] is null).ToList();
        if (limit is { } n) todo = [.. todo.Take(n)];
        var meanings = Meanings();
        if (dryRun)
        {
            var sample = todo.FirstOrDefault(r => r.Request.Conversation.Count > 0);
            if (sample.Request is null) sample = todo.FirstOrDefault();
            if (sample.Request is not null)
                foreach (var m in ChatModelMemoryGate.Messages(sample.Request, [.. sample.Request.Candidates.Select((_, i) => $"m{i + 1}")], meanings))
                    output.WriteLine($"--- {m.Role}\n{m.Text}");
            var inTokens = todo.Sum(r => ChatModelMemoryGate.Messages(r.Request, [.. r.Request.Candidates.Select((_, i) => $"m{i + 1}")], meanings)
                .Sum(m => m.Text.Length)) / 4.0;
            var outTokens = todo.Sum(r => 8.0 * r.Request.Candidates.Count + 40);
            output.WriteLine($"ask-chat: {todo.Count} turns to ask, {todo.Count} calls, {todo.Sum(r => r.Request.Candidates.Count)} memories; "
                + $"about {inTokens:N0} tokens in and {outTokens:N0} out before any reasoning, "
                + $"about {(inTokens * InPrice + outTokens * OutPrice) / 1e6:0.###} USD at GLM-5.3-Flash prices; "
                + $"key present: {Environment.GetEnvironmentVariable("BITDEER_API_KEY") is { Length: > 0 }}. Nothing was sent.");
            return 0;
        }
        var resolution = InferenceProviderEnvironment.Resolve(Environment.GetEnvironmentVariable);
        if (resolution.Settings is not { } settings
            || !InferenceClientFactory.TryCreateChatClient(settings, "judge", out var chat, out var why, model: model, generousTimeout: true))
        {
            output.WriteLine($"error: ask-chat: no chat model ({resolution.Diagnostic})");
            return 1;
        }
        cache["model"] = model ?? settings.Model;
        cache["provider"] = settings.Provider.ToString();
        output.WriteLine($"ask-chat: {todo.Count} turns, one call each, to {settings.Provider} ({model ?? settings.Model})");
        var gate = new SemaphoreSlim(workers);
        var save = new object();
        var done = 0;
        await Task.WhenAll(todo.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                JsonObject? answer = null;
                string? error = null;
                // One meter for the turn: every attempt is paid for, the failed ones too. A re-asked turn keeps what its
                // earlier failed runs cost, so the spend in the file stays whole.
                JsonNode? before;
                lock (save) before = answers[item.Id];
                var earlier = Math.Round(((double?)before?["cost"] ?? 0) + ((double?)before?["costEarlier"] ?? 0), 6);
                var metered = new MeteredChat(chat);
                var all = Stopwatch.StartNew();
                var attempts = 0;
                double Cost() => Math.Round((metered.In * InPrice + metered.Out * OutPrice) / 1e6, 6);
                for (var retry = true; retry && attempts < 3; )
                {
                    attempts++;
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        var decision = await new ChatModelMemoryGate(metered, meanings, effort).DecideAsync(item.Request, cancellationToken).ConfigureAwait(false);
                        var yes = new JsonObject();
                        foreach (var (key, p) in decision.Probabilities) yes[key] = Math.Round(p, 4);
                        answer = new JsonObject
                        {
                            ["yes"] = yes, ["calls"] = attempts, ["attempts"] = attempts,
                            ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3), ["secondsAll"] = Math.Round(all.Elapsed.TotalSeconds, 3),
                            ["tokensIn"] = metered.In, ["tokensOut"] = metered.Out, ["cost"] = Cost(), ["model"] = decision.AnsweredBy,
                        };
                        retry = false;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested) throw;
                        error = $"{ex.GetType().Name}: {ex.Message}";
                        // A refused reply at temperature 0 comes back the same: only transport failures are worth a retry.
                        retry = ex is not InvalidOperationException;
                    }
                }
                answer ??= new JsonObject
                {
                    ["error"] = error, ["attempts"] = attempts, ["secondsAll"] = Math.Round(all.Elapsed.TotalSeconds, 3),
                    ["tokensIn"] = metered.In, ["tokensOut"] = metered.Out, ["cost"] = Cost(),
                };
                if (earlier > 0) answer["costEarlier"] = earlier;
                lock (save) answers[item.Id] = answer;
            }
            finally
            {
                gate.Release();
                lock (save)
                {
                    if (++done % 25 == 0 || done == todo.Count)
                    {
                        File.WriteAllText(outPath, cache.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        output.WriteLine($"  {done}/{todo.Count}");
                    }
                }
            }
        })).ConfigureAwait(false);
        var answered = answers.Select(a => a.Value).Where(a => a?["yes"] is not null).ToList();
        var failed = answers.Count(a => a.Value?["error"] is not null);
        var seconds = answered.Select(a => (double)a!["seconds"]!).Order().ToList();
        string At(double q) => seconds.Count == 0 ? "0" : seconds[Math.Min(seconds.Count - 1, (int)(q * seconds.Count))].ToString("0.##", CultureInfo.InvariantCulture);
        output.WriteLine($"ask-chat: {answered.Count} turns answered, {failed} failed; tokens in {answered.Sum(a => (long?)a!["tokensIn"] ?? 0):N0}, "
            + $"out {answered.Sum(a => (long?)a!["tokensOut"] ?? 0):N0}; cost {answers.Sum(a => ((double?)a.Value?["cost"] ?? 0) + ((double?)a.Value?["costEarlier"] ?? 0)):0.####} USD "
            + "(failed turns included); "
            + $"{At(0.5)} s a turn at the median, {At(0.95)} s at p95");
        return failed > 0 ? 2 : 0;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>Counts the tokens of the one call it carries.</summary>
    private sealed class MeteredChat(IChatClient inner) : DelegatingChatClient(inner)
    {
        public long In { get; private set; }
        public long Out { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            In += response.Usage?.InputTokenCount ?? 0;
            Out += response.Usage?.OutputTokenCount ?? 0;
            return response;
        }

        protected override void Dispose(bool disposing) { }
    }
}
