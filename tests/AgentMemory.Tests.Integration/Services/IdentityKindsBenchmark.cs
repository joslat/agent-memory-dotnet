using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.IdentityKinds;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using static AgentMemory.Tests.IdentityKinds.IdentityKindCases;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// The tier 1 run of the Identity Kinds Benchmark (IKB-1), ported from the harness that recorded the 2.3.1 baseline
/// with its counting unchanged. Three arms: <c>previous</c> (the previous resolver), <c>rules</c> (evidence-based
/// identity, no model) and <c>writer</c> (the same, and each turn about an already stored thing carries a correct
/// reference to it: the best a store-aware writer can do). Every case writes under an owner of its own.
/// </summary>
internal static class IdentityKindsBenchmark
{
    public const int Dimensions = 1024;

    public static readonly string[] Arms = ["previous", "rules", "writer"];

    public sealed record Row(
        string Case, string Kind, string Situation, string Arm, int RealThings, int SeparateThings, string Verdict,
        int WrongMerges, bool? LastMentionJoinedToTheRightThing, int OpenProposals, int TimesNamedInProfileBlock,
        int EntitiesRecalledForTheName, string[] Decisions, string? Failure);

    private sealed record Decision(string Status, string Name, string? ConfirmedId, string Diagnostics);

    public static async Task<IReadOnlyList<Row>> RunArmAsync(string arm, string uri, string user, string password, IDriver driver)
    {
        var cases = Build().ToArray();
        var rows = new List<Row>();
        var start = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var logs = new ConcurrentQueue<string>();
        var scripted = new Scripted();
        await using var sp = Services(uri, user, password, arm != "previous", scripted, logs);
        await sp.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
        await Q(driver, "CALL db.awaitIndexes(120)", new { });

        foreach (var c in cases)
        {
            var owner = $"idb-{arm}-{Guid.NewGuid():N}";
            var names = c.Turns.SelectMany(t => t.Mentions).Where(m => m.Thing < FillerThing).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            var nodeThing = new Dictionary<string, int>(StringComparer.Ordinal);   // stored node -> the real thing it was first made for
            var firstNode = new Dictionary<int, string>();
            var mentionThings = new List<int>();                                   // one entry per mention, in order
            string? failure = null;

            foreach (var t in c.Turns)
            {
                try
                {
                    var m = new Message
                    {
                        MessageId = Guid.NewGuid().ToString("N"), SessionId = owner + "-" + t.Session, ConversationId = owner + "-" + t.Session,
                        Role = "user", Content = t.Text, TimestampUtc = start.AddSeconds(t.AtSeconds),
                    };
                    scripted.ByMessage[m.MessageId] = t;
                    if (arm == "writer")
                        scripted.References[m.MessageId] = t.Mentions.Select(x => firstNode.TryGetValue(x.Thing, out var target)
                            ? new ExtractedEntityReference { EntityId = target, Quote = x.Name } : null).ToArray();
                    await sp.GetRequiredService<IShortTermMemoryService>().AddMessagesAsync([m]);
                    await using var scope = sp.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>()
                        .ExtractAsync(new ExtractionRequest { Messages = [m], SessionId = m.SessionId, UserId = owner });
                    mentionThings.AddRange(t.Mentions.Select(x => x.Thing));
                    foreach (var r in await Q(driver, "MATCH (e:Entity {owner_id: $o}) RETURN e.id AS id, e.name AS name", new { o = owner }))
                    {
                        var id = r["id"].As<string>();
                        if (nodeThing.ContainsKey(id)) continue;
                        var thing = t.Mentions.FirstOrDefault(x => x.Name == r["name"].As<string>())?.Thing ?? t.Mentions[0].Thing;
                        nodeThing[id] = thing;
                        firstNode.TryAdd(thing, id);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                    break;
                }
            }

            var live = await Q(driver, @"MATCH (e:Entity {owner_id: $o}) WHERE e.invalidated_at IS NULL AND e.merged_into IS NULL
                RETURN e.id AS id, e.name AS name, e.type AS type", new { o = owner });
            var decisions = new List<Decision>();
            foreach (var d in await Q(driver, "MATCH (d:IdentityDecision {owner_id: $o}) RETURN d.identity_record_json AS j ORDER BY d.created_at", new { o = owner }))
            {
                using var doc = JsonDocument.Parse(d["j"].As<string>());
                var root = doc.RootElement;
                var status = root.GetProperty("Status");
                decisions.Add(new(
                    status.ValueKind == JsonValueKind.Number ? new[] { "Confirmed", "Pending", "New" }[status.GetInt32()] : status.ToString(),
                    root.GetProperty("Observation").GetProperty("Name").GetString() ?? "",
                    root.TryGetProperty("ConfirmedEntityId", out var ce) && ce.ValueKind == JsonValueKind.String ? ce.GetString() : null,
                    root.TryGetProperty("Diagnostics", out var dg) && dg.ValueKind == JsonValueKind.Array
                        ? string.Join("+", dg.EnumerateArray().Select(x => x.ToString()).Where(x => x != "identity_revalidated_without_confirmation")) : ""));
            }

            // How many separate things the store holds under the case's name(s).
            var separate = arm == "previous"
                ? live.Count(e => names.Contains(e["name"].As<string>()))
                : decisions.Count(d => names.Contains(d.Name) && d.Status != "Confirmed");
            // A wrong merge: a mention confirmed into a stored thing that was made for a different real thing.
            var wrongMerges = 0;
            bool? lastJoinedRight = null;
            if (arm == "previous") wrongMerges = Math.Max(0, c.RealThings - separate);
            else
            {
                var own = decisions.Where(d => names.Contains(d.Name)).ToList();
                var things = mentionThings.Where(x => x < FillerThing).ToList();
                for (var i = 0; i < own.Count && i < things.Count; i++)
                    if (own[i].Status == "Confirmed" && own[i].ConfirmedId is { } id && nodeThing.TryGetValue(id, out var target) && target != things[i])
                        wrongMerges++;
                if (c.LastMustJoinThing is { } must && own.Count > 0)
                    lastJoinedRight = own[^1].Status == "Confirmed" && own[^1].ConfirmedId is { } last &&
                        nodeThing.TryGetValue(last, out var joined) && joined == must;
            }

            string? block = null;
            var recalled = 0;
            try
            {
                await using var scope2 = sp.CreateAsyncScope();
                var wm = scope2.ServiceProvider.GetService<IWorkingMemoryService>();
                if (wm is not null)
                {
                    await wm.RebuildAsync(owner);
                    block = (await wm.GetAsync(owner))?.Text;
                }
                var answer = await scope2.ServiceProvider.GetRequiredService<IMemoryService>().RecallAsync(new RecallRequest
                {
                    SessionId = owner + "-s1", UserId = owner, Query = names.First(), Options = RecallOptions.Default with { MinSimilarityScore = 0.0 },
                });
                recalled = answer.Context.RelevantEntities.Items.Count(e => names.Contains(e.Name));
            }
            catch (Exception ex)
            {
                failure ??= "read: " + ex.GetType().Name + ": " + ex.Message;
            }
            var keyLines = (block ?? "").Split('\n').SkipWhile(l => !l.StartsWith("Key entities", StringComparison.Ordinal)).Skip(1)
                .Where(l => names.Any(n => l.StartsWith(n + " (", StringComparison.Ordinal))).ToList();

            var verdict = failure is not null ? "failed" : separate == c.RealThings && wrongMerges == 0 ? "right" : separate > c.RealThings ? "split" : "merged";
            rows.Add(new(c.Id, c.Kind, c.Situation, arm, c.RealThings, separate, verdict, wrongMerges, lastJoinedRight,
                decisions.Count(d => d.Status == "Pending"), keyLines.Count, recalled,
                decisions.Where(d => names.Contains(d.Name)).Select(d => d.Status + (d.Diagnostics.Length > 0 ? $" [{d.Diagnostics}]" : "")).ToArray(),
                failure));
        }
        return rows;
    }

    private static ServiceProvider Services(string uri, string user, string password, bool evidence, Scripted scripted, ConcurrentQueue<string> logs)
    {
        var s = new ServiceCollection();
        s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(new CaptureLoggerProvider(logs)));
        s.AddSingleton<IEntityExtractor>(scripted);
        s.AddSingleton<IFactExtractor>(scripted);
        s.AddSingleton<IPreferenceExtractor>(scripted);
        s.AddSingleton<IRelationshipExtractor>(scripted);
        s.AddNeo4jAgentMemory(o =>
        {
            o.Uri = uri; o.Username = user; o.Password = password; o.Database = "neo4j";
            o.EmbeddingDimensions = Dimensions; o.EncryptionEnabled = false;
        });
        s.AddAgentMemoryCore(_ => { });
        s.Configure<IdentityResolutionOptions>(o =>
        {
            o.Enabled = evidence;
            if (evidence) o.ApplyConversationalPolicies();
        });
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<IIdGenerator, GuidIdGenerator>();
        s.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new TokenHashEmbedder(Dimensions));
        return s.BuildServiceProvider();
    }

