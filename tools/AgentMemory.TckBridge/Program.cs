using System.Text.Json;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.TckBridge;
using Microsoft.Extensions.AI;
using Neo4j.Driver;

var builder = WebApplication.CreateBuilder(args);

// ---- Config conventions (mirror the CLI / McpHost samples) ----
var neo4jUri = builder.Configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
var neo4jUsername = builder.Configuration["Neo4j:Username"] ?? "neo4j";
var neo4jPassword = builder.Configuration["Neo4j:Password"] ?? "password";
var neo4jDatabase = builder.Configuration["Neo4j:Database"] ?? "neo4j";
var embeddingDimensions = int.TryParse(builder.Configuration["EmbeddingDimensions"], out var configuredDims)
    ? configuredDims
    : 1536;

// Default listen URL http://localhost:3001 — but let an explicit ASPNETCORE_URLS (env var or any other
// config source, e.g. appsettings/--urls) win rather than clobbering it.
var explicitUrlsConfigured =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) ||
    !string.IsNullOrWhiteSpace(builder.Configuration["urls"]);
if (!explicitUrlsConfigured)
{
    builder.WebHost.UseUrls("http://localhost:3001");
}

// ---- Services (meta-package overload, same as TckMirroredBehaviorTests) ----
builder.Services.AddNeo4jAgentMemory(
    configureMemory: _ => { },
    configureNeo4j: o =>
    {
        o.Uri = neo4jUri;
        o.Username = neo4jUsername;
        o.Password = neo4jPassword;
        o.Database = neo4jDatabase;
        o.EmbeddingDimensions = embeddingDimensions;
    });

// Neither AddAgentMemoryCore nor the Neo4j infrastructure registration TryAdds an
// IEmbeddingGenerator<string, Embedding<float>> (confirmed by reading both — the Neo4j
// ServiceCollectionExtensions.AddNeo4jAgentMemory and AgentMemory.Core's AddAgentMemoryCore never
// touch that service type), so this explicit registration is required, not a double-register risk.
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), embeddingDimensions));

builder.Services.AddSingleton<BridgeAdmin>();

// Wire contract is snake_case; ASP.NET defaults to camelCase. Do NOT globally ignore nulls —
// embedding:null and title:null are legitimate response values the TCK asserts on.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// ---- Bronze endpoints ----

app.MapPost("/setup", async (ISchemaBootstrapper bootstrapper, INeo4jSessionFactory sessionFactory, CancellationToken ct) =>
{
    await bootstrapper.BootstrapAsync(ct).ConfigureAwait(false);
    await WaitForVectorIndexesOnlineAsync(sessionFactory, ct).ConfigureAwait(false);
    // Bridge protocol: /setup returns {"ok": true} (the runner reads result.get("ok", True)); matches
    // the upstream C# reference conformance server's shape.
    return Results.Ok(new { ok = true });
});

app.MapPost("/teardown", async (BridgeAdmin admin, CancellationToken ct) =>
{
    await admin.WipeAllDataAsync(ct).ConfigureAwait(false);
    return Results.NoContent();
});

app.MapPost("/clear_all_data", async (BridgeAdmin admin, CancellationToken ct) =>
{
    await admin.WipeAllDataAsync(ct).ConfigureAwait(false);
    return Results.NoContent();
});

app.MapPost("/add_message", async (
    AddMessageRequest req,
    IConversationRepository conversationRepo,
    IShortTermMemoryService shortTerm,
    IIdGenerator idGenerator,
    IClock clock,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CancellationToken ct) =>
{
    // Upstream auto-creates/reuses one conversation per session (Bronze assumption); the server
    // assigns both the conversation id (on first message) and the message id + timestamp.
    var conversations = await conversationRepo.GetBySessionAsync(req.SessionId, ct).ConfigureAwait(false);
    var conversation = conversations.Count > 0 ? conversations[0] : null;
    string conversationId;
    if (conversation is null)
    {
        conversationId = idGenerator.GenerateId();
        await shortTerm.AddConversationAsync(conversationId, req.SessionId, userId: null, metadata: null, ct)
            .ConfigureAwait(false);
    }
    else
    {
        conversationId = conversation.ConversationId;
    }

    var embedding = await EmbedTextAsync(embeddingGenerator, req.Content, ct).ConfigureAwait(false);
    var message = new Message
    {
        MessageId = idGenerator.GenerateId(),
        ConversationId = conversationId,
        SessionId = req.SessionId,
        Role = req.Role,
        Content = req.Content,
        TimestampUtc = clock.UtcNow,
        Embedding = embedding,
        Metadata = req.Metadata ?? new Dictionary<string, object>(),
    };
    var saved = await shortTerm.AddMessageAsync(message, ct).ConfigureAwait(false);
    return Results.Ok(ToDto(saved));
});

