using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using AgentMemory.Inference;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// HaluMem (arXiv 2511.03506; strategy/memoryrouter/halumem-plan.md): one person's sessions, in order, through the library as
/// the Recommended preset ships it (the store-aware writer with the update judge; recall by the gate on JEV; dreaming off),
/// each turn at its own time, one owner for the person. After each session it records what the store created or closed
/// during it (the session's extracted memories), and what recall returns for each of the session's update points (the
/// first 10 kept) and questions (the first 20 kept). Nothing here answers or scores: the benchmark's own prompts do that,
/// outside this repository.
/// </summary>
public sealed class HaluMemSessions(TextWriter output)
{
    private const int UpdateTopK = 10;
    private const int QuestionTopK = 20;
    private const int ContextMessages = 4;

    private static readonly string[] DuringCypher =
    [
        "MATCH (x) WHERE x.owner_id = $owner AND (x:Fact OR x:Entity OR x:Preference) AND ((x.created_at >= datetime($from) AND x.created_at < datetime($to)) OR (x.invalidated_at >= datetime($from) AND x.invalidated_at < datetime($to))) RETURN labels(x) AS kind, properties(x) AS p, null AS s, null AS t",
        "MATCH (s:Entity)-[x]->(t:Entity) WHERE x.owner_id = $owner AND ((x.created_at >= datetime($from) AND x.created_at < datetime($to)) OR (x.invalidated_at >= datetime($from) AND x.invalidated_at < datetime($to))) RETURN [type(x)] AS kind, properties(x) AS p, s.name AS s, t.name AS t",
    ];

    private const string CreatedCypher = "UNWIND $ids AS id MATCH (x {id: id}) RETURN x.id AS id, toString(x.created_at) AS at";

