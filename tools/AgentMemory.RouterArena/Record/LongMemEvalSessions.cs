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
/// LongMemEval (Wu et al., ICLR 2025) one user turn at a time, which the LongMemEval tool cannot do (its preparation
/// extracts whole sessions in batches, so the store-aware writer, which takes one user message, is never reached). Each
/// question is its own owner with its own clock: its sessions (the oracle file holds only the evidence sessions) are played
/// in date order, every user message written as it is said with the four messages before it as context, through the
/// Recommended preset (the writer with the update judge; or, with --writer off, the same preset on the per-turn
/// extractors), and then recall runs at the question's date through the gate. What recall kept (its first 20, each with
/// when it was stored) is recorded; answering and judging are done outside (strategy arena/longmemeval_score.py, with the
/// benchmark's own judge prompts). Several questions run at once; results are written after each question, and a run
/// started again skips the questions already in its file.
/// </summary>
public sealed class LongMemEvalSessions(TextWriter output)
{
    private const int QuestionTopK = 20;
    private const int ContextMessages = 4;

    private const string CreatedCypher = "UNWIND $ids AS id MATCH (x {id: id}) RETURN x.id AS id, toString(x.created_at) AS at";

    /// <summary>"2023/05/20 (Sat) 02:21", the benchmark's dates, read as UTC.</summary>
    internal static DateTimeOffset ParseDate(string text)
    {
        var open = text.IndexOf(" (", StringComparison.Ordinal);
        var close = text.IndexOf(')', StringComparison.Ordinal);
        var plain = open >= 0 && close > open ? text[..open] + text[(close + 1)..] : text;
        return DateTimeOffset.ParseExact(plain.Trim(), "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private sealed record Question(string Id, string Type, string Text, string Answer, DateTimeOffset AskedAt,
        IReadOnlyList<(DateTimeOffset At, string Id, IReadOnlyList<(string Role, string Content)> Messages)> Sessions);

    public async Task<int> RunAsync(string dataPath, IReadOnlyCollection<string> types, int limit, bool writer, int parallel, string outPath, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var questions = Read(dataPath).Where(q => types.Count == 0 || types.Contains(q.Type)).OrderBy(q => q.Type, StringComparer.Ordinal)
            .ThenBy(q => q.Id, StringComparer.Ordinal).Take(limit > 0 ? limit : int.MaxValue).ToList();
        var resolution = InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } settings || !InferenceClientFactory.TryCreateChatClient(settings, "extraction", out var chat, out var why))
        {
            output.WriteLine("error: longmemeval: no inference provider configured (AI_INFERENCE_PROVIDER, BITDEER_API_KEY)");
            return 1;
        }
        var done = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (File.Exists(outPath))
        {
            using var previous = JsonDocument.Parse(await File.ReadAllTextAsync(outPath, cancellationToken).ConfigureAwait(false));
            foreach (var r in previous.RootElement.GetProperty("results").EnumerateArray())
                done[r.GetProperty("questionId").GetString()!] = r.Clone();
        }
        var todo = questions.Where(q => !done.ContainsKey(q.Id)).ToList();
        output.WriteLine($"longmemeval: {questions.Count} questions ({string.Join(", ", questions.GroupBy(q => q.Type).Select(g => $"{g.Key} {g.Count()}"))}), "
            + $"{todo.Count} to run ({done.Count} already in {Path.GetFileName(outPath)}), "
            + $"{todo.Sum(q => q.Sessions.Sum(s => s.Messages.Count(m => m.Role == "user")))} user turns; written by {settings.Provider} ({settings.Model}), "
            + $"the Recommended preset {(writer ? "(the store-aware writer)" : "on the per-turn extractors (--writer off)")}, the gate on JEV, {parallel} at once");
        if (dryRun)
        {
            output.WriteLine("longmemeval: dry run: nothing was sent.");
            return 0;
        }