app.MapPost("/get_conversation", async (
    GetConversationRequest req,
    IConversationRepository conversationRepo,
    IShortTermMemoryService shortTerm,
    CancellationToken ct) =>
{
    var conversations = await conversationRepo.GetBySessionAsync(req.SessionId, ct).ConfigureAwait(false);
    var conversation = conversations.Count > 0 ? conversations[0] : null;

    // Chronological (oldest-first), no cap — NOT GetRecentMessagesAsync, which is newest-first.
    IReadOnlyList<Message> messages = await shortTerm.GetAllSessionMessagesAsync(req.SessionId, ct)
        .ConfigureAwait(false);
    if (req.Limit is int limit)
        messages = messages.Take(limit).ToList();

    // JUDGMENT CALL: unknown/no-conversation session returns an empty-messages envelope (200), not a
    // 404 — SCN-B-045 ("returns empty for non-existent session") implies this shape. The runner parses
    // the envelope id through UUID(...) (tck _conversation_from_dict), and TCK session ids are not UUIDs
    // (fixture: f"tck-{uuid4()}"), so fall back to the nil UUID (not the raw session id) when there is no
    // backing Conversation node yet. Timestamps fall back to DateTimeOffset default in that case.
    var dto = new TckConversation(
        Id: conversation?.ConversationId ?? Guid.Empty.ToString(),
        SessionId: req.SessionId,
        Messages: messages.Select(ToDto).ToList(),
        Title: conversation?.Title,
        CreatedAt: conversation?.CreatedAtUtc ?? default,
        UpdatedAt: conversation?.UpdatedAtUtc ?? default);
    return Results.Ok(dto);
});

app.MapPost("/search_messages", async (
    SearchMessagesRequest req,
    IShortTermMemoryService shortTerm,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CancellationToken ct) =>
{
    var embedding = await EmbedTextAsync(embeddingGenerator, req.Query, ct).ConfigureAwait(false);
    var limit = req.Limit ?? 10;
    var minScore = req.Threshold ?? 0.7;
    var results = await shortTerm.SearchMessagesAsync(req.SessionId, embedding, limit, minScore, ct)
        .ConfigureAwait(false);
    return Results.Ok(results.Select(ToDto).ToList());
});

app.MapPost("/list_sessions", async (
    ListSessionsRequest req,
    IConversationRepository conversationRepo,
    CancellationToken ct) =>
{
    var limit = req.Limit ?? 100;
    var sessions = await conversationRepo.ListSessionsAsync(limit, ct).ConfigureAwait(false);
    return Results.Ok(sessions.Select(ToSessionDto).ToList());
});

app.MapPost("/delete_message", async (
    DeleteMessageRequest req,
    IMessageRepository messageRepo,
    CancellationToken ct) =>
{
    // JUDGMENT CALL: base_adapter.delete_message returns a bare bool upstream, while
    // bridge-protocol.adoc names a "deleted" field. No reference bridge server was available to
    // confirm live; defaulting to the wrapped {"deleted": bool} shape per the plan's guidance
    // (a superset of the bare-bool contract is the safer default — trivially unwrapped by a caller
    // that only expects a bool, whereas the reverse is not true).
    // The TCK client round-trips ids through Python's UUID(), which re-emits them in canonical dashed
    // form, while IIdGenerator stores them as unhyphenated 32-char hex ("N" format). Normalize the
    // incoming id to the stored format so the lookup matches regardless of hyphenation.
    var messageId = Guid.TryParse(req.MessageId, out var parsed) ? parsed.ToString("N") : req.MessageId;
    var deleted = await messageRepo.DeleteAsync(messageId, cascade: true, ct).ConfigureAwait(false);
    return Results.Ok(new { deleted });
});

app.MapPost("/clear_session", async (
    ClearSessionRequest req,
    IShortTermMemoryService shortTerm,
    CancellationToken ct) =>
{
    // Owner-agnostic by design: messages/conversations carry no owner_id in .NET (only reasoning
    // traces do), so ownerId stays null rather than weakening owner scoping to satisfy an upstream
    // assumption that has no .NET equivalent (see plan §5, intentional divergence).
    await shortTerm.ClearSessionAsync(req.SessionId, ownerId: null, ct).ConfigureAwait(false);
    return Results.NoContent();
});

// ---- Long-term memory (Bronze schema tier: add_entity / add_preference / add_fact) ----
// The Bronze marker covers "schema and short-term memory"; the schema tests create long-term records
// and assert their round-tripped shape. Confidence defaults to 1.0 (the TCK never supplies it), and the
// text is embedded via the deterministic stub so the records are also vector-searchable.

app.MapPost("/add_entity", async (
    AddEntityRequest req,
    ILongTermMemoryService longTerm,
    IIdGenerator idGenerator,
    IClock clock,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CancellationToken ct) =>
{
    var embedding = await EmbedTextAsync(embeddingGenerator, $"{req.Name} {req.Description}".Trim(), ct).ConfigureAwait(false);
    var entity = await longTerm.AddEntityAsync(new Entity
    {
        EntityId = idGenerator.GenerateId(),
        Name = req.Name,
        Type = req.EntityType,
        Description = req.Description,
        Confidence = 1.0,
        Embedding = embedding,
        CreatedAtUtc = clock.UtcNow,
    }, ct).ConfigureAwait(false);
    return Results.Ok(new TckEntity(
        entity.EntityId, entity.Name, entity.Type, entity.Subtype, entity.Description,
        entity.Embedding, entity.CanonicalName, entity.CreatedAtUtc));
});

