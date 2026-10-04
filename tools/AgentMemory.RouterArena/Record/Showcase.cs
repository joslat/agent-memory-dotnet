using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using AgentMemory.Inference;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.RouterArena.Data;
using AgentMemory.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.Neo4j;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// <c>showcase</c> (root PLAN 41.07): a few questions through the library end to end, three ways: today's recall, the gate
/// (the judges at the fan-in) and everything found. One world loaded once into a throwaway Neo4j; for each way a stack over
/// the same store; for each question the recall (timed, the judges' time inside it), the reply model (timed, its tokens
/// as it reports them) and the reply. Scoring the replies is `showcase_grade.py`.
/// </summary>
public sealed class Showcase(TextWriter output)
{
    private static readonly DateTimeOffset AskedAt = DateTimeOffset.Parse("2026-10-03T10:00:00Z", CultureInfo.InvariantCulture);

    public sealed record Options(string WorldPath, IReadOnlyList<(string Level, MatrixItem Item)> Questions, string OutPath,
        string? Laya, string? ExamplesPath, string Owner = "marta", string Session = "marta-now", int Repeats = 2, int Replies = 3);

    public async Task<int> RunAsync(Options o, bool dryRun, CancellationToken cancellationToken = default)
    {
        var resolution = InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } settings || !InferenceClientFactory.TryCreateChatClient(settings, "answer", out var chat, out var why))
        {
            output.WriteLine("error: showcase: no inference provider configured (AI_INFERENCE_PROVIDER, BITDEER_API_KEY)");
            return 1;
        }
        var judges = new List<SystemOneEndpoint>
        {
            new() { Name = "jev", Endpoint = new Uri("https://api.typesafe.ai/v1/systemone"), KeyVariable = "TYPESAFE_API_KEY", Weight = o.Laya is null ? 1.0 : 0.8 },
        };
        if (o.Laya is not null)
            judges.Add(new SystemOneEndpoint { Name = "laya", Endpoint = new Uri(o.Laya), Weight = 0.2 });
        var modes = new[] { MemoryGateMode.Everything, MemoryGateMode.Floor, MemoryGateMode.Judge };
        output.WriteLine($"showcase: {o.Questions.Count} questions x {modes.Length} ways x {o.Repeats} timings; replies by {settings.Provider} ({settings.Model}); "
            + $"judges {string.Join(" + ", judges.Select(j => $"{j.Name} {j.Weight}"))}");
        if (dryRun)
        {
            output.WriteLine($"showcase: dry run: {o.Questions.Count * modes.Length * o.Replies} replies, about {o.Questions.Count * o.Repeats * 11} judge calls. Nothing was sent.");
            return 0;
        }

        var world = ValidationPackReader.ReadFile(o.WorldPath);
        var request = new RecordRequest(o.WorldPath, "", o.OutPath, []);
        using var embeddings = new OllamaEmbeddingGenerator(request.Ollama.TrimEnd('/'), request.Model);
        embeddings.Dimensions = (await embeddings.GenerateAsync(["ping"], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.Length;
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using var container = new Neo4jBuilder(Recorder.Image)
            .WithEnvironment("NEO4J_AUTH", $"neo4j/{password}")
            .WithEnvironment("NEO4J_server_memory_heap_max__size", "768m")
            .WithEnvironment("NEO4J_server_memory_pagecache_size", "128m")
            .Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        void Neo4j(Neo4jOptions n)
        {
            n.Uri = container.GetConnectionString();
            n.Username = "neo4j";
            n.Password = password;
            n.Database = "neo4j";
            n.EmbeddingDimensions = embeddings.Dimensions ?? 1024;
            n.Extensions.Add("arithmetic");
        }
        await using var store = await new ValidationPackRunner(Neo4j, _ => embeddings, ArenaLogging.Console).LoadAsync(world, "sc", cancellationToken).ConfigureAwait(false);

        ServiceProvider Stack(MemoryGateMode mode)
        {
            var services = new ServiceCollection();
            services.AddLogging(ArenaLogging.Console);
            services.AddSingleton<IClock>(new FixedClock(AskedAt));
            services.AddNeo4jAgentMemory(store.Options, Neo4j, _ => { });
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings);
            services.AddSingleton(chat);
            services.AddAgentMemoryGate(g =>
            {
                g.Mode = mode;
                foreach (var j in judges) g.Judges.Add(j);
                g.ExamplesPath = o.ExamplesPath;
            });
            return services.BuildServiceProvider();
        }

        var rows = new List<object>();
        foreach (var mode in modes)
        {
            await using var provider = Stack(mode);
            foreach (var (level, item) in o.Questions)
            {
                var recallMs = new List<double>();
                MemoryContext? context = null;
                for (var r = 0; r <= o.Repeats; r++)   // the first recall warms the store and the judges; the rest are timed
                {
                    using var scope = provider.CreateScope();
                    var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
                    var watch = Stopwatch.StartNew();
                    context = (await memory.RecallAsync(new RecallRequest
                    {
                        SessionId = $"sc-{o.Session}", UserId = store.Owner(o.Owner), Query = item.Text, TemporalReferenceTime = AskedAt,
                        Options = store.Options.Recall,
                    }, cancellationToken).ConfigureAwait(false)).Context;
                    if (r > 0) recallMs.Add(watch.Elapsed.TotalMilliseconds);
                }
                var memories = Lines(context!);
                var prompt = Prompt(item.Text, memories);
                context!.Metadata.TryGetValue("gate.ms", out var judgeMs);
                context.Metadata.TryGetValue("gate", out var gate);
                // Several replies from the same prompt: the reply model's own variance, so one odd reply does not decide.
                var replies = new List<object>();
                for (var k = 0; k < o.Replies; k++)
                {
                    var replyWatch = Stopwatch.StartNew();
                    var response = await chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
                    replies.Add(new
                    {
                        replyMs = Math.Round(replyWatch.Elapsed.TotalMilliseconds, 1),
                        inputTokens = response.Usage?.InputTokenCount, outputTokens = response.Usage?.OutputTokenCount, reply = response.Text,
                    });
                    output.WriteLine($"  {mode,-10} {level,-7} {memories.Count,3} memories, recall {recallMs.Order().ElementAt(recallMs.Count / 2),6:0} ms ({gate}, "
                        + $"judges {judgeMs} ms), reply {replyWatch.Elapsed.TotalMilliseconds,6:0} ms, tokens in {response.Usage?.InputTokenCount} out {response.Usage?.OutputTokenCount}");
                }
                rows.Add(new
                {
                    way = mode.ToString().ToLowerInvariant(), level, turn = item.Id, question = item.Text,
                    gate = gate?.ToString(), memories = memories.Count, memoryLines = memories,
                    recallMs = Math.Round(recallMs.Order().ElementAt(recallMs.Count / 2), 1), judgeMs, replies,
                });
            }
        }
        await File.WriteAllTextAsync(o.OutPath, JsonSerializer.Serialize(new
        {
            format = "showcase/1", askedAt = AskedAt, replyModel = settings.Model, judges = judges.Select(j => new { j.Name, j.Weight }), rows,
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        output.WriteLine($"showcase: {rows.Count} rows to {o.OutPath}");
        return 0;
    }

    /// <summary>The recalled memories as the reply model reads them; the same rendering for every way.</summary>
    internal static List<string> Lines(MemoryContext c)
    {
        var lines = new List<string>();
        string D(DateTimeOffset d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        string Fact(Fact f) => $"{f.Subject} | {f.Predicate} | {f.Object}"
            + (f.OccurredOn is { } on ? $" (on {D(on)})" : f.ValidFrom is { } from ? (f.ValidUntil is { } until ? $" (from {D(from)} until {D(until)})" : $" (since {D(from)})") : "");
        if (!string.IsNullOrWhiteSpace(c.WorkingMemoryBlock)) lines.Add(c.WorkingMemoryBlock.Trim());
        lines.AddRange(c.RelevantFacts.Items.Select(Fact));
        lines.AddRange(c.DueFacts.Items.Select(f => Fact(f) + " (due now)"));
        lines.AddRange(c.ExpiringFacts.Items.Select(f => Fact(f) + " (expires soon)"));
        lines.AddRange(c.RelevantEntities.Items.Select(e => $"{e.Name} ({e.Type})"));
        lines.AddRange(c.RelevantRelationships.Items.Select(r => $"{r.SourceName} -[{r.Relationship.RelationshipType}]-> {r.TargetName}"));
        lines.AddRange(c.RelevantPreferences.Items.Select(p => p.PreferenceText));
        lines.AddRange(c.RelevantMessages.Items.Select(m => $"earlier, {m.Role}: {m.Content}"));
        lines.AddRange(c.SimilarTraces.Items.Select(t => t.Outcome is { Length: > 0 } ? $"{t.Task} ({t.Outcome})" : t.Task));
        return [.. lines.Distinct(StringComparer.Ordinal)];
    }

    private static List<ChatMessage> Prompt(string question, IReadOnlyList<string> memories) =>
    [
        new(ChatRole.System, "You are Marta's personal assistant. Today is Saturday 3 October 2026. You remember only what is listed "
            + "under MEMORIES (about Marta, her people and her world) and the conversation so far. Reply to her last message briefly "
            + "and helpfully, in her language. Never invent facts about Marta or her people.\n\nMEMORIES:\n"
            + string.Join("\n", memories.Select(m => $"- {m}"))),
        new(ChatRole.User, question),
    ];

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