    private static async Task<List<IRecord>> Q(IDriver d, string cypher, object p)
    {
        await using var s = d.AsyncSession();
        var c = await s.RunAsync(cypher, p);
        return await c.ToListAsync();
    }

    /// <summary>The extraction of every turn, as the case gives it; with the writer arm's references to stored things.</summary>
    private sealed class Scripted : IEntityExtractor, IFactExtractor, IPreferenceExtractor, IRelationshipExtractor
    {
        public readonly ConcurrentDictionary<string, Turn> ByMessage = new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string, ExtractedEntityReference?[]> References = new(StringComparer.Ordinal);

        private Turn? Find(IReadOnlyList<Message> messages) => messages.Count > 0 && ByMessage.TryGetValue(messages[^1].MessageId, out var t) ? t : null;

        Task<IReadOnlyList<ExtractedEntity>> IEntityExtractor.ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
        {
            var turn = Find(messages);
            if (turn is null) return Task.FromResult<IReadOnlyList<ExtractedEntity>>([]);
            References.TryGetValue(messages[^1].MessageId, out var refs);
            return Task.FromResult<IReadOnlyList<ExtractedEntity>>(turn.Mentions.Select((m, i) => new ExtractedEntity
            {
                Name = m.Name, Type = m.Type, Confidence = 0.9, Reference = refs is not null && i < refs.Length ? refs[i] : null,
            }).ToArray());
        }

