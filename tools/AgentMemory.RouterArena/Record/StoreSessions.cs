using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using AgentMemory.Inference;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// <c>store-sessions</c> (world3-design.md, "through the library"): world 3 on one store. The seed pack is loaded once (the pack
/// plays the model), then every turn, in order, goes through the library's real write path (model extraction, the configured
/// provider) with the clock at its session's date, so each turn meets the store the library itself made: its earlier
/// mistakes included. Per turn: what it created or closed at its instant. Per session: the owner's whole live store, for the
/// end-state measures (stale values, duplicates, drift). Scoring is <c>storage_w3_library.py</c>.
/// </summary>
/// <remarks>Forms: <c>today</c> (the library as configured by the pack), <c>update-judge</c> (the same, with the 1.9 update
/// judge on the write path: JEV, P ≥ 0.65) and <c>update-judge-dream</c> (D1: the same, plus a dream pass after every session:
/// live facts, or live preferences, at cosine ≥ 0.85 go to the update judge, older as the stored one, newer as the new one,
/// and the older is closed at P ≥ 0.65, non-destructively). Round 1's write gate was never in the library, so it cannot run
/// here.</remarks>
public sealed class StoreSessions(TextWriter output)
{
    public sealed record Turn(
        [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("session")] int Session,
        [property: JsonPropertyName("date")] string Date, [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("prior")] IReadOnlyList<PriorTurn> Prior);

    public sealed record PriorTurn([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("text")] string Text);

    private sealed record SetFile([property: JsonPropertyName("items")] IReadOnlyList<Turn> Items);

    /// <summary>A world's turns in their order (both subsets, sorted by id: sNN:TT); <paramref name="set"/> names the files
    /// (<c>w3</c>: w3-stores.json and w3-none.json; <c>w4</c> for world 4, round 3's test).</summary>
    public static IReadOnlyList<Turn> ReadTurns(string setsDirectory, string set = "w3") =>
        [.. new[] { $"{set}-stores.json", $"{set}-none.json" }
            .SelectMany(f => JsonSerializer.Deserialize<SetFile>(File.ReadAllText(Path.Combine(setsDirectory, f)))!.Items)
            .OrderBy(t => t.Id, StringComparer.Ordinal)];

    /// <summary>A turn's instant: its session's evening, one minute per turn, so each turn's writes are its own.</summary>
    public static DateTimeOffset InstantOf(Turn turn) =>
        DateTimeOffset.Parse($"{turn.Date}T19:00:00Z", System.Globalization.CultureInfo.InvariantCulture)
            .AddMinutes(int.Parse(turn.Id[^2..], System.Globalization.CultureInfo.InvariantCulture));

