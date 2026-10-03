using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Ask;

/// <summary>
/// How to ask a decision model (40.75, 40.76): how many memories a gate is shown per door, whether it is told today's date
/// and each memory's dates and kind (<see cref="Context"/>), how many nearest labelled tuning turns it sees as examples,
/// whether each question goes in a call of its own (<see cref="Single"/>: to see whether batching moves the answers),
/// which turns are asked, and where (TypeSafe's JEV, or a local System One server such as Laya's).
/// </summary>
public sealed record JevAskOptions
{
    public int Shown { get; init; } = JevAsk.ShownPerDoor;
    public bool Context { get; init; }
    public int Examples { get; init; }
    public bool Single { get; init; }
    public string Split { get; init; } = "all";
    public string Endpoint { get; init; } = JevClient.TypeSafeEndpoint;

    /// <summary>The environment variable holding the key; none for a local server.</summary>
    public string? KeyVariable { get; init; } = "TYPESAFE_API_KEY";

    /// <summary>On a dry run, every request body is also written here, one JSON per line (a latency bench replays them).</summary>
    public string? DumpRequests { get; init; }
}

/// <summary>
/// <c>ask-jev</c>: the decision model JEV asked about every turn, once; the answers are recorded and every JEV contestant
/// replays them, free. Three ways to ask (the owner's ideas):
/// <list type="bullet">
/// <item><c>doors</c>: one for all, before the search: the turn and the conversation, and one yes/no question per door
/// ("does the reply need this type of memory?"), in one call.</item>
/// <item><c>gates</c>: a gate at each door, after the search: one call per door, with the door's best memories for the
/// turn ("should these go in front of the assistant?").</item>
/// <item><c>items</c>: a gate per memory: one call per door, one question per memory found.</item>
/// </list>
/// The three-stage protocol: <c>--dry-run</c> prints a real request and the count of calls and spends nothing;
/// <c>--limit 1</c> asks one turn (the real cost per call); then every turn not yet answered. A failed turn is asked again
/// on the next run; an answered one never is.
/// </summary>
public sealed class JevAsk(TextWriter output)
{
    /// <summary>The memories a gate saw per door in round 3: the best by score, as the wide search found them.</summary>
    public const int ShownPerDoor = 6;

