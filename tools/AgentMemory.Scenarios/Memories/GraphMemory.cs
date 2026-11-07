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
    Action<MemoryGateOptions>? Gate)
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
        GraphArm arm, IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embeddings, IClock clock,
        Neo4jTarget neo4j, string owner, bool writes, ILoggerProvider log, CancellationToken cancellationToken = default)
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
            },
            arm.Extraction);
        services.AddAgentMemoryFramework(o =>
        {
            arm.Framework(o);
            // Inline, so each statement is learned at the moment the script says it was said.
            o.ExtractInBackground = false;
        });
        if (arm.Gate is { } gate)
        {
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

    /// <summary>What the store holds for this owner, read straight from Neo4j.</summary>
    public async Task<GraphSnapshot> SnapshotAsync()
    {
        await using var driver = GraphDatabase.Driver(_neo4j.Uri, AuthTokens.Basic(_neo4j.Username, _neo4j.Password));

        var facts = await RowsAsync(driver, """
            MATCH (f:Fact {owner_id: $owner})
            WHERE f.invalidated_at IS NULL
            RETURN f.subject AS subject, f.predicate AS predicate, f.object AS object, f.category AS category,
                   toString(f.occurred_on) AS occurred, toString(f.valid_from) AS validFrom, toString(f.valid_until) AS validUntil
            ORDER BY f.created_at
            """, row => new StoredFact(
                row["subject"].As<string>(), row["predicate"].As<string>(), row["object"].As<string>(),
                Day(row["occurred"]), Day(row["validFrom"]), Day(row["validUntil"]), row["category"].As<string?>())).ConfigureAwait(false);

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
