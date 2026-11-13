using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Extraction.Llm;
using AgentMemory.Gate;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace AgentMemory.Scenarios.Memories;

/// <summary>
/// One way of configuring AgentMemory for the scenario. Arms that share <see cref="Writes"/> share one written store:
/// the first of them writes it, the others only recall from it, so a recall setting is compared on the same memories.
/// </summary>
internal sealed record GraphArm(
    string Name,
    string Writes,
    string How,
    Func<MemoryOptions> Memory,
    Action<LlmExtractionOptions> Extraction,
    Action<AgentFrameworkOptions> Framework,
    Action<MemoryGateOptions>? Gate,
    Action<IdentityResolutionOptions>? Identity = null)
{
    /// <summary>Whether the arm runs the retrieval judge (JEV).</summary>
    public bool Judge => Gate is not null;
}

/// <summary>Where the run's Neo4j is, and the embedding width its vector indexes were built for.</summary>
internal sealed record Neo4jTarget(string Uri, string Username, string Password, int EmbeddingDimensions);

/// <summary>
/// The graph memory: AgentMemory on Neo4j, through the Agent Framework facade as a host uses it. A model reads each
/// statement and writes people, events, facts and their dates; recall picks what reaches the prompt.
/// </summary>
internal sealed class GraphMemory : IMemory
{
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly Neo4jMicrosoftMemoryFacade _facade;
    private readonly MemoryOptions _options;
    private readonly Neo4jTarget _neo4j;
    private readonly bool _writes;

    private GraphMemory(GraphArm arm, ServiceProvider services, MemoryOptions options, Neo4jTarget neo4j, string owner, bool writes)
    {
        Arm = arm;
        _services = services;
        _scope = services.CreateAsyncScope();
        _facade = _scope.ServiceProvider.GetRequiredService<Neo4jMicrosoftMemoryFacade>();
        _options = options;
        _neo4j = neo4j;
        Owner = owner;
        _writes = writes;
    }

    public GraphArm Arm { get; }

    public string Name => Arm.Name;

    public string How => Arm.How;

    /// <summary>Whose memories these are: one owner per written store, so arms never see each other's.</summary>
    public string Owner { get; }

    /// <summary>Whether this arm writes its store, or recalls from the one an earlier arm wrote.</summary>
    public bool Writes => _writes;

    public static async Task<GraphMemory> CreateAsync(
        GraphArm arm, IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embeddings, TimeProvider clock,
        Neo4jTarget neo4j, string owner, bool writes, ILoggerProvider log, CallLedger? ledger = null, CancellationToken cancellationToken = default, IChatClient? identityChat = null)
    {
        var options = arm.Memory();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders().AddProvider(log).SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(clock);
        services.AddSingleton(chat);
        services.AddSingleton(embeddings);
        services.AddNeo4jAgentMemory(
            options,
            o =>
            {
                o.Uri = neo4j.Uri;
                o.Username = neo4j.Username;
                o.Password = neo4j.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = neo4j.EmbeddingDimensions;
                if (arm.Identity is not null) o.Extensions.Add("identity-evidence");
            },
            arm.Extraction);
        if (arm.Identity is { } identity)
        {
            services.Configure(identity);
            if (identityChat is not null) services.AddIdentityAdjudication(_ => identityChat, maxInFlight: 1);
        }
        services.AddAgentMemoryFramework(o =>
        {
            arm.Framework(o);
            // Inline, so each statement is learned at the moment the script says it was said.
            o.ExtractInBackground = false;
        });
        if (arm.Gate is { } gate)
        {
            // The judge's transport, recorded: the gate registers its client only if none is (TryAdd).
            if (ledger is not null)
            {
                services.AddSingleton(sp => new SystemOneClient(
                    new HttpClient(new RecordingJudgeHandler(ledger)
                    {
                        InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
                    }) { Timeout = TimeSpan.FromSeconds(30) },
                    sp.GetService<TimeProvider>(), sp.GetService<ILogger<SystemOneClient>>()));
            }

            services.AddAgentMemoryGate(g =>
            {
                g.ApplyRecommended();
                g.Judges.Add(new SystemOneEndpoint
                {
                    Name = "jev", Endpoint = new Uri("https://api.typesafe.ai/v1/systemone"), KeyVariable = "TYPESAFE_API_KEY",
                });
                gate(g);
            });
        }

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(cancellationToken).ConfigureAwait(false);
        return new GraphMemory(arm, provider, options, neo4j, owner, writes);
    }