    public async Task<int> RunAsync(ArenaData data, string mode, string outPath, bool dryRun, int? limit, int workers, string model,
        JevAskOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new JevAskOptions();
        if (mode is not ("doors" or "gates" or "items"))
        {
            output.WriteLine("error: ask-jev: --mode doors | gates | items");
            return 1;
        }
        var cache = File.Exists(outPath) ? JsonNode.Parse(await File.ReadAllTextAsync(outPath, cancellationToken).ConfigureAwait(false))!.AsObject() : new JsonObject
        {
            ["format"] = "jev-answers/1", ["mode"] = mode, ["model"] = model, ["endpoint"] = new Uri(options.Endpoint).Host,
            ["matrix"] = data.Matrix.Sha256, ["recording"] = data.RecordingSha256, ["shownPerDoor"] = options.Shown,
            ["context"] = options.Context, ["examples"] = options.Examples, ["single"] = options.Single, ["answers"] = new JsonObject(),
        };
        if ((string?)cache["mode"] != mode)
        {
            output.WriteLine($"error: ask-jev: {outPath} holds '{cache["mode"]}' answers, not '{mode}'");
            return 1;
        }
        var answers = cache["answers"]!.AsObject();
        var todo = data.Matrix.InSplit(options.Split).Where(i => answers[i.Id]?["yes"] is null).ToList();
        if (limit is { } n) todo = [.. todo.Take(n)];
        var calls = todo.Select(i => Calls(i, data, mode, options)).ToList();
        var key = options.KeyVariable is null ? "" : Environment.GetEnvironmentVariable(options.KeyVariable);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("agentmemory-router-arena/1");
        var client = new JevClient(http, new Uri(options.Endpoint), key ?? "", model);

        if (dryRun)
        {
            var sample = calls.SelectMany(c => c).FirstOrDefault(c => c.Questions.Count > 1) ?? calls.SelectMany(c => c).FirstOrDefault();
            if (sample is not null)
                output.WriteLine(JsonNode.Parse(client.Render(sample.State, sample.Questions))!.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            if (options.DumpRequests is { } dump)
                await File.WriteAllLinesAsync(dump, todo.SelectMany((item, i) => calls[i].Select(x =>
                    new JsonObject { ["turn"] = item.Id, ["keys"] = JsonSerializer.SerializeToNode(x.Keys), ["body"] = JsonNode.Parse(client.Render(x.State, x.Questions)) }
                        .ToJsonString())), cancellationToken).ConfigureAwait(false);
            output.WriteLine($"ask-jev {mode}: {todo.Count} turns to ask, {calls.Sum(c => c.Count)} calls, {calls.Sum(c => c.Sum(x => x.Questions.Count))} questions; "
                + $"about {calls.Sum(c => c.Sum(x => client.Render(x.State, x.Questions).Length)) / 4:N0} tokens in; "
                + $"key present: {key is not null}. Nothing was sent.");
            return 0;
        }
        if (key is null)
        {
            output.WriteLine($"error: ask-jev: {options.KeyVariable} is not set");
            return 1;
        }
        output.WriteLine($"ask-jev {mode}: {todo.Count} turns, {calls.Sum(c => c.Count)} calls to {new Uri(options.Endpoint).Host} ({model})");
        var gate = new SemaphoreSlim(workers);
        var done = 0;
        var save = new object();
        await Task.WhenAll(todo.Select(async (item, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var yes = new JsonObject();
                double cost = 0, seconds = 0, slowest = 0;
                string? answeredBy = null;
                foreach (var call in calls[index])
                {
                    var reply = await client.AskAsync(call.State, call.Questions, cancellationToken).ConfigureAwait(false);
                    foreach (var (id, p) in reply.Yes) yes[call.Keys[id]] = Math.Round(p, 4);
                    cost += reply.Cost ?? 0;
                    seconds += reply.Seconds;
                    slowest = Math.Max(slowest, reply.Seconds);
                    answeredBy = reply.Model;
                }
                lock (save) answers[item.Id] = new JsonObject
                {
                    // A router asks a turn's calls at once: the turn waits for the slowest.
                    ["yes"] = yes, ["calls"] = calls[index].Count, ["cost"] = cost, ["seconds"] = Math.Round(slowest, 3),
                    ["secondsAll"] = Math.Round(seconds, 3), ["model"] = answeredBy,
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                lock (save) answers[item.Id] = new JsonObject { ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
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
        var times = answered.Select(a => (double)a!["secondsAll"]! / Math.Max(1, (int)a["calls"]!)).Order().ToList();
        var turnTimes = answered.Select(a => (double)a!["seconds"]!).Order().ToList();
        output.WriteLine($"ask-jev {mode}: {answered.Count} turns answered, {failed} failed; cost {answered.Sum(a => (double)a!["cost"]!):0.#####} USD reported; "
            + $"median {(times.Count > 0 ? times[times.Count / 2] : 0):0.###} s a call, {(turnTimes.Count > 0 ? turnTimes[turnTimes.Count / 2] : 0):0.###} s a turn (slowest call); "
            + $"answered by {string.Join(", ", answered.Select(a => (string?)a!["model"]).Distinct())}");
        return failed > 0 ? 2 : 0;
    }

    /// <summary>One call: its state, its questions, and the answer key each question's id is recorded under.</summary>
    public sealed record Call(object State, IReadOnlyDictionary<string, YesNo> Questions, IReadOnlyDictionary<string, string> Keys);

    public static IReadOnlyList<Call> Calls(MatrixItem item, ArenaData data, string mode, JevAskOptions? options = null)
    {
        options ??= new JevAskOptions();
        var conversation = item.Prior.Select(p => new { p.Role, p.Text }).ToList();
        var today = options.Context ? data.Recording.AskedAt.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture) : null;
        if (mode == "doors")
        {
            var questions = Doors.All.ToDictionary(Doors.Name, d => new YesNo(
                $"Before replying to the user's turn, should the assistant open its long-term memory of this type: {Doors.Meaning(d)}?",
                "Yes: the reply needs this type of memory to be right, personal or complete.",
                "No: the reply does not need this type of memory."), StringComparer.Ordinal);
            object state = today is null ? new { conversation, turn = item.Text } : new { today, conversation, turn = item.Text };
            return [new Call(state, questions, questions.Keys.ToDictionary(k => k, k => k))];
        }
        var context = new TurnContext(data.Records[item.Id], data);
        var neighbours = options.Examples > 0 ? context.Neighbours(item, options.Examples, rewrite: false) : [];
        var calls = new List<Call>();
        foreach (var door in Doors.All)
        {
            var shown = Shown(door, context, options.Shown, options.Context);
            if (shown.Count == 0) continue;
            var examples = neighbours.Select(n => new
            {
                turn = n.Example.Text,
                neededThisType = Referee.DoorsNeeded(n.Example, data, data.Matrix.DoorsLabelled).Contains(door),
                memoriesItNeeded = n.Example.Needs.SelectMany(g => g.Take(1)).Take(4).Select(data.World.RefOf).ToList(),
            }).ToList();
            if (mode == "gates")
            {
                var state = new { today, conversation, turn = item.Text, memoryType = Doors.Name(door), meaning = Doors.Meaning(door), memories = shown.Select(s => s.Text).ToList(), examples = examples.Count > 0 ? examples : null };
                calls.Add(new Call(state, new Dictionary<string, YesNo>
                {
                    ["gate"] = new(
                        "These memories were found for the user's turn. Should they be put in front of the assistant before it replies?",
                        "Yes: at least one of them makes the reply right, personal or complete.",
                        "No: none of them is needed; they would be noise."),
                }, new Dictionary<string, string> { ["gate"] = Doors.Name(door) }));
                continue;
            }
            // Each memory once, in the state under its key; each question names its key.
            var memories = shown.Select((s, i) => (Key: $"m{i + 1}", s.Id, s.Text)).ToList();
            var itemState = new
            {
                today, conversation, turn = item.Text, memoryType = Doors.Name(door), meaning = Doors.Meaning(door),
                memories = memories.ToDictionary(m => m.Key, m => m.Text), examples = examples.Count > 0 ? examples : null,
            };
            YesNo Question(string key) => new(
                $"Should memory {key} be put in front of the assistant before it replies to the user's turn?",
                "Yes: it makes the reply right, personal or complete.", "No: the reply does not need it; it would be noise.");
            if (options.Single)
                foreach (var m in memories)
                    calls.Add(new Call(itemState, new Dictionary<string, YesNo> { [m.Key] = Question(m.Key) }, new Dictionary<string, string> { [m.Key] = $"{Doors.Name(door)}|{m.Id}" }));
            else
                calls.Add(new Call(itemState, memories.ToDictionary(m => m.Key, m => Question(m.Key)), memories.ToDictionary(m => m.Key, m => $"{Doors.Name(door)}|{m.Id}")));
        }
        return calls;
    }

    /// <summary>
    /// What a door's gate is shown for the turn: a searching door's best <paramref name="count"/> memories as the wide search
    /// found them; a reading door's memories as it reads them; the working door's profile and last messages. With
    /// <paramref name="context"/>, each memory says when it holds and what it is (due, faded, computed, how a task ended).
    /// </summary>
    public static IReadOnlyList<(string Id, string Text)> Shown(Door door, TurnContext context, int count = ShownPerDoor, bool contextual = false)
    {
        if (door == Door.Working)
        {
            var recent = context.Record.Today.RecentMessages.Select((m, i) => ($"W{i + 1}", m.Key)).ToList();
            var profile = context.Record.Today.WorkingMemory;
            return profile is null ? recent : [("W0", profile), .. recent];
        }
        var candidates = Doors.Reading.Contains(door)
            ? context.Reading(door)
            : context.Source(false, wide: true).Where(c => c.Door == door).OrderByDescending(c => c.Score ?? 0).ToList();
        return [.. candidates.DistinctBy(c => c.Id).Take(count).Select(c => (c.Id, contextual ? Described(c, context.Data.World) : TextOf(c, context.Data.World)))];
    }

    /// <summary>A memory's text: the world's, or for one the world does not hold, what recall returned.</summary>
    public static string TextOf(Candidate candidate, WorldIndex world) =>
        candidate.Id.StartsWith('?') || candidate.Id.StartsWith("X?", StringComparison.Ordinal)
            ? candidate.Id[(candidate.Id.IndexOf(':') + 1)..]
            : world.RefOf(candidate.Id);

    /// <summary>A memory with what a recall knows about it beside its text: its dates, and what kind of thing it is.</summary>
    public static string Described(Candidate candidate, WorldIndex world)
    {
        var text = TextOf(candidate, world);
        var note = world.NoteOf(candidate.Id);
        var parts = new List<string>();
        string Day(string iso) => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : iso;
        string? Field(string name) => note.Split(", ").FirstOrDefault(p => p.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..];
        if (Field("occurredOn") is { } on) parts.Add($"on {Day(on)}");
        else if (Field("validFrom") is { } from)
            parts.Add(Field("validUntil") is { } until ? $"from {Day(from)} until {Day(until)}" : $"since {Day(from)}");
        if (candidate.Section == RecallSection.Due) parts.Add("due now");
        if (candidate.Section == RecallSection.Expiring) parts.Add("expires soon");
        if (candidate.Door == Door.Forgetting) parts.Add("faded from memory");
        if (world.DoorOf(candidate.Id) == Door.Derived) parts.Add("computed from what was said");
        if (candidate.Section == RecallSection.Traces && note.Length > 0) parts.Add($"{note}");
        return parts.Count == 0 ? text : $"{text} ({string.Join("; ", parts)})";
    }
}
