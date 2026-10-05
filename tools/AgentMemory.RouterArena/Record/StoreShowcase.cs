using System.Globalization;
using System.Security.Cryptography;
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
using Neo4j.Driver;
using Testcontainers.Neo4j;

namespace AgentMemory.RouterArena.Record;

/// <summary>
/// <c>store-showcase</c> (root PLAN 41.08): a short conversation of changes written through the library twice, on a fresh
/// copy of the world each time: today's write path, and with the update judge. Then read back how clean the memory ended
/// up (what was written, which replaced memories were closed, which stay live beside their replacement, what was closed
/// wrongly) and asked, through the gate, about each change. Scoring the answers is `store_showcase_grade.py`.
/// </summary>
public sealed class StoreShowcase(TextWriter output)
{
    private static readonly DateTimeOffset WrittenAt = DateTimeOffset.Parse("2026-10-03T11:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset AskedAt = DateTimeOffset.Parse("2026-10-03T12:00:00Z", CultureInfo.InvariantCulture);

    /// <summary>A statement, what it replaces (the world memory's id and text), and the question that checks it afterwards.</summary>
    public sealed record Change(string Said, string? ReplacesId, string? Replaces, string? Question, string? Now);

    public async Task<int> RunAsync(string worldPath, IReadOnlyList<Change> changes, string outPath, string? laya, string? examplesPath,
        bool dryRun, string owner = "marta", CancellationToken cancellationToken = default)
    {
        var resolution = InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } settings || !InferenceClientFactory.TryCreateChatClient(settings, "extraction", out var chat, out _))
        {
            output.WriteLine("error: store-showcase: no inference provider configured (AI_INFERENCE_PROVIDER, BITDEER_API_KEY)");
            return 1;
        }
        var questions = changes.Where(c => c.Question is not null).ToList();
        output.WriteLine($"store-showcase: {changes.Count} statements x 2 write paths, then {questions.Count} questions each; by {settings.Model}");
        if (dryRun)
        {
            output.WriteLine($"store-showcase: dry run: {2 * changes.Count} extractions, {2 * questions.Count} replies. Nothing was sent.");
            return 0;
        }
        var world = ValidationPackReader.ReadFile(worldPath);
        var request = new RecordRequest(worldPath, "", outPath, []);
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
        var runner = new ValidationPackRunner(Neo4j, _ => embeddings, ArenaLogging.Console);
        var runs = new List<object>();
        foreach (var judged in new[] { false, true })
        {
            var prefix = judged ? "ssj" : "sst";
            await using var store = await runner.LoadAsync(world, prefix, cancellationToken).ConfigureAwait(false);
            var clock = new MovableClock(WrittenAt);
            var services = new ServiceCollection();
            services.AddLogging(ArenaLogging.Console);
            services.AddSingleton<IClock>(clock);
            services.AddNeo4jAgentMemory(store.Options, Neo4j, _ => { });
            services.AddSingleton(chat);
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings);
            services.AddAgentMemoryGate(g =>
            {
                g.Mode = MemoryGateMode.Judge;
                g.Judges.Add(new SystemOneEndpoint { Name = "jev", Endpoint = new Uri("https://api.typesafe.ai/v1/systemone"), KeyVariable = "TYPESAFE_API_KEY", Weight = laya is null ? 1.0 : 0.8 });
                if (laya is not null) g.Judges.Add(new SystemOneEndpoint { Name = "laya", Endpoint = new Uri(laya), Weight = 0.2 });
                g.ExamplesPath = examplesPath;
                g.UpdateJudge = judged;   // the only difference between the two runs
            });
            await using var provider = services.BuildServiceProvider();
            var ownerId = store.Owner(owner);
            var sessionId = $"{prefix}-{owner}-now";
            var k = 0;
            foreach (var change in changes)
            {
                using var scope = provider.CreateScope();
                var said = await scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>().AddMessageAsync(new Message
                {
                    MessageId = $"{sessionId}-ss{k++}", ConversationId = sessionId, SessionId = sessionId, Role = "user", Content = change.Said,
                    TimestampUtc = WrittenAt,
                }, cancellationToken).ConfigureAwait(false);
                await scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>()
                    .ExtractAsync(new ExtractionRequest { SessionId = sessionId, UserId = ownerId, Messages = [said] }, cancellationToken).ConfigureAwait(false);
            }
            var written = await ReadBackAsync(provider.GetRequiredService<INeo4jTransactionRunner>(), ownerId, cancellationToken).ConfigureAwait(false);
            var stillLive = new List<string>();
            foreach (var change in changes.Where(c => c.Replaces is not null))
                if (await LiveAsync(provider.GetRequiredService<INeo4jTransactionRunner>(), ownerId, change.Replaces!, cancellationToken).ConfigureAwait(false))
                    stillLive.Add(change.ReplacesId!);
            clock.Now = AskedAt;
            var answers = new List<object>();
            foreach (var change in questions)
            {
                using var scope = provider.CreateScope();
                var context = (await scope.ServiceProvider.GetRequiredService<IMemoryService>().RecallAsync(new RecallRequest
                {
                    SessionId = $"{prefix}-ask-{owner}", UserId = ownerId, Query = change.Question!, TemporalReferenceTime = AskedAt, Options = store.Options.Recall,
                }, cancellationToken).ConfigureAwait(false)).Context;
                var memories = Showcase.Lines(context);
                var reply = await chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, "You are Marta's personal assistant. Today is Saturday 3 October 2026. You remember only what is listed under "
                        + "MEMORIES. Reply briefly. Never invent facts about Marta or her people.\n\nMEMORIES:\n" + string.Join("\n", memories.Select(m => $"- {m}"))),
                    new ChatMessage(ChatRole.User, change.Question!),
                ], cancellationToken: cancellationToken).ConfigureAwait(false);
                answers.Add(new { question = change.Question, now = change.Now, old = change.Replaces, memories, reply = reply.Text });
            }
            var created = written.Where(w => w.Created).ToList();
            output.WriteLine($"  {(judged ? "update judge" : "today"),-12}: {created.Count} written ({string.Join(", ", created.GroupBy(w => w.Kind).Select(g => $"{g.Key} {g.Count()}"))}); "
                + $"{written.Count(w => !w.Created)} closed; {stillLive.Count} of {changes.Count(c => c.Replaces is not null)} replaced memories still live");
            runs.Add(new
            {
                writePath = judged ? "update judge" : "today", written = created.Select(w => new { w.Kind, w.Text }),
                closed = written.Where(w => !w.Created).Select(w => w.Text), stillLive, answers,
            });
        }
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new { format = "store-showcase/1", writtenAt = WrittenAt, model = settings.Model, changes, runs },
            new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        output.WriteLine($"store-showcase: to {outPath}");
        return 0;
    }

    private sealed record Written(string Kind, string Text, bool Created);

    /// <summary>What the conversation created, and what it closed, at the instant of writing.</summary>
    private static Task<List<Written>> ReadBackAsync(INeo4jTransactionRunner tx, string ownerId, CancellationToken cancellationToken) =>
        tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("""
                MATCH (x) WHERE x.owner_id = $owner AND (x:Fact OR x:Preference OR x:Entity)
                  AND (x.created_at = datetime($at) OR x.invalidated_at = datetime($at))
                RETURN labels(x)[0] AS kind, x.created_at = datetime($at) AS created,
                       coalesce(x.subject + ' | ' + x.predicate + ' | ' + x.object, x.preference, x.name) AS text
                UNION ALL
                MATCH (s)-[x]->(t) WHERE x.owner_id = $owner AND x.created_at = datetime($at)
                RETURN 'Relationship' AS kind, true AS created, s.name + ' -[' + type(x) + ']-> ' + t.name AS text
                """, new Dictionary<string, object?> { ["owner"] = ownerId, ["at"] = WrittenAt.ToString("O") }).ConfigureAwait(false);
            return (await cursor.ToListAsync().ConfigureAwait(false))
                .Select(r => new Written(r["kind"].As<string>(), r["text"].As<string>() ?? "", r["created"].As<bool>())).ToList();
        }, cancellationToken);

    /// <summary>Whether the world memory with this text (a fact as "s | p | o", or a preference) is still live.</summary>
    private static Task<bool> LiveAsync(INeo4jTransactionRunner tx, string ownerId, string text, CancellationToken cancellationToken) =>
        tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("""
                MATCH (x) WHERE x.owner_id = $owner AND x.invalidated_at IS NULL
                  AND ((x:Fact AND x.subject + ' | ' + x.predicate + ' | ' + x.object = $text) OR (x:Preference AND x.preference = $text))
                RETURN count(x) AS n
                """, new Dictionary<string, object?> { ["owner"] = ownerId, ["text"] = text }).ConfigureAwait(false);
            return (await cursor.SingleAsync().ConfigureAwait(false))["n"].As<long>() > 0;
        }, cancellationToken);

    private sealed class MovableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public DateTimeOffset UtcNow => Now;
    }
}