        Task<IReadOnlyList<ExtractedFact>> IFactExtractor.ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
        {
            var turn = Find(messages);
            return Task.FromResult<IReadOnlyList<ExtractedFact>>(turn is null ? [] :
                [new ExtractedFact { Subject = turn.Mentions[0].Name, Predicate = "was said", Object = turn.Text, Confidence = 0.9 }]);
        }

        Task<IReadOnlyList<ExtractedPreference>> IPreferenceExtractor.ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtractedPreference>>([]);

        Task<IReadOnlyList<ExtractedRelationship>> IRelationshipExtractor.ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtractedRelationship>>([]);
    }

    /// <summary>
    /// The baseline's embedder: a hashed bag of words (SHA-256 per token, never string.GetHashCode), with a small
    /// per-text component so that no two different texts tie on similarity.
    /// </summary>
    private sealed class TokenHashEmbedder(int dims) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public float[] Embed(string text)
        {
            var v = new float[dims];
            foreach (var raw in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var tok = new string(raw.Where(char.IsLetterOrDigit).ToArray());
                if (tok.Length == 0) continue;
                var h = SHA256.HashData(Encoding.UTF8.GetBytes(tok));
                var idx = BitConverter.ToUInt32(h, 0) % (uint)dims;
                v[idx] += (h[4] & 1) == 0 ? 1f : -1f;
            }
            var whole = SHA256.HashData(Encoding.UTF8.GetBytes("text:" + text));
            for (var i = 0; i < whole.Length && i < dims; i++)
                v[(whole[i] * 7 + i) % dims] += (whole[(i + 1) % whole.Length] - 127.5f) / 1000f;
            var norm = Math.Sqrt(v.Sum(x => (double)x * x));
            if (norm == 0)
            {
                v[0] = 1;
                return v;
            }
            for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
            return v;
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(t => new Embedding<float>(Embed(t)))));

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class CaptureLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, sink);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (sink.Count < 200)
                    sink.Enqueue($"{logLevel} {category}: {formatter(state, exception)}");
            }
        }
    }
}