    internal static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.ParseExact(text, "MMM dd, yyyy, HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public async Task<int> RunAsync(string dataPath, int person, int firstSession, int lastSession, string outPath, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var line = File.ReadLines(dataPath).Where(l => l.Trim().Length > 0).Skip(person - 1).FirstOrDefault();
        if (line is null)
        {
            output.WriteLine($"error: halumem: {dataPath} has no person {person}");
            return 1;
        }
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var uuid = root.GetProperty("uuid").GetString()!;
        var allSessions = root.GetProperty("sessions").EnumerateArray().ToList();
        var sessions = allSessions.Skip(firstSession - 1).Take(Math.Max(0, lastSession - firstSession + 1)).ToList();
        var userTurns = sessions.Sum(s => s.GetProperty("dialogue").EnumerateArray().Count(d => d.GetProperty("role").GetString() == "user"));
        var resolution = InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } settings)
        {
            output.WriteLine("error: halumem: no inference provider configured (AI_INFERENCE_PROVIDER, BITDEER_API_KEY)");
            return 1;
        }
        if (!InferenceClientFactory.TryCreateChatClient(settings, "extraction", out var chat, out var why))
        {
            output.WriteLine($"error: halumem: {why}");
            return 1;
        }
        output.WriteLine($"halumem: person {person} ({uuid}), sessions {firstSession}-{firstSession + sessions.Count - 1} of {allSessions.Count}: "
            + $"{userTurns} user turns, {sessions.Sum(s => s.TryGetProperty("questions", out var q) ? q.GetArrayLength() : 0)} questions; "
            + $"written by {settings.Provider} ({settings.Model}), the Recommended preset, the gate on JEV");
        if (dryRun)
        {
            output.WriteLine("halumem: dry run: nothing was sent.");
            return 0;
        }

        using var ollama = new OllamaEmbeddingGenerator("http://127.0.0.1:11434", "bge-m3");
        ollama.Dimensions = (await ollama.GenerateAsync(["ping"], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.Length;
        var embeddings = new CachingEmbeddingGenerator(ollama);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using var container = new Neo4jBuilder(Recorder.Image)
            .WithEnvironment("NEO4J_AUTH", $"neo4j/{password}")
            .WithEnvironment("NEO4J_server_memory_heap_initial__size", "256m")
            .WithEnvironment("NEO4J_server_memory_heap_max__size", "768m")
            .WithEnvironment("NEO4J_server_memory_pagecache_size", "128m")
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

        var first = ParseTime(sessions[0].GetProperty("start_time").GetString()!);
        var clock = new Clock(first);
        var services = new ServiceCollection();
        services.AddLogging(ArenaLogging.Console);
        services.AddSingleton<IClock>(clock);
#pragma warning disable AMREC001, AMGATE001
        services.AddNeo4jAgentMemory(MemoryOptions.CreateRecommended(), Neo4j, o => o.ApplyRecommended());
        var usage = new UsageCountingChatClient(chat);
        services.AddSingleton<IChatClient>(usage);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings);
        services.AddAgentMemoryGate(g =>
        {
            g.ApplyRecommended();
            g.Judges.Add(new SystemOneEndpoint { Name = "jev", Endpoint = new Uri("https://api.typesafe.ai/v1/systemone"), KeyVariable = "TYPESAFE_API_KEY" });
        });
#pragma warning restore AMREC001, AMGATE001
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(cancellationToken).ConfigureAwait(false);
        var tx = provider.GetRequiredService<INeo4jTransactionRunner>();
        await tx.WriteAsync(async runner => await runner.RunAsync("CALL db.awaitIndexes(60)").ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        var ownerId = $"halumem-p{person:00}";
        var results = new List<object>();
        for (var si = 0; si < sessions.Count; si++)
        {
            var session = sessions[si];
            var number = firstSession + si;
            var sessionId = $"{ownerId}-s{number:000}";
            var start = ParseTime(session.GetProperty("start_time").GetString()!);
            var dialogue = session.GetProperty("dialogue").EnumerateArray().ToList();
            var (calls0, input0, output0) = usage.Snapshot();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var failed = new List<string>();
            var said = new List<Message>();
            clock.Now = start;
            using (var scope = provider.CreateScope())
            {
                var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
                await shortTerm.AddConversationAsync(sessionId, sessionId, userId: ownerId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            for (var k = 0; k < dialogue.Count; k++)
            {
                var d = dialogue[k];
                var role = d.GetProperty("role").GetString()!;
                // Each message at its own instant, a second apart within a session that gives one time for all of them.
                var at = d.TryGetProperty("timestamp", out var ts) && ts.GetString() is { } t ? ParseTime(t) : start;
                clock.Now = at > clock.Now ? at : clock.Now.AddSeconds(1);
                using var scope = provider.CreateScope();
                var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
                var message = await shortTerm.AddMessageAsync(new Message
                {
                    MessageId = $"{sessionId}-m{k:000}", ConversationId = sessionId, SessionId = sessionId, Role = role,
                    Content = d.GetProperty("content").GetString() ?? string.Empty, TimestampUtc = clock.Now,
                }, cancellationToken).ConfigureAwait(false);
                if (role == "user")
                {
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>().ExtractAsync(new ExtractionRequest
                        {
                            SessionId = sessionId, UserId = ownerId, Messages = [message],
                            ContextMessages = said.Skip(Math.Max(0, said.Count - ContextMessages)).ToList(),
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed.Add($"{message.MessageId}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                said.Add(message);
            }
            var end = clock.Now.AddSeconds(1);
            var extracted = await StoreSessions.QueryAsync(tx, DuringCypher, new Dictionary<string, object?>
            {
                ["owner"] = ownerId, ["from"] = start.ToString("O"), ["to"] = end.ToString("O"),
            }, cancellationToken).ConfigureAwait(false);
            var (calls1, input1, output1) = usage.Snapshot();
            watch.Stop();

            // The update points and the questions are asked after the session, as the benchmark does.
            var updates = new List<object>();
            foreach (var point in session.TryGetProperty("memory_points", out var mps) ? mps.EnumerateArray() : default)
            {
                if (point.GetProperty("is_update").GetString() != "True" || point.GetProperty("original_memories").GetArrayLength() == 0) continue;
                var text = point.GetProperty("memory_content").GetString()!;
                updates.Add(new { index = point.GetProperty("index").GetInt32(), memory = text, recalled = await RecallAsync(provider, tx, sessionId, ownerId, text, UpdateTopK, cancellationToken).ConfigureAwait(false) });
            }
            var questions = new List<object>();
            foreach (var q in session.TryGetProperty("questions", out var qs) ? qs.EnumerateArray() : default)
            {
                var text = q.GetProperty("question").GetString()!;
                questions.Add(new { question = text, recalled = await RecallAsync(provider, tx, sessionId, ownerId, text, QuestionTopK, cancellationToken).ConfigureAwait(false) });
            }
            results.Add(new
            {
                session = number, start, end, userTurns = dialogue.Count(d => d.GetProperty("role").GetString() == "user"), failed, extracted, updates, questions,
                cost = new { calls = calls1 - calls0, inputTokens = input1 - input0, outputTokens = output1 - output0, ms = watch.ElapsedMilliseconds },
            });
            output.WriteLine($"  session {number}: {dialogue.Count} messages, {extracted.Count} memories created or closed, {updates.Count} update points and "
                + $"{questions.Count} questions recalled, {failed.Count} turn(s) failed, {watch.Elapsed.TotalSeconds:0} s");
            await RunFile.WriteAsync(outPath, JsonSerializer.Serialize(new
            {
                format = "halumem-sessions/1", data = Path.GetFileName(dataPath), person, uuid, owner = ownerId, model = settings.Model, sessions = results,
            }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        }
        output.WriteLine($"halumem: {results.Count} sessions to {outPath}");
        return 0;
    }

    /// <summary>What recall puts in the prompt for a query: the gate's kept items in its order, the first <paramref name="top"/>, each with when it was stored.</summary>
    private static async Task<object> RecallAsync(IServiceProvider provider, INeo4jTransactionRunner tx, string sessionId, string ownerId, string query, int top,
        CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        try
        {
            var context = await scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>().AssembleContextAsync(
                new RecallRequest { SessionId = sessionId, UserId = ownerId, Query = query }, cancellationToken).ConfigureAwait(false);
#pragma warning disable AMGATE001
            if (!context.Metadata.TryGetValue(MemoryGateTrace.MetadataKey, out var traced) || traced is not MemoryGateTrace trace)
                return new { outcome = "no-trace", items = Array.Empty<object>() };
            var kept = trace.Items.Where(x => x.Kept).Take(top).ToList();
            var created = await tx.ReadAsync(async runner =>
            {
                var cursor = await runner.RunAsync(CreatedCypher, new { ids = kept.Select(x => x.ItemId).ToArray() }).ConfigureAwait(false);
                return (await cursor.ToListAsync().ConfigureAwait(false)).ToDictionary(r => r["id"].As<string>(), r => r["at"].As<string?>());
            }, cancellationToken).ConfigureAwait(false);
            return new
            {
                outcome = trace.Outcome, reason = trace.FallbackReason, offered = trace.Offered, keptCount = trace.Kept,
                items = kept.Select(x => new { id = x.ItemId, type = x.MemoryType, text = x.Text, p = x.Probability, at = created.GetValueOrDefault(x.ItemId) }),
            };
#pragma warning restore AMGATE001
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { outcome = "error", reason = $"{ex.GetType().Name}: {ex.Message}", items = Array.Empty<object>() };
        }
    }

    private sealed class Clock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset Now { get; set; } = start;

        public DateTimeOffset UtcNow => Now;
    }
}