    public Task RememberAsync(Turn turn, CancellationToken cancellationToken = default)
    {
        if (!_writes) return Task.CompletedTask;
        var session = $"{Owner}-{turn.Session}";
        return _facade.PersistAfterRunAsync([new ChatMessage(ChatRole.User, turn.Text)], session, session, Owner, cancellationToken);
    }

    public async Task<string> RecallAsync(string question, int? budgetTokens, CancellationToken cancellationToken = default)
    {
        // A new conversation, as it would be weeks later: nothing of what was said is "recent".
        var session = $"{Owner}-ask";
        var recalled = await _facade.GetContextForRunAsync([new ChatMessage(ChatRole.User, question)], session, session, Owner, cancellationToken)
            .ConfigureAwait(false);
        return string.Join("\n\n", recalled.Select(message => message.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    /// <summary>
    /// The same recall with ranking diagnostics on: which facts were candidates, their scores and ranks, and what each
    /// section searched. It explains a fact that never reached the prompt: never a candidate, or a candidate that lost.
    /// </summary>
    public async Task<object> ExplainAsync(string question, CancellationToken cancellationToken = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var result = await memory.RecallAsync(new RecallRequest
        {
            SessionId = $"{Owner}-ask",
            UserId = Owner,
            Query = question,
            Question = question,
            Options = _options.Recall with { IncludeDiagnostics = true },
        }, cancellationToken).ConfigureAwait(false);

        var context = result.Context;
        var facts = context.RelevantFacts.Items.ToDictionary(f => f.FactId, f => f);
        return new
        {
            totalItems = result.TotalItemsRetrieved,
            resolvedAsOf = context.ResolvedTemporalAsOf,
            metadata = result.Metadata.ToDictionary(m => m.Key, m => m.Value?.ToString()),
            facts = new
            {
                diagnostics = context.RelevantFacts.Diagnostics,
                ranked = context.RelevantFacts.RankedItems.Select(r => new
                {
                    r.ItemId, score = Math.Round(r.Score, 4), r.RetrievalRank, r.ContextRank,
                    text = facts.TryGetValue(r.ItemId, out var f) ? Describe(f) : null,
                }),
                kept = context.RelevantFacts.Items.Select(Describe),
            },
            entities = new { diagnostics = context.RelevantEntities.Diagnostics, kept = context.RelevantEntities.Items.Select(e => $"{e.Name} ({e.Type})") },
            window = new { resolved = context.ResolvedTimeWindow, facts = context.WindowFacts.Items.Select(Describe) },
            upcoming = context.UpcomingFacts.Items.Select(Describe),
            overlapping = context.OverlappingFacts.Items.Select(Describe),
            due = context.DueFacts.Items.Select(Describe),
            expiring = context.ExpiringFacts.Items.Select(Describe),
        };
    }

    /// <summary>Probe native event support on the actual written store, with no vector/model retrieval.</summary>
    public async Task<EventDetailProbe> ProbeEventDetailsAsync(DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var memory = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
        var ownerScope = MemoryScope.For(Owner, includeShared: false);
        var seeds = await memory.GetFactsInWindowAsync(start, end, 10, ownerScope, cancellationToken).ConfigureAwait(false);
        var ids = seeds.Select(fact => fact.FactId).ToArray();
        var details = await memory.GetEventDetailsAsync(ids, ids, 8, ownerScope, cancellationToken).ConfigureAwait(false);
        return new(seeds.Select(Describe).ToArray(), details.Where(fact =>
            fact.OccurredOn is null && fact.ValidFrom is null && fact.ValidUntil is null)
            .Select(fact => new EventDetailFact(fact.Subject, fact.Predicate, fact.Object, fact.FactId)).ToArray(), ids);
    }

    /// <summary>What the store holds for this owner, read straight from Neo4j.</summary>
    public async Task<GraphSnapshot> SnapshotAsync()
    {
        await using var driver = GraphDatabase.Driver(_neo4j.Uri, AuthTokens.Basic(_neo4j.Username, _neo4j.Password));

        var facts = await RowsAsync(driver, """
            MATCH (f:Fact {owner_id: $owner})
            WHERE f.invalidated_at IS NULL
            RETURN f.id AS factId, f.subject AS subject, f.predicate AS predicate, f.object AS object, f.category AS category,
                   toString(f.occurred_on) AS occurred, toString(f.valid_from) AS validFrom, toString(f.valid_until) AS validUntil
            ORDER BY f.created_at
            """, row => new StoredFact(
                row["subject"].As<string>(), row["predicate"].As<string>(), row["object"].As<string>(),
                Day(row["occurred"]), Day(row["validFrom"]), Day(row["validUntil"]), row["category"].As<string?>(), row["factId"].As<string>())).ConfigureAwait(false);

        var closed = await RowsAsync(driver, """
            MATCH (f:Fact {owner_id: $owner})
            WHERE f.invalidated_at IS NOT NULL
            RETURN f.subject + ' · ' + f.predicate + ' · ' + f.object + ' (closed: ' + coalesce(f.invalidated_reason, 'no reason') + ')' AS text
            ORDER BY f.created_at
            """, row => row["text"].As<string>()).ConfigureAwait(false);

        var entities = await RowsAsync(driver, """
            MATCH (e:Entity {owner_id: $owner})
            RETURN e.name AS name, coalesce(e.type, '') AS type
            ORDER BY type, name
            """, row => $"{row["name"].As<string>()} ({row["type"].As<string>()})").ConfigureAwait(false);

        var relationships = await RowsAsync(driver, """
            MATCH (a:Entity {owner_id: $owner})-[r:RELATED_TO]->(b:Entity)
            RETURN a.name + ' — ' + coalesce(r.relation_type, 'RELATED_TO') + ' → ' + b.name AS text
            ORDER BY text
            """, row => row["text"].As<string>()).ConfigureAwait(false);

        var preferences = await RowsAsync(driver, """
            MATCH (p:Preference {owner_id: $owner})
            RETURN '[' + coalesce(p.category, '') + '] ' + p.preference AS text
            ORDER BY text
            """, row => row["text"].As<string>()).ConfigureAwait(false);

        return new GraphSnapshot(facts, closed, entities, relationships, preferences);
    }

    /// <summary>Original scoped decision/relationship receipts for the opt-in validation report.</summary>
    public async Task<object?> IdentitySnapshotAsync()
    {
        if(Arm.Identity is null) return null;
        await using var driver=GraphDatabase.Driver(_neo4j.Uri,AuthTokens.Basic(_neo4j.Username,_neo4j.Password));
        var decisions=await RowsAsync(driver,"MATCH (d:IdentityDecision {owner_id:$owner}) RETURN d.identity_record_json AS record ORDER BY d.id",
            row=>System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(row["record"].As<string>()));
        var relationships=await RowsAsync(driver,"MATCH (o:IdentityRelationshipObservation {owner_id:$owner}) RETURN o.identity_record_json AS record ORDER BY o.id",
            row=>System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(row["record"].As<string>()));
        var bindings=await RowsAsync(driver,"""
            MATCH (f:Fact {owner_id:$owner})-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding)-[:DECIDED_BY]->(d:IdentityDecision)
            WHERE f.invalidated_at IS NULL AND b.owner_id=$owner AND d.owner_id=$owner
            OPTIONAL MATCH (b)-[:RESOLVES_TO]->(e:Entity)
            RETURN f.id AS factId,b.id AS bindingId,b.identity_fact_role AS role,
                   b.identity_observed_surface AS surface,d.id AS decisionId,e.id AS entityId
            ORDER BY factId,role,bindingId LIMIT 4097
            """,row=>new { factId=row["factId"].As<string>(),bindingId=row["bindingId"].As<string>(),
                role=row["role"].As<string>(),surface=row["surface"].As<string>(),decisionId=row["decisionId"].As<string>(),
                entityId=row["entityId"].As<string?>() });
        return new { decisions,relationships,bindings=bindings.Take(4096).ToArray(),bindingsTruncated=bindings.Count>4096 };
    }

    /// <summary>
    /// Live entities as the store keeps them (any arm): name, aliases and the messages each was extracted from. The
    /// head-to-head scores the old model's merges with this; SAME_AS links are counted, never treated as merges.
    /// </summary>
    public async Task<object> EntitySnapshotAsync()
    {
        await using var driver=GraphDatabase.Driver(_neo4j.Uri,AuthTokens.Basic(_neo4j.Username,_neo4j.Password));
        var entities=await RowsAsync(driver,"""
            MATCH (e:Entity {owner_id:$owner})
            WHERE e.merged_into IS NULL AND e.invalidated_at IS NULL
            OPTIONAL MATCH (e)-[:EXTRACTED_FROM]->(m:Message)
            WITH e, collect(DISTINCT m.content) AS linked
            OPTIONAL MATCH (s:Message) WHERE s.id IN coalesce(e.source_message_ids, [])
            WITH e, linked, collect(DISTINCT s.content) AS listed
            RETURN e.id AS id, e.name AS name, e.type AS type, coalesce(e.aliases, []) AS aliases,
                   [x IN linked + listed WHERE x IS NOT NULL] AS sources
            ORDER BY id LIMIT 2000
            """,row=>new { id=row["id"].As<string>(),name=row["name"].As<string>(),type=row["type"].As<string>(),
                aliases=row["aliases"].As<List<string>>(),sources=row["sources"].As<List<string>>().Distinct(StringComparer.Ordinal).ToArray() });
        var sameAs=await RowsAsync(driver,"MATCH (a:Entity {owner_id:$owner})-[r:SAME_AS]->(b:Entity) RETURN a.id AS a, b.id AS b ORDER BY a, b",
            row=>new { a=row["a"].As<string>(),b=row["b"].As<string>() });
        return new { entities,sameAs };
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync().ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
    }

    private static string Describe(Fact fact)
    {
        var when = fact.OccurredOn is { } on ? $" [on {on:yyyy-MM-dd}]"
            : fact.ValidFrom is { } from ? $" [{from:yyyy-MM-dd} → {(fact.ValidUntil is { } until ? until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "…")}]"
            : "";
        return $"{fact.Subject} · {fact.Predicate} · {fact.Object}{when}";
    }

    private static DateOnly? Day(object? value) =>
        value is string text && text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    private async Task<IReadOnlyList<T>> RowsAsync<T>(IDriver driver, string cypher, Func<IRecord, T> read)
    {
        var result = await driver.ExecutableQuery(cypher).WithParameters(new { owner = Owner }).ExecuteAsync().ConfigureAwait(false);
        return result.Result.Select(read).ToList();
    }
}

internal sealed record GraphSnapshot(
    IReadOnlyList<StoredFact> Facts,
    IReadOnlyList<string> Closed,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> Relationships,
    IReadOnlyList<string> Preferences);

internal sealed record EventDetailProbe(IReadOnlyList<string> DatedSeeds, IReadOnlyList<EventDetailFact> UndatedDetails, IReadOnlyList<string> SeedFactIds);