        using var ollama = new OllamaEmbeddingGenerator("http://127.0.0.1:11434", "bge-m3");
        ollama.Dimensions = (await ollama.GenerateAsync(["ping"], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.Length;
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using var container = new Neo4jBuilder(Recorder.Image)
            .WithEnvironment("NEO4J_AUTH", $"neo4j/{password}")
            .WithEnvironment("NEO4J_server_memory_heap_initial__size", "256m")
            .WithEnvironment("NEO4J_server_memory_heap_max__size", "1g")
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
        var usage = new UsageCountingChatClient(chat);

        ServiceProvider Build(Clock clock)
        {
            var services = new ServiceCollection();
            services.AddLogging(ArenaLogging.Console);
            services.AddSingleton<IClock>(clock);
#pragma warning disable AMREC001, AMGATE001
            services.AddNeo4jAgentMemory(MemoryOptions.CreateRecommended(), Neo4j, o =>
            {
                o.ApplyRecommended();
                o.UseMemoryWriter = writer;
            });
            services.AddSingleton<IChatClient>(usage);
            // An instance, so the container never disposes the shared generator under another question.
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new CachingEmbeddingGenerator(ollama));
            services.AddAgentMemoryGate(g =>
            {
                g.ApplyRecommended();
                g.Judges.Add(new SystemOneEndpoint { Name = "jev", Endpoint = new Uri("https://api.typesafe.ai/v1/systemone"), KeyVariable = "TYPESAFE_API_KEY" });
            });
#pragma warning restore AMREC001, AMGATE001
            return services.BuildServiceProvider();
        }

        await using (var first = Build(new Clock(DateTimeOffset.UnixEpoch)))
        {
            await first.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(cancellationToken).ConfigureAwait(false);
            await first.GetRequiredService<INeo4jTransactionRunner>().WriteAsync(async runner =>
                await runner.RunAsync("CALL db.awaitIndexes(60)").ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        var results = done.Values.Select(v => (object)v).ToList();
        var gate = new object();
        var finished = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = cancellationToken }, async (q, ct) =>
        {
            var ownerId = $"lme-{q.Id}".ToLowerInvariant();
            var clock = new Clock(q.Sessions.Count > 0 ? q.Sessions[0].At : q.AskedAt);
            await using var provider = Build(clock);
            var tx = provider.GetRequiredService<INeo4jTransactionRunner>();
            var failed = new List<string>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var turns = 0;
            for (var si = 0; si < q.Sessions.Count; si++)
            {
                var (at, sourceId, messages) = q.Sessions[si];
                var sessionId = $"{ownerId}-s{si:00}";
                clock.Now = at > clock.Now ? at : clock.Now.AddMinutes(1);
                using (var scope = provider.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>()
                        .AddConversationAsync(sessionId, sessionId, userId: ownerId, cancellationToken: ct).ConfigureAwait(false);
                var said = new List<Message>();
                for (var k = 0; k < messages.Count; k++)
                {
                    var (role, content) = messages[k];
                    // Each message at its own instant, ten seconds apart within its session.
                    clock.Now = clock.Now.AddSeconds(10);
                    using var scope = provider.CreateScope();
                    var message = await scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>().AddMessageAsync(new Message
                    {
                        MessageId = $"{sessionId}-m{k:000}", ConversationId = sessionId, SessionId = sessionId, Role = role, Content = content,
                        TimestampUtc = clock.Now,
                    }, ct).ConfigureAwait(false);
                    if (role == "user")
                    {
                        turns++;
                        try
                        {
                            await scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>().ExtractAsync(new ExtractionRequest
                            {
                                SessionId = sessionId, UserId = ownerId, Messages = [message],
                                ContextMessages = said.Skip(Math.Max(0, said.Count - ContextMessages)).ToList(),
                            }, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed.Add($"{message.MessageId}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    said.Add(message);
                }
            }

            // The question is asked at its own date, in a session of its own.
            clock.Now = q.AskedAt > clock.Now ? q.AskedAt : clock.Now.AddMinutes(1);
            var askId = $"{ownerId}-ask";
            using (var scope = provider.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>()
                    .AddConversationAsync(askId, askId, userId: ownerId, cancellationToken: ct).ConfigureAwait(false);
            var recalled = await RecallAsync(provider, tx, askId, ownerId, q.Text, ct).ConfigureAwait(false);
            watch.Stop();
            var result = new
            {
                questionId = q.Id, type = q.Type, question = q.Text, answer = q.Answer, questionDate = q.AskedAt, sessions = q.Sessions.Count, turns,
                failed, recalled, ms = watch.ElapsedMilliseconds,
            };
            string json;
            lock (gate)
            {
                results.Add(result);
                finished++;
                output.WriteLine($"  {finished}/{todo.Count} {q.Id} ({q.Type}): {turns} turns, {failed.Count} failed, {watch.Elapsed.TotalSeconds:0} s");
                json = JsonSerializer.Serialize(new
                {
                    format = "longmemeval-turns/1", data = Path.GetFileName(dataPath), writer, model = settings.Model, results,
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            await WriteLockedAsync(outPath, json, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var (calls, input, outputTokens) = usage.Snapshot();
        output.WriteLine($"longmemeval: {results.Count} questions to {outPath}; this run {calls} chat calls, {input} in, {outputTokens} out");
        return 0;
    }

    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private static async Task WriteLockedAsync(string path, string json, CancellationToken cancellationToken)
    {
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunFile.WriteAsync(path, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static List<Question> Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<Question>();
        foreach (var q in doc.RootElement.EnumerateArray())
        {
            var dates = q.GetProperty("haystack_dates").EnumerateArray().Select(d => ParseDate(d.GetString()!)).ToList();
            var ids = q.GetProperty("haystack_session_ids").EnumerateArray().Select(d => d.GetString()!).ToList();
            var sessions = q.GetProperty("haystack_sessions").EnumerateArray()
                .Select((s, i) => (At: dates[i], Id: ids[i], Messages: (IReadOnlyList<(string, string)>)s.EnumerateArray()
                    .Select(m => (m.GetProperty("role").GetString()!, m.GetProperty("content").GetString() ?? "")).ToList()))
                .OrderBy(s => s.At).ToList();
            var answer = q.GetProperty("answer");
            list.Add(new Question(q.GetProperty("question_id").GetString()!, q.GetProperty("question_type").GetString()!,
                q.GetProperty("question").GetString()!, answer.ValueKind == JsonValueKind.String ? answer.GetString()! : answer.GetRawText(),
                ParseDate(q.GetProperty("question_date").GetString()!), sessions));
        }
        return list;
    }

    /// <summary>What recall puts in the prompt for the question: the gate's kept items in its order, the first 20, each with when it was stored.</summary>
    private static async Task<object> RecallAsync(IServiceProvider provider, INeo4jTransactionRunner tx, string sessionId, string ownerId, string query,
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
            var kept = trace.Items.Where(x => x.Kept).Take(QuestionTopK).ToList();
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
