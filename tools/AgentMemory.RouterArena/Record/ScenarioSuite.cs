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
/// Short conversations, each aimed at one behaviour of the store-aware writer (a transition that ends a state, a one-off
/// that ends with its day, a repeat that is reinforced, a place or someone else's news kept, and cases that must not
/// trigger any of it), played through the library as the Recommended preset ships it, one owner each, and checked on the
/// stored graph: no judge. A minutes-long loop for a writer change, before a world and a fresh read measure it.
/// </summary>
public sealed class ScenarioSuite(TextWriter output)
{
    private const int ContextMessages = 4;

    private static readonly string[] StoreCypher =
    [
        "MATCH (x) WHERE x.owner_id = $owner AND (x:Fact OR x:Entity OR x:Preference) RETURN labels(x) AS kind, properties(x) AS p, null AS s, null AS t",
        "MATCH (s:Entity)-[x]->(t:Entity) WHERE x.owner_id = $owner RETURN [type(x)] AS kind, properties(x) AS p, s.name AS s, t.name AS t",
    ];

    private static readonly System.Text.RegularExpressions.Regex SeedConnection =
        new(@"^\s*(?<s>.+?)\s*-\[\s*(?<r>[A-Z_]+)\s*\]->\s*(?<t>.+?)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private sealed record Turn(DateTimeOffset At, string User, string? Assistant);

    private sealed record Check(string Kind, string Text, int AtLeast);

    private sealed record Scenario(string Id, string Fix, string Person, DateTimeOffset? CheckAt, IReadOnlyList<Turn> Turns, IReadOnlyList<Check> Checks,
        IReadOnlyList<string> Seeds);

    private sealed record Item(string Kind, string Text, bool Closed, DateTimeOffset? Until, int Mentions);

    public async Task<int> RunAsync(string file, int repeat, string outPath, bool dryRun, int parallel = 6, CancellationToken cancellationToken = default)
    {
        var scenarios = Read(file);
        var resolution = InferenceProviderEnvironment.Resolve();
        if (resolution.Settings is not { } settings || !InferenceClientFactory.TryCreateChatClient(settings, "extraction", out var chat, out var why))
        {
            output.WriteLine("error: scenarios: no inference provider configured (AI_INFERENCE_PROVIDER, BITDEER_API_KEY)");
            return 1;
        }
        output.WriteLine($"scenarios: {scenarios.Count} ({string.Join(", ", scenarios.GroupBy(s => s.Fix).Select(g => $"{g.Key} {g.Count()}"))}), "
            + $"{scenarios.Sum(s => s.Seeds.Count)} memories seeded, "
            + $"{scenarios.Sum(s => s.Turns.Count)} turns, {repeat} time(s); written by {settings.Provider} ({settings.Model}), the Recommended preset, the update judge on JEV");
        if (dryRun)
        {
            output.WriteLine("scenarios: dry run: nothing was sent.");
            return 0;
        }

        using var ollama = new OllamaEmbeddingGenerator("http://127.0.0.1:11434", "bge-m3");
        ollama.Dimensions = (await ollama.GenerateAsync(["ping"], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.Length;
        var embeddings = new CachingEmbeddingGenerator(ollama);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using var container = new Neo4jBuilder(Recorder.Image)
            .WithEnvironment("NEO4J_AUTH", $"neo4j/{password}")
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

        ServiceProvider Build(Clock clock)
        {
            var services = new ServiceCollection();
            services.AddLogging(ArenaLogging.Console);
            services.AddSingleton<IClock>(clock);
#pragma warning disable AMREC001, AMGATE001
            services.AddNeo4jAgentMemory(MemoryOptions.CreateRecommended(), Neo4j, o => o.ApplyRecommended());
            services.AddSingleton<IChatClient>(new UsageCountingChatClient(chat));
            // An instance, so the container never disposes the shared generator under another scenario.
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new CachingEmbeddingGenerator(ollama));
            services.AddAgentMemoryGate(g =>
            {
                g.ApplyRecommended();
                g.Judges.Add(new SystemOneEndpoint { Name = "jev", Endpoint = new Uri("https://api.typesafe.ai/v1/systemone"), KeyVariable = "TYPESAFE_API_KEY" });
            });
#pragma warning restore AMREC001, AMGATE001
            return services.BuildServiceProvider();
        }

        await using (var first = Build(new Clock(scenarios[0].Turns[0].At)))
        {
            await first.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(cancellationToken).ConfigureAwait(false);
            await first.GetRequiredService<INeo4jTransactionRunner>().WriteAsync(async runner =>
                await runner.RunAsync("CALL db.awaitIndexes(60)").ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        // Each scenario has its own services and clock (the clock is one per container, and scenarios live on different
        // dates), all on one store, one owner each; several run at once.
        var runs = Enumerable.Range(1, repeat).SelectMany(r => scenarios.Select(sc => (Repeat: r, Scenario: sc))).Select((x, i) => (x.Repeat, x.Scenario, Order: i)).ToList();
        var done = new List<(int Order, string Fix, object Result, int Passed, int Total)>();
        var gate = new object();
        await Parallel.ForEachAsync(runs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = cancellationToken }, async (run, ct) =>
        {
            var (r, scenario, order) = run;
            var owner = $"sc-{scenario.Id}-r{r}".ToLowerInvariant();
            var sessionId = $"{owner}-s1";
            var failed = new List<string>();
            var said = new List<Message>();
            var clock = new Clock(scenario.Turns[0].At.AddDays(-30));
            await using var provider = Build(clock);
            var tx = provider.GetRequiredService<INeo4jTransactionRunner>();
            using (var scope = provider.CreateScope())
            {
                var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
                // A seed is a fact ("subject | predicate | object") or a connection ("A -[RELATION]-> B"; its two people are
                // seeded as entities once each).
                var people = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                async Task<string> PersonAsync(string name)
                {
                    if (people.TryGetValue(name, out var id)) return id;
                    var entity = await longTerm.AddEntityAsync(new Entity
                    {
                        EntityId = $"{owner}-person-{people.Count:00}", Name = name, Type = "PERSON", Confidence = 0.9, OwnerId = owner, CreatedAtUtc = clock.Now,
                    }, ct).ConfigureAwait(false);
                    return people[name] = entity.EntityId;
                }
                for (var i = 0; i < scenario.Seeds.Count; i++)
                {
                    if (SeedConnection.Match(scenario.Seeds[i]) is { Success: true } link)
                    {
                        await longTerm.AddRelationshipAsync(new Relationship
                        {
                            RelationshipId = $"{owner}-seed-{i:00}", SourceEntityId = await PersonAsync(link.Groups["s"].Value).ConfigureAwait(false),
                            TargetEntityId = await PersonAsync(link.Groups["t"].Value).ConfigureAwait(false), RelationshipType = link.Groups["r"].Value,
                            Confidence = 0.9, OwnerId = owner, CreatedAtUtc = clock.Now.AddMinutes(i),
                        }, ct).ConfigureAwait(false);
                        continue;
                    }
                    var parts = scenario.Seeds[i].Split('|', 3, StringSplitOptions.TrimEntries);
                    if (parts.Length != 3) continue;
                    await longTerm.AddFactAsync(new Fact
                    {
                        FactId = $"{owner}-seed-{i:00}", Subject = parts[0], Predicate = parts[1], Object = parts[2], Confidence = 0.9,
                        OwnerId = owner, CreatedAtUtc = clock.Now.AddMinutes(i),
                    }, ct).ConfigureAwait(false);
                }
            }
            clock.Now = scenario.Turns[0].At;
            using (var scope = provider.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>()
                    .AddConversationAsync(sessionId, sessionId, userId: owner, cancellationToken: ct).ConfigureAwait(false);
            for (var k = 0; k < scenario.Turns.Count; k++)
            {
                var turn = scenario.Turns[k];
                clock.Now = turn.At;
                using var scope = provider.CreateScope();
                var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
                var message = await shortTerm.AddMessageAsync(new Message
                {
                    MessageId = $"{sessionId}-m{k:00}u", ConversationId = sessionId, SessionId = sessionId, Role = "user", Content = turn.User, TimestampUtc = turn.At,
                }, ct).ConfigureAwait(false);
                try
                {
                    await scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>().ExtractAsync(new ExtractionRequest
                    {
                        SessionId = sessionId, UserId = owner, Messages = [message],
                        ContextMessages = said.Skip(Math.Max(0, said.Count - ContextMessages)).ToList(),
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add($"{message.MessageId}: {ex.GetType().Name}: {ex.Message}");
                }
                said.Add(message);
                if (turn.Assistant is { } reply)
                    said.Add(await shortTerm.AddMessageAsync(new Message
                    {
                        MessageId = $"{sessionId}-m{k:00}a", ConversationId = sessionId, SessionId = sessionId, Role = "assistant", Content = reply,
                        TimestampUtc = turn.At.AddSeconds(5),
                    }, ct).ConfigureAwait(false));
            }

            var at = scenario.CheckAt ?? scenario.Turns[^1].At.AddMinutes(1);
            var items = Items(await StoreSessions.QueryAsync(tx, StoreCypher, new Dictionary<string, object?> { ["owner"] = owner }, ct)
                .ConfigureAwait(false), scenario.Person);
            var checks = scenario.Checks.Select(c => new { check = $"{c.Kind}: {c.Text}{(c.Kind == "mentions" ? $" >= {c.AtLeast}" : "")}", passed = Passes(c, items, at) }).ToList();
            var result = new
            {
                repeat = r, scenario = scenario.Id, fix = scenario.Fix, checkAt = at, failed, checks,
                store = items.Select(i => $"{(i.Closed ? "[closed] " : i.Until is { } u && u <= at ? $"[ended {u:yyyy-MM-dd}] " : "")}{i.Kind}: {i.Text}"
                    + (i.Until is { } until && !(until <= at) ? $" [until {until:yyyy-MM-dd}]" : "") + (i.Mentions > 1 ? $" (x{i.Mentions})" : "")),
            };
            lock (gate)
            {
                done.Add((order, scenario.Fix, result, checks.Count(c => c.passed), checks.Count));
                output.WriteLine($"  r{r} {scenario.Id,-28} {string.Join("  ", checks.Select(c => (c.passed ? "PASS " : "FAIL ") + c.check))}{(failed.Count > 0 ? $"  ({failed.Count} turn(s) failed)" : "")}");
            }
        }).ConfigureAwait(false);

        var results = done.OrderBy(d => d.Order).Select(d => d.Result).ToList();
        var tally = done.GroupBy(d => d.Fix).ToDictionary(g => g.Key, g => (Passed: g.Sum(d => d.Passed), Total: g.Sum(d => d.Total)));
        output.WriteLine("scenarios: " + string.Join(", ", tally.Select(t => $"{t.Key} {t.Value.Passed}/{t.Value.Total}")));
        await RunFile.WriteAsync(outPath, JsonSerializer.Serialize(new
        {
            format = "scenarios/1", file = Path.GetFileName(file), model = settings.Model, repeat,
            tally = tally.ToDictionary(t => t.Key, t => new { passed = t.Value.Passed, total = t.Value.Total }), results,
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static bool Passes(Check check, IReadOnlyList<Item> items, DateTimeOffset at)
    {
        var alternatives = check.Text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A fact reads "subject | predicate | object"; a check is a phrase ("works at Gord's shop"), so the separators go.
        bool Says(Item i) => alternatives.Any(a => i.Text.Replace(" | ", " ", StringComparison.Ordinal).Contains(a, StringComparison.OrdinalIgnoreCase));
        bool Current(Item i) => !i.Closed && (i.Until is null || i.Until > at);
        return check.Kind switch
        {
            "live" => items.Any(i => Current(i) && Says(i)),
            "notLive" => !items.Any(i => Current(i) && i.Kind != "entity" && Says(i)),
            "known" => items.Any(Says),
            "mentions" => items.Any(i => Current(i) && Says(i) && i.Mentions >= check.AtLeast),
            _ => false,
        };
    }

    private static IReadOnlyList<Item> Items(IEnumerable<Dictionary<string, object?>> rows, string person)
    {
        var items = new List<Item>();
        foreach (var row in rows)
        {
            var p = (Dictionary<string, object?>)row["props"]!;
            string? S(string k) => p.TryGetValue(k, out var v) ? v?.ToString() : null;
            var kind = ((string)row["kind"]!).Split(',')[0];
            var text = kind switch
            {
                "Fact" => $"{S("subject")} | {S("predicate")} | {S("object")}",
                "Preference" => S("preference") ?? "",
                "Entity" => $"{S("name")} ({S("type")})",
                _ => $"{row["source"]} -[{S("relation_type") ?? kind}]-> {row["target"]}",
            };
            text = text.Replace("user |", $"{person} |", StringComparison.Ordinal);
            var until = S("valid_until") is { } u && DateTimeOffset.TryParse(u.Split('[')[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : (DateTimeOffset?)null;
            var closed = S("invalidated_at") is { Length: > 0 } || S("merged_into") is { Length: > 0 };
            var mentions = int.TryParse(S("mention_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 1;
            items.Add(new Item(kind switch { "Fact" => "fact", "Preference" => "preference", "Entity" => "entity", _ => "connection" }, text, closed, until, mentions));
        }
        return items;
    }

    private static IReadOnlyList<Scenario> Read(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var filler = doc.RootElement.TryGetProperty("filler", out var f) ? f.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
        var list = new List<Scenario>();
        foreach (var s in doc.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            var turns = s.GetProperty("turns").EnumerateArray().Select(t => new Turn(
                DateTimeOffset.Parse(t.GetProperty("at").GetString()!, CultureInfo.InvariantCulture),
                t.GetProperty("user").GetString()!,
                t.TryGetProperty("assistant", out var a) ? a.GetString() : null)).ToList();
            var checks = s.GetProperty("checks").EnumerateArray().Select(c =>
            {
                var kind = c.EnumerateObject().First(p => p.Name != "atLeast").Name;
                return new Check(kind, c.GetProperty(kind).GetString()!, c.TryGetProperty("atLeast", out var n) ? n.GetInt32() : 1);
            }).ToList();
            var seeds = (s.TryGetProperty("filler", out var useFiller) && useFiller.GetBoolean() ? filler : [])
                .Concat(s.TryGetProperty("seed", out var seed) ? seed.EnumerateArray().Select(x => x.GetString()!) : []).ToList();
            list.Add(new Scenario(s.GetProperty("id").GetString()!, s.GetProperty("fix").GetString()!, s.GetProperty("person").GetString()!,
                s.TryGetProperty("checkAt", out var ca) ? DateTimeOffset.Parse(ca.GetString()!, CultureInfo.InvariantCulture) : null, turns, checks, seeds));
        }
        return list;
    }

    private sealed class Clock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset Now { get; set; } = start;

        public DateTimeOffset UtcNow => Now;
    }
}