    public async Task<int> RunAsync(string packPath, string setsDirectory, string form, string outPath, bool dryRun, int? limit,
        CancellationToken cancellationToken = default, string set = "w3", string? ownerName = null)
    {
        // writer: the library with the store-aware writer (AMWRITE001, storage round 3's F ported) and the update judge
        // confirming its closings; the turn's earlier messages are its context, as the harness gave them.
        if (form is not ("today" or "update-judge" or "update-judge-dream" or "writer"))
        {
            output.WriteLine($"error: store-sessions: --form must be today, update-judge, update-judge-dream or writer (was {form})");
            return 1;
        }
        var pack = ValidationPackReader.ReadFile(packPath);
        var problems = ValidationPackReader.Check(pack);
        if (problems.Count > 0)
        {
            output.WriteLine($"error: store-sessions: the pack has {problems.Count} problem(s): {string.Join("; ", problems.Take(5))}");
            return 1;
        }
        var turns = ReadTurns(setsDirectory, set).Take(limit ?? int.MaxValue).ToList();
        var resolution = InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } settings)
        {
            output.WriteLine("error: store-sessions: no inference provider configured (AI_INFERENCE_PROVIDER, BITDEER_API_KEY)");
            return 1;
        }
        if (!InferenceClientFactory.TryCreateChatClient(settings, "extraction", out var chat, out var why))
        {
            output.WriteLine($"error: store-sessions: {why}");
            return 1;
        }
        var owner = pack.Owners[0];
        output.WriteLine($"store-sessions: form {form}; {turns.Count} turns in {turns.Select(t => t.Session).Distinct().Count()} sessions "
            + $"({turns[0].Date} to {turns[^1].Date}) on one store seeded by {pack.Id} ({pack.Sessions.Sum(s => s.Messages.Count)} messages); "
            + $"extraction by {settings.Provider} ({settings.Model}); owner {owner}");
        var dream = form == "update-judge-dream";
        if (dryRun)
        {
            output.WriteLine($"store-sessions: dry run: one pack load, about {turns.Count} extraction calls"
                + (form != "today" ? " and an update-judge call for each write that has a similar stored memory" : "")
                + (dream ? "; after each session, a dream pass over live pairs at cosine >= 0.85" : "")
                + $"; the first turn would be written at {InstantOf(turns[0]):O}: \"{turns[0].Text}\". Nothing was sent.");
            return 0;
        }

        var request = new RecordRequest(packPath, "", outPath, []);
        using var ollama = new OllamaEmbeddingGenerator(request.Ollama.TrimEnd('/'), request.Model);
        ollama.Dimensions = (await ollama.GenerateAsync(["ping"], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.Length;
        var embeddings = new CachingEmbeddingGenerator(ollama);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using var container = new Neo4jBuilder(Recorder.Image)
            .WithEnvironment("NEO4J_AUTH", $"neo4j/{password}")
            // Lean, as the sample-run gate runs it: world 3's store is a few thousand memories, and the machine is shared
            // (two runs at 768m/128m were stopped for low memory, 10-05).
            .WithEnvironment("NEO4J_server_memory_heap_initial__size", "256m")
            .WithEnvironment("NEO4J_server_memory_heap_max__size", "512m")
            .WithEnvironment("NEO4J_server_memory_pagecache_size", "64m")
            .Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        void Neo4j(Neo4jOptions o)
        {
            o.Uri = container.GetConnectionString();
            o.Username = "neo4j";
            o.Password = password;
            o.Database = "neo4j";
            o.EmbeddingDimensions = ollama.Dimensions ?? 1024;
        }
        var runner = new ValidationPackRunner(Neo4j, _ => embeddings, ArenaLogging.Console);
        await using var store = await runner.LoadAsync(pack, set, cancellationToken).ConfigureAwait(false);
        var clock = new MovingClock(InstantOf(turns[0]));
        var services = new ServiceCollection();
        services.AddLogging(ArenaLogging.Console);
        services.AddSingleton<IClock>(clock);
        // A measurement sees every writer failure: the turn stores nothing and its error is recorded, never handed to the
        // extractors (the library's default since 2026-10-08), so a run with a failed turn is visible and is run again.
#pragma warning disable AMWRITE001
        store.Options.Extraction.FallBackToExtractorsWhenWriterFails = false;
#pragma warning restore AMWRITE001
        services.AddNeo4jAgentMemory(store.Options, Neo4j, form == "writer" ? o => o.UseMemoryWriter = true : _ => { });
        var usage = new UsageCountingChatClient(chat);
        services.AddSingleton<IChatClient>(usage);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings);
        if (form is "update-judge" or "update-judge-dream" or "writer")
        {
#pragma warning disable AMGATE001
            services.AddAgentMemoryGate(g =>
            {
                g.Mode = MemoryGateMode.Floor;      // recall untouched: only the write path is measured
                g.UpdateJudge = true;
                g.Judges.Add(new SystemOneEndpoint
                {
                    Name = "jev", Endpoint = new Uri("https://api.typesafe.ai/v1/systemone"), KeyVariable = "TYPESAFE_API_KEY",
                });
            });
#pragma warning restore AMGATE001
        }
        await using var provider = services.BuildServiceProvider();
        var ownerId = store.Owner(owner);
        var tx = provider.GetRequiredService<INeo4jTransactionRunner>();
        if (!string.IsNullOrWhiteSpace(ownerName))
        {
            // --owner-name: the owner has said their name before the first turn ("user | is named | Lukas", written at the seed's
            // instant, a day before turn 1): what the library learns from such a sentence, and what the harness's prompts were
            // given. It is not one of any turn's writes; it is one more live memory in every session's end state.
            using var seedScope = provider.CreateScope();
            await seedScope.ServiceProvider.GetRequiredService<IFactRepository>().UpsertAsync(new Fact
            {
                FactId = $"owner-name-{ownerId}", Subject = "user", Predicate = "is named", Object = ownerName.Trim(), Confidence = 1.0,
                OwnerId = ownerId, CreatedAtUtc = InstantOf(turns[0]).AddDays(-1),
            }, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"store-sessions: the owner's name is known before turn 1: {ownerName.Trim()}");
        }
        var results = new List<object>();
        var sessions = new List<object>();
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            var at = InstantOf(turn);
            clock.Now = at;
            var sessionId = $"{set}-{owner}-s{turn.Session:00}";
            string? error = null;
            var (calls0, input0, output0) = usage.Snapshot();
            var (prompt0, reply0) = usage.Characters();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var scope = provider.CreateScope())
            {
                var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
                var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
                try
                {
                    var added = new List<Message>();
                    var k = 0;
                    foreach (var (role, text) in turn.Prior.Select(p => (p.Role, p.Text)).Append(("user", turn.Text)))
                        added.Add(await shortTerm.AddMessageAsync(new Message
                        {
                            MessageId = $"{sessionId}-t{turn.Id[^2..]}-{k++}", ConversationId = sessionId, SessionId = sessionId, Role = role,
                            Content = text, TimestampUtc = at,
                        }, cancellationToken).ConfigureAwait(false));
                    // The writer reads the turn's earlier messages as context (the harness's CONVERSATION); the other forms
                    // extract from the last message alone, as they always did.
                    await pipeline.ExtractAsync(new ExtractionRequest
                    {
                        SessionId = sessionId, UserId = ownerId, Messages = [added[^1]],
                        ContextMessages = form == "writer" ? added[..^1] : [],
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = $"{ex.GetType().Name}: {ex.Message}";
                }
            }
            watch.Stop();
            var (calls1, input1, output1) = usage.Snapshot();
            var (prompt1, reply1) = usage.Characters();
            // The turn's cost: the chat model's calls and tokens (the update judge is not a chat client: its time is in ms).
            var cost = new
            {
                calls = calls1 - calls0, inputTokens = input1 - input0, outputTokens = output1 - output0,
                promptChars = prompt1 - prompt0, replyChars = reply1 - reply0, ms = watch.ElapsedMilliseconds,
            };
            var written = await AtAsync(tx, ownerId, at, cancellationToken).ConfigureAwait(false);
            results.Add(new { turn = turn.Id, session = turn.Session, at, text = turn.Text, written, error, cost });
            output.WriteLine($"  {i + 1}/{turns.Count} {turn.Id}: {written.Count} written or closed{(error is null ? "" : $" ({error[..Math.Min(80, error.Length)]})")}");
            var lastOfSession = i == turns.Count - 1 || turns[i + 1].Session != turn.Session;
            if (lastOfSession)
            {
                List<object> dreamt = [];
                List<string> dreamErrors = [];
                if (dream)
                {
                    clock.Now = at.AddMinutes(30);         // the pass runs after the session, at its own instant
                    try
                    {
                        var pass = await DreamAsync(provider, tx, ownerId, clock.Now, cancellationToken).ConfigureAwait(false);
                        dreamt = pass.Closed;
                        dreamErrors = pass.Errors;
                        output.WriteLine($"  session {turn.Session}: the dream pass read {pass.Live} live facts and preferences, found {pass.Pairs} pairs at "
                            + $"cosine >= 0.85, asked the judge about them (highest P {pass.Highest:0.00}) and closed {dreamt.Count}"
                            + (dreamErrors.Count > 0 ? $"; {dreamErrors.Count} batch(es) failed: {dreamErrors[0]}" : ""));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A failed pass is recorded and the run goes on: the session's end state is then what the write path left.
                        dreamErrors = [$"{ex.GetType().Name}: {ex.Message}"];
                        output.WriteLine($"  session {turn.Session}: the dream pass FAILED ({dreamErrors[0]}); the run goes on");
                    }
                }
                var live = await LiveAsync(tx, ownerId, cancellationToken).ConfigureAwait(false);
                sessions.Add(new { session = turn.Session, date = turn.Date, live, dreamt, dreamErrors });
                output.WriteLine($"  session {turn.Session} ({turn.Date}): {live.Count} live memories");
            }
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new
            {
                format = "store-sessions/1", form, model = settings.Model, pack = pack.Id, owner, results, sessions,
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        }
        output.WriteLine($"store-sessions: {results.Count} turns, {sessions.Count} sessions to {outPath}");
        return 0;
    }

    private static readonly string[] AtCypher =
    [
        "MATCH (x) WHERE x.owner_id = $owner AND (x.created_at = datetime($at) OR x.invalidated_at = datetime($at)) RETURN labels(x) AS kind, properties(x) AS p, null AS s, null AS t",
        "MATCH (s)-[x]->(t) WHERE x.owner_id = $owner AND (x.created_at = datetime($at) OR x.invalidated_at = datetime($at)) RETURN [type(x)] AS kind, properties(x) AS p, s.name AS s, t.name AS t",
    ];

    private static readonly string[] LiveCypher =
    [
        "MATCH (x) WHERE x.owner_id = $owner AND (x:Fact OR x:Entity OR x:Preference) AND x.invalidated_at IS NULL AND x.merged_into IS NULL RETURN labels(x) AS kind, properties(x) AS p, null AS s, null AS t",
        "MATCH (s:Entity)-[x]->(t:Entity) WHERE x.owner_id = $owner AND x.invalidated_at IS NULL RETURN [type(x)] AS kind, properties(x) AS p, s.name AS s, t.name AS t",
    ];

    /// <summary>Every node or relationship of the owner created or closed at the turn's instant, without vectors.</summary>
    private static Task<List<Dictionary<string, object?>>> AtAsync(INeo4jTransactionRunner tx, string ownerId, DateTimeOffset at, CancellationToken ct) =>
        QueryAsync(tx, AtCypher, new Dictionary<string, object?> { ["owner"] = ownerId, ["at"] = at.ToString("O") }, ct);

    /// <summary>The owner's live store: facts, entities and preferences not closed or merged, and live connections.</summary>
    private static Task<List<Dictionary<string, object?>>> LiveAsync(INeo4jTransactionRunner tx, string ownerId, CancellationToken ct) =>
        QueryAsync(tx, LiveCypher, new Dictionary<string, object?> { ["owner"] = ownerId }, ct);

    private static Task<List<Dictionary<string, object?>>> QueryAsync(INeo4jTransactionRunner tx, string[] cyphers,
        Dictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        tx.ReadAsync(async runner =>
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (var cypher in cyphers)
            {
                var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
                foreach (var r in await cursor.ToListAsync().ConfigureAwait(false))
                {
                    var p = r["p"].As<IDictionary<string, object>>()
                        .Where(kv => !kv.Key.Contains("embedding", StringComparison.Ordinal))
                        .ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.ToString());
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = string.Join(",", r["kind"].As<List<object>>()), ["source"] = r["s"].As<string?>(), ["target"] = r["t"].As<string?>(), ["props"] = p,
                    });
                }
            }
            return rows;
        }, cancellationToken);

    private const string DreamCypher =
        "MATCH (x) WHERE x.owner_id = $owner AND (x:Fact OR x:Preference) AND x.invalidated_at IS NULL AND x.embedding IS NOT NULL " +
        "RETURN x.id AS id, x:Fact AS fact, coalesce(x.subject + ' | ' + x.predicate + ' | ' + x.object, x.preference) AS text, " +
        "x.embedding AS embedding, toString(x.created_at) AS at";

    /// <summary>
    /// D1, the dream pass: every pair of live facts, or of live preferences, at cosine >= 0.85 goes to the update judge (the
    /// older as the stored memory, the newer as the new one); at P >= 0.65 the older is closed, as a change, by the newer.
    /// Pairs are taken most similar first, and a memory closed once is not offered again.
    /// </summary>
    private static async Task<(List<object> Closed, int Live, int Pairs, double Highest, List<string> Errors)> DreamAsync(IServiceProvider provider,
        INeo4jTransactionRunner tx, string ownerId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(DreamCypher, new Dictionary<string, object?> { ["owner"] = ownerId }).ConfigureAwait(false);
            return (await cursor.ToListAsync().ConfigureAwait(false)).Select(r => (
                Id: r["id"].As<string>(), Fact: r["fact"].As<bool>(), Text: r["text"].As<string>(),
                Vector: r["embedding"].As<List<double>>().Select(d => (float)d).ToArray(), At: r["at"].As<string>())).ToList();
        }, ct).ConfigureAwait(false);
        var pairs = new List<(double Cos, int Older, int Newer)>();
        for (var i = 0; i < rows.Count; i++)
            for (var j = i + 1; j < rows.Count; j++)
            {
                if (rows[i].Fact != rows[j].Fact) continue;
                var c = Cosine(rows[i].Vector, rows[j].Vector);
                if (c < 0.85) continue;
                var (older, newer) = string.CompareOrdinal(rows[i].At, rows[j].At) <= 0 ? (i, j) : (j, i);
                pairs.Add((c, older, newer));
            }
        pairs.Sort((a, b) => b.Cos.CompareTo(a.Cos));
        if (pairs.Count == 0) return ([], rows.Count, 0, 0, []);
        using var scope = provider.CreateScope();
        var judge = scope.ServiceProvider.GetRequiredService<IMemoryUpdateJudge>();
        // In batches of at most 12 pairs, the size the write path sends: one request does not grow with the store, and a
        // failed batch is recorded and skipped, never the end of the run.
        var verdicts = new Dictionary<string, double>(StringComparer.Ordinal);
        var errors = new List<string>();
        for (var start = 0; start < pairs.Count; start += 12)
        {
            var batch = Enumerable.Range(start, Math.Min(12, pairs.Count - start))
                .Select(k => new MemoryUpdatePair($"d{k}", rows[pairs[k].Newer].Text, rows[pairs[k].Older].Text)).ToList();
            try
            {
                foreach (var (key, value) in await judge.JudgeAsync(new MemoryUpdateRequest(null, now, batch), ct).ConfigureAwait(false))
                    verdicts[key] = value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"pairs {start}-{start + batch.Count - 1}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        var facts = scope.ServiceProvider.GetRequiredService<AgentMemory.Abstractions.Repositories.IFactRepository>();
        var preferences = scope.ServiceProvider.GetRequiredService<AgentMemory.Abstractions.Repositories.IPreferenceRepository>();
        var memoryScope = AgentMemory.Abstractions.Options.MemoryScope.For(ownerId, includeShared: false);
        var closedIds = new HashSet<string>(StringComparer.Ordinal);
        var closed = new List<object>();
        for (var k = 0; k < pairs.Count; k++)
        {
            var (c, older, newer) = pairs[k];
            if (!verdicts.TryGetValue($"d{k}", out var p) || p < 0.65) continue;
            if (closedIds.Contains(rows[older].Id) || closedIds.Contains(rows[newer].Id)) continue;
            var ok = rows[older].Fact
                ? await facts.SupersedeAsync(rows[older].Id, rows[newer].Id, FactClosureReason.Change, now, memoryScope, ct).ConfigureAwait(false)
                : await preferences.SupersedeAsync(rows[older].Id, rows[newer].Id, memoryScope, ct).ConfigureAwait(false);
            if (!ok) continue;
            closedIds.Add(rows[older].Id);
            closed.Add(new { closed = rows[older].Text, by = rows[newer].Text, cosine = Math.Round(c, 3), p = Math.Round(p, 3) });
        }
        return (closed, rows.Count, pairs.Count, verdicts.Count == 0 ? 0 : verdicts.Values.Max(), errors);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    private sealed class MovingClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset Now { get; set; } = start;

        public DateTimeOffset UtcNow => Now;
    }
}