app.MapPost("/add_preference", async (
    AddPreferenceRequest req,
    ILongTermMemoryService longTerm,
    IIdGenerator idGenerator,
    IClock clock,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CancellationToken ct) =>
{
    var embedding = await EmbedTextAsync(embeddingGenerator, req.Preference, ct).ConfigureAwait(false);
    var preference = await longTerm.AddPreferenceAsync(new Preference
    {
        PreferenceId = idGenerator.GenerateId(),
        Category = req.Category,
        PreferenceText = req.Preference,
        Context = req.Context,
        Confidence = 1.0,
        Embedding = embedding,
        CreatedAtUtc = clock.UtcNow,
    }, ct).ConfigureAwait(false);
    return Results.Ok(new TckPreference(
        preference.PreferenceId, preference.Category, preference.PreferenceText,
        preference.Context, preference.Embedding));
});

app.MapPost("/add_fact", async (
    AddFactRequest req,
    ILongTermMemoryService longTerm,
    IIdGenerator idGenerator,
    IClock clock,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CancellationToken ct) =>
{
    var embedding = await EmbedTextAsync(embeddingGenerator, $"{req.Subject} {req.Predicate} {req.Obj}", ct).ConfigureAwait(false);
    var fact = await longTerm.AddFactAsync(new Fact
    {
        FactId = idGenerator.GenerateId(),
        Subject = req.Subject,
        Predicate = req.Predicate,
        Object = req.Obj,
        Confidence = 1.0,
        Embedding = embedding,
        CreatedAtUtc = clock.UtcNow,
    }, ct).ConfigureAwait(false);
    return Results.Ok(new TckFact(fact.FactId, fact.Subject, fact.Predicate, fact.Object, fact.Embedding));
});

app.Run();

// ---- Mapping helpers ----

static TckMessage ToDto(Message m) =>
    new(m.MessageId, m.Role, m.Content, m.TimestampUtc, m.Embedding, m.Metadata);

static TckSessionInfo ToSessionDto(SessionSummary s) =>
    // SessionSummary has no distinct created_at; map both timestamps from LastActivity (best available).
    // The TCK only asserts created_at is present (SPEC-2.4.6) and that message_count is accurate.
    new(s.SessionId, s.MessageCount, s.LastActivity, s.LastActivity);

// Mirrors the single-string embedding call shape used by AgentMemory.Core.Services.EmbeddingOrchestrator
// (GenerateAsync with a one-element input, then unwrap the sole result's vector) so the bridge's embedding
// invocation matches the codebase's canonical MEAI usage rather than inventing a parallel call shape.
static async Task<float[]> EmbedTextAsync(
    IEmbeddingGenerator<string, Embedding<float>> generator, string text, CancellationToken ct)
{
    var generated = await generator.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
    return generated[0].Vector.ToArray();
}

// Polls SHOW INDEXES until every VECTOR index reports ONLINE, mirroring
// Neo4jIntegrationFixture.WaitForVectorIndexesAsync (vector index population is asynchronous in Neo4j).
// Bounded so a stuck server can't hang /setup forever.
static async Task WaitForVectorIndexesOnlineAsync(
    INeo4jSessionFactory sessionFactory, CancellationToken ct, int timeoutSeconds = 30)
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
    var token = timeoutCts.Token;
    while (true)
    {
        // Caller cancellation always propagates; the bounded timeout just ends the wait (best-effort —
        // schema is already bootstrapped, indexes finish coming online shortly after).
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var session = sessionFactory.OpenSession(AccessMode.Read);
            // NOTE: SHOW INDEXES needs an explicit YIELD before a WHERE/RETURN can reference its columns —
            // "SHOW INDEXES WHERE ... RETURN ..." is a syntax error in Neo4j 5.x, which the catch below would
            // swallow and turn into a full-timeout busy-loop. Keep the YIELD.
            var result = await session.RunAsync(
                "SHOW INDEXES YIELD type, state WHERE type = 'VECTOR' AND state <> 'ONLINE' RETURN count(*) AS pending")
                .ConfigureAwait(false);
            // Pass the bounded token so a stalled driver call cannot run past the timeout.
            var record = await result.SingleAsync(token).ConfigureAwait(false);
            if (record["pending"].As<long>() == 0) return;
        }
        catch (OperationCanceledException)
        {
            // Timeout elapsed (or the caller cancelled) mid-query; stop waiting. Re-throw only genuine
            // caller cancellation — the bounded timeout is best-effort, not a failure of /setup.
            break;
        }
        catch
        {
            // Neo4j may not be fully ready yet (e.g. indexes still being created); keep polling
            // until the bounded token trips.
        }

        try
        {
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    ct.ThrowIfCancellationRequested();
}

// Exposed so a future WebApplicationFactory<Program>-based integration test (plan §4, Part C item 3)
// can host this bridge in-process without a separate process.
public partial class Program { }
