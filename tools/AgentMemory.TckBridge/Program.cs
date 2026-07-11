using System.Text.Json;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.TckBridge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

// Register the deterministic StubEmbeddingGenerator as a FALLBACK (TryAdd, matching
// tools/AgentMemory.Cli): a host running the bridge against a real environment can supply its own
// IEmbeddingGenerator<string, Embedding<float>> (registered before this point) and it wins, honouring the
// README's "unless a real provider is wired in" contract. Nothing in AddNeo4jAgentMemory /
// AddAgentMemoryCore registers this service, so TryAdd installs the deterministic stub by default.
builder.Services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
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

    // Unknown/no-conversation session returns an empty-messages envelope (200), not a 404 — the confirmed
    // contract (SCN-B-045 "returns empty for non-existent session", which passes in the conformance run).
    // The runner parses the envelope id through UUID(...) (tck _conversation_from_dict) and TCK session ids
    // are not UUIDs (fixture: f"tck-{uuid4()}"), so fall back to the nil UUID (not the raw session id) when
    // there is no backing Conversation node yet. Timestamps fall back to DateTimeOffset default in that case.
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
    // delete_message returns {"deleted": bool} — the confirmed contract: bridge-protocol.adoc's wire
    // spec names a "deleted" field and the TCK runner reads result.get("deleted", False)
    // (tck/adapters/http_bridge.py), verified by the passing Bronze conformance run.
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

// Shared-bucket scope for the long-term READ endpoints: a null scope is UNSCOPED (all owners), which would
// leak cross-owner records on a multi-tenant store. There is no built-in "shared only" scope (Global is
// unscoped), so use a sentinel owner + includeShared: the filter becomes (owner_id = <sentinel> OR owner_id
// IS NULL) and, since nothing is ever created under the sentinel, effectively "owner_id IS NULL" — the same
// shared-bucket the bridge writes into, consistent with /list_traces and /get_tool_stats.
var sharedScope = MemoryScope.For("__tck-bridge-shared-sentinel__", includeShared: true);

app.MapPost("/search_entities", async (
    SearchEntitiesRequest req,
    ILongTermMemoryService longTerm,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CancellationToken ct) =>
{
    var embedding = await EmbedTextAsync(embeddingGenerator, req.Query, ct).ConfigureAwait(false);
    var limit = req.Limit ?? 10;
    var results = await longTerm.SearchEntitiesAsync(embedding, limit, minScore: 0.0, scope: sharedScope, ct)
        .ConfigureAwait(false);
    return Results.Ok(results.Select(ToEntityDto).ToList());
});

app.MapPost("/search_preferences", async (
    SearchPreferencesRequest req,
    ILongTermMemoryService longTerm,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CancellationToken ct) =>
{
    var embedding = await EmbedTextAsync(embeddingGenerator, req.Query, ct).ConfigureAwait(false);
    var limit = req.Limit ?? 10;
    var results = await longTerm.SearchPreferencesAsync(embedding, limit, minScore: 0.0, scope: sharedScope, ct)
        .ConfigureAwait(false);
    // Category is applied as a post-search filter (not a separate category-only lookup) so /search_preferences
    // stays a single semantic-search call, matching the bridge-protocol shape (query + optional category).
    if (!string.IsNullOrEmpty(req.Category))
        results = results.Where(p => p.Category == req.Category).ToList();
    return Results.Ok(results.Select(ToPreferenceDto).ToList());
});

app.MapPost("/get_entity_by_name", async (
    GetEntityByNameRequest req,
    ILongTermMemoryService longTerm,
    CancellationToken ct) =>
{
    var entities = await longTerm.GetEntitiesByNameAsync(req.Name, includeAliases: true, scope: sharedScope, ct)
        .ConfigureAwait(false);
    // TCK contract: Entity or null (SPEC-3.6.2). GetEntitiesByNameAsync can return several rows (exact-name
    // plus alias matches) and its Cypher defines no order, so entities[0] would be nondeterministic when
    // duplicates exist. Pick deterministically: an exact Name match wins, then tie-break on EntityId.
    var match = entities
        .OrderByDescending(e => string.Equals(e.Name, req.Name, StringComparison.Ordinal))
        .ThenBy(e => e.EntityId, StringComparer.Ordinal)
        .FirstOrDefault();
    TckEntity? dto = match is not null ? ToEntityDto(match) : null;
    return Results.Ok(dto);
});

app.MapPost("/get_related_entities", async (
    GetRelatedEntitiesRequest req,
    ILongTermMemoryService longTerm,
    IEntityRepository entityRepo,
    CancellationToken ct) =>
{
    // ILongTermMemoryService has no "related entities" method — GetEntityRelationshipsAsync (the closest
    // graph-traversal primitive) returns Relationship edges (source/target ids), not the entities themselves,
    // so each hop's related ids are resolved individually via IEntityRepository.GetByIdAsync. depth > 1 is
    // handled as a genuine BFS expansion (not just single-hop): each level's discovered ids become the next
    // level's frontier, so multi-hop traversal is real, not a stub.
    var entityId = NormalizeId(req.EntityId);
    var depth = req.Depth is int d && d > 0 ? d : 1;
    var frontier = new HashSet<string> { entityId };
    var visited = new HashSet<string> { entityId };
    var relatedIds = new List<string>();

    for (var hop = 0; hop < depth && frontier.Count > 0; hop++)
    {
        var nextFrontier = new HashSet<string>();
        foreach (var id in frontier)
        {
            var relationships = await longTerm.GetEntityRelationshipsAsync(id, scope: sharedScope, ct).ConfigureAwait(false);
            foreach (var rel in relationships)
            {
                if (req.RelationshipType is { Length: > 0 } && rel.RelationshipType != req.RelationshipType)
                    continue;
                var otherId = rel.SourceEntityId == id ? rel.TargetEntityId : rel.SourceEntityId;
                if (visited.Add(otherId))
                {
                    relatedIds.Add(otherId);
                    nextFrontier.Add(otherId);
                }
            }
        }
        frontier = nextFrontier;
    }

    var related = new List<Entity>(relatedIds.Count);
    foreach (var id in relatedIds)
    {
        var entity = await entityRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        // GetByIdAsync is unscoped; keep only shared-bucket entities so a shared relationship that happens to
        // point at an owned entity can't leak it (defense-in-depth alongside the shared-scope traversal above
        // and the add_relationship guard below).
        if (entity is { OwnerId: null }) related.Add(entity);
    }
    return Results.Ok(related.Select(ToEntityDto).ToList());
});

// add_relationship is Gold-tier in bridge-protocol.adoc, but the Silver get_related_entities tests call it
// to set up their fixture data, so the bridge serves it. Normalize the entity ids to stored ("N") form —
// the TCK round-trips them through Python UUID() into dashed form — before creating the edge, so it matches
// the same-normalized ids get_related_entities traverses.
app.MapPost("/add_relationship", async (
    AddRelationshipRequest req,
    ILongTermMemoryService longTerm,
    IEntityRepository entityRepo,
    IIdGenerator idGenerator,
    IClock clock,
    CancellationToken ct) =>
{
    var sourceId = NormalizeId(req.SourceId);
    var targetId = NormalizeId(req.TargetId);
    // Owner-isolation guard: the entity MERGE-by-id doesn't owner-filter, so an unguarded shared edge could
    // attach to another owner's private entity and then be leaked through get_related_entities. Reject if
    // either endpoint already exists and is NOT shared.
    foreach (var (label, id) in new[] { ("source_id", sourceId), ("target_id", targetId) })
    {
        var existing = await entityRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (existing is { OwnerId: not null })
            return Results.BadRequest(new { error = $"{label} references a non-shared entity" });
    }
    var relationship = await longTerm.AddRelationshipAsync(new Relationship
    {
        RelationshipId = idGenerator.GenerateId(),
        SourceEntityId = sourceId,
        TargetEntityId = targetId,
        RelationshipType = req.RelationshipType,
        Confidence = 1.0,
        Attributes = req.Properties ?? new Dictionary<string, object>(),
        CreatedAtUtc = clock.UtcNow,
    }, ct).ConfigureAwait(false);
    return Results.Ok(new TckRelationship(
        relationship.RelationshipId, relationship.SourceEntityId, relationship.TargetEntityId,
        relationship.RelationshipType, relationship.Attributes));
});

// ---- Reasoning memory (Silver tier: start_trace / add_step / record_tool_call / complete_trace /
// get_trace_with_steps / list_traces / get_tool_stats) ----

app.MapPost("/start_trace", async (
    StartTraceRequest req,
    IReasoningMemoryService reasoning,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    CancellationToken ct) =>
{
    // Bridge embeds the task explicitly (via the injected IEmbeddingGenerator) rather than relying on
    // ReasoningMemoryService's own auto-embedding, mirroring add_entity/add_preference/add_fact's pattern
    // above — the bridge only has access to the public IEmbeddingGenerator, not the internal orchestrator.
    var embedding = await EmbedTextAsync(embeddingGenerator, req.Task, ct).ConfigureAwait(false);
    var trace = await reasoning.StartTraceAsync(
        req.SessionId, req.Task, taskEmbedding: embedding, ownerId: null, cancellationToken: ct)
        .ConfigureAwait(false);
    return Results.Ok(ToTraceDto(trace, Array.Empty<TckReasoningStep>()));
});

// step_number is derived as "current count + 1", a read-then-write that is not atomic, so add_step is
// serialized. A single process-wide gate (rather than a per-trace-id dictionary) both fixes the race and
// avoids an unbounded map of trace-id -> SemaphoreSlim that would never be reclaimed in a long-running
// process; for a sequential conformance bridge the extra serialization across traces is immaterial.
var addStepGate = new SemaphoreSlim(1, 1);

app.MapPost("/add_step", async (
    AddStepRequest req,
    IReasoningMemoryService reasoning,
    CancellationToken ct) =>
{
    // AddStepAsync requires a caller-supplied stepNumber, but the TCK's add_step contract has no such
    // parameter (bridge-protocol.adoc: trace_id, thought?, action?, observation? only) — there is no
    // "peek next step number" primitive either, so the next number is derived the same way a caller would
    // discover it: read the trace's current step count and add one. Costs one extra read per add_step call.
    var traceId = NormalizeId(req.TraceId);
    await addStepGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        var (trace, existingSteps) = await reasoning.GetTraceWithStepsAsync(traceId, ct).ConfigureAwait(false);
        // Shared-bucket only: trace ids come from the caller, so refuse to mutate a non-shared (owner-scoped)
        // trace by id — treat it as not found, consistent with get_trace_with_steps.
        if (trace.OwnerId is not null)
            return Results.NotFound(new { error = "trace not found" });
        var stepNumber = existingSteps.Count + 1;
        var step = await reasoning.AddStepAsync(
            traceId, stepNumber, req.Thought, req.Action, req.Observation, cancellationToken: ct)
            .ConfigureAwait(false);
        return Results.Ok(ToStepDto(step, Array.Empty<ToolCall>()));
    }
    finally
    {
        addStepGate.Release();
    }
});

app.MapPost("/record_tool_call", async (
    RecordToolCallRequest req,
    IReasoningMemoryService reasoning,
    CancellationToken ct) =>
{
    // RecordToolCallAsync's own default is ToolCallStatus.Pending, but the TCK's BaseAdapter contract default
    // is SUCCESS (record_tool_call(..., status: ToolCallStatus = ToolCallStatus.SUCCESS)); the TCK's HTTP
    // client always serializes its resolved default onto the wire (http_bridge.py's _serialize only omits
    // None, and SUCCESS is never None), so in practice "status" arrives on every call — this fallback only
    // matters for a caller hitting the bridge directly without going through the TCK client.
    ToolCallStatus status;
    if (req.Status is { Length: > 0 })
    {
        // TryParse (not Parse) so a malformed status from a direct caller is a 400, not a 500.
        if (!Enum.TryParse(req.Status, ignoreCase: true, out status))
            return Results.BadRequest(new { error = $"invalid tool call status '{req.Status}'" });
    }
    else
    {
        status = ToolCallStatus.Success;
    }
    // A direct (non-TCK) caller may omit "arguments"; the non-nullable JsonElement then binds as
    // ValueKind.Undefined, whose GetRawText() throws — default it to an empty object. Same for a
    // null/undefined result.
    var argumentsJson = req.Arguments.ValueKind == JsonValueKind.Undefined ? "{}" : req.Arguments.GetRawText();
    var resultJson = req.Result is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } r
        ? r.GetRawText()
        : null;
    var toolCall = await reasoning.RecordToolCallAsync(
        NormalizeId(req.StepId), req.ToolName, argumentsJson, resultJson, status, req.DurationMs, req.Error,
        cancellationToken: ct)
        .ConfigureAwait(false);
    return Results.Ok(ToToolCallDto(toolCall));
});

app.MapPost("/complete_trace", async (
    CompleteTraceRequest req,
    IReasoningMemoryService reasoning,
    CancellationToken ct) =>
{
    var trace = await reasoning.CompleteTraceAsync(NormalizeId(req.TraceId), req.Outcome, req.Success, ct)
        .ConfigureAwait(false);
    return Results.Ok(ToTraceDto(trace, Array.Empty<TckReasoningStep>()));
});

app.MapPost("/get_trace_with_steps", async (
    GetTraceWithStepsRequest req,
    IReasoningTraceRepository traceRepo,
    IReasoningStepRepository stepRepo,
    IToolCallRepository toolCallRepo,
    CancellationToken ct) =>
{
    // Uses the repositories directly (not IReasoningMemoryService.GetTraceWithStepsAsync, which throws
    // TraceNotFound) so a nonexistent trace_id can return the TCK-mandated 200 null (SPEC-4.5.3) rather than
    // a 500 — the same null-on-not-found convention as Bronze's /get_entity_by_name-style lookups.
    var traceId = NormalizeId(req.TraceId);
    var trace = await traceRepo.GetByIdAsync(traceId, ct).ConfigureAwait(false);
    TckReasoningTrace? dto = null;
    // Shared-bucket only: a non-shared (owner-scoped) trace is treated as not-found (200 null) so a known/
    // guessed private trace id can't be read through the owner-agnostic bridge.
    if (trace is { OwnerId: null })
    {
        var steps = await stepRepo.GetByTraceAsync(traceId, ct).ConfigureAwait(false);
        var stepDtos = new List<TckReasoningStep>(steps.Count);
        foreach (var step in steps)
        {
            var toolCalls = await toolCallRepo.GetByStepAsync(step.StepId, ct).ConfigureAwait(false);
            stepDtos.Add(ToStepDto(step, toolCalls));
        }
        dto = ToTraceDto(trace, stepDtos);
    }
    return Results.Ok(dto);
});

app.MapPost("/list_traces", async (
    ListTracesRequest req,
    IReasoningMemoryService reasoning,
    INeo4jSessionFactory sessionFactory,
    CancellationToken ct) =>
{
    var limit = req.Limit ?? 100;
    IReadOnlyList<ReasoningTrace> traces;
    if (!string.IsNullOrEmpty(req.SessionId))
    {
        traces = await reasoning.ListTracesAsync(req.SessionId, limit, scope: sharedScope, ct).ConfigureAwait(false);
    }
    else
    {
        // Judgment call (openQuestions): neither IReasoningMemoryService.ListTracesAsync nor
        // IReasoningTraceRepository can list traces across every session — both require a session_id — yet
        // SPEC-4.6.1 (list_traces with no session_id) requires exactly "all traces, any session". Read-only
        // raw Cypher against the same ReasoningTrace schema Neo4jReasoningTraceRepository maintains, mirroring
        // BridgeAdmin's own "no high-level service for X, so run raw Cypher" precedent — filtered to the
        // shared/global bucket (owner_id IS NULL) to match this bridge's owner-agnostic design (see
        // /clear_session's comment), so this fallback can never surface another owner's private trace.
        await using var session = sessionFactory.OpenSession(AccessMode.Read);
        var cursor = await session.RunAsync(
            "MATCH (t:ReasoningTrace) WHERE t.owner_id IS NULL RETURN t ORDER BY t.started_at DESC LIMIT $limit",
            new { limit }).ConfigureAwait(false);
        var records = await cursor.ToListAsync(ct).ConfigureAwait(false);
        traces = records.Select(r => MapTraceNode(r["t"].As<INode>())).ToList();
    }
    return Results.Ok(traces.Select(t => ToTraceDto(t, Array.Empty<TckReasoningStep>())).ToList());
});

app.MapPost("/get_tool_stats", async (
    GetToolStatsRequest req,
    INeo4jSessionFactory sessionFactory,
    CancellationToken ct) =>
{
    // No IToolCallRepository/service exposes tool-usage aggregates, so this is read-only raw Cypher
    // (mirroring BridgeAdmin's "no high-level service for X" precedent). We deliberately do NOT read the
    // global (:Tool) aggregate node — it accumulates across ALL owners and carries no owner_id, so on a
    // multi-owner store it would leak cross-owner usage stats. Instead we aggregate ToolCall nodes reached
    // only through shared-bucket traces (owner_id IS NULL), keeping the bridge owner-agnostic and consistent
    // with /list_traces. Success/failure classification matches ToolCallQueries.UpsertToolInstance
    // (failed = error/failure/timeout).
    await using var session = sessionFactory.OpenSession(AccessMode.Read);
    var cursor = await session.RunAsync(
        "MATCH (t:ReasoningTrace)-[:HAS_STEP]->(:ReasoningStep)-[:USES_TOOL]->(tc:ToolCall) " +
        "WHERE t.owner_id IS NULL AND ($toolName IS NULL OR tc.tool_name = $toolName) " +
        "WITH tc.tool_name AS name, count(tc) AS total_calls, " +
        "sum(CASE WHEN tc.status = 'success' THEN 1 ELSE 0 END) AS successful_calls, " +
        "sum(CASE WHEN tc.status IN ['error', 'failure', 'timeout'] THEN 1 ELSE 0 END) AS failed_calls, " +
        "sum(coalesce(tc.duration_ms, 0)) AS total_duration_ms " +
        "RETURN name, total_calls, successful_calls, failed_calls, total_duration_ms ORDER BY name",
        new { toolName = (object?)req.ToolName }).ConfigureAwait(false);
    var records = await cursor.ToListAsync(ct).ConfigureAwait(false);
    var stats = records.Select(r =>
    {
        var totalCalls = checked((int)r["total_calls"].As<long>());
        var totalDurationMs = r["total_duration_ms"].As<long?>() ?? 0;
        return new TckToolStats(
            r["name"].As<string>(),
            totalCalls,
            checked((int)r["successful_calls"].As<long>()),
            checked((int)r["failed_calls"].As<long>()),
            totalCalls > 0 ? (double)r["successful_calls"].As<long>() / totalCalls : 0.0,
            totalCalls > 0 ? (double)totalDurationMs / totalCalls : null);
    }).ToList();
    return Results.Ok(stats);
});

app.Run();

// ---- Mapping helpers ----

static TckMessage ToDto(Message m) =>
    new(m.MessageId, m.Role, m.Content, m.TimestampUtc, m.Embedding, m.Metadata);

static TckSessionInfo ToSessionDto(SessionSummary s) =>
    // SessionSummary has no distinct created_at; map both timestamps from LastActivity (best available).
    // The TCK only asserts created_at is present (SPEC-2.4.6) and that message_count is accurate.
    new(s.SessionId, s.MessageCount, s.LastActivity, s.LastActivity);

// The TCK round-trips every id this bridge mints through Python's UUID(...), which re-emits it in canonical
// dashed form on the next request, while IIdGenerator (GuidIdGenerator) stores ids as unhyphenated 32-char
// hex ("N" format) — the same normalization /delete_message already applies to message_id. Silver adds
// several more id-shaped parameters (entity_id, trace_id, step_id) that need the same treatment before an
// exact-match lookup, so it is centralized here rather than re-inlined at each call site.
static string NormalizeId(string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : id;

// Distinct names, not ToDto overloads: C# local functions cannot be overloaded by parameter list (unlike
// ordinary methods, redeclaring the same local-function name with different parameters is CS0128 "already
// defined in this scope") — this bit Program.cs's existing top-level `static TckMessage ToDto(Message m)`
// local function the first time these were named `ToDto` too.
static TckEntity ToEntityDto(Entity e) =>
    new(e.EntityId, e.Name, e.Type, e.Subtype, e.Description, e.Embedding, e.CanonicalName, e.CreatedAtUtc);

static TckPreference ToPreferenceDto(Preference p) =>
    new(p.PreferenceId, p.Category, p.PreferenceText, p.Context, p.Embedding);

static TckReasoningTrace ToTraceDto(ReasoningTrace trace, IReadOnlyList<TckReasoningStep> steps) =>
    new(trace.TraceId, trace.SessionId, trace.Task, steps, trace.Outcome, trace.Success,
        trace.StartedAtUtc, trace.CompletedAtUtc);

static TckReasoningStep ToStepDto(ReasoningStep step, IReadOnlyList<ToolCall> toolCalls) =>
    new(step.StepId, step.TraceId, step.StepNumber, step.Thought, step.Action, step.Observation,
        toolCalls.Select(ToToolCallDto).ToList());

static TckToolCall ToToolCallDto(ToolCall tc) =>
    new(tc.ToolCallId, tc.ToolName, ParseJsonObject(tc.ArgumentsJson), ParseJsonOrNull(tc.ResultJson),
        tc.Status.ToString().ToLowerInvariant(), tc.DurationMs, tc.Error);

// ArgumentsJson is stored as a JSON string on the ToolCall domain record; re-parse it into a JsonElement so
// the wire form is a real JSON object (the TCK asserts `arguments == {...}`, a dict), not an escaped string.
// Falls back to an empty object for null/blank storage (mirrors the TCK model's own dict default_factory).
static JsonElement ParseJsonObject(string? json)
{
    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    return doc.RootElement.Clone();
}

// ResultJson is `Any = None` on the wire — absent/null stays null; anything else round-trips as a JsonElement
// (object, array, string, number, or bool) rather than a JSON-escaped string.
static JsonElement? ParseJsonOrNull(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.Clone();
}

// Duplicates AgentMemory.Neo4j.Infrastructure.Neo4jDateTimeHelper's tiny conversion logic: that helper is
// `internal` to AgentMemory.Neo4j (no InternalsVisibleTo for this bridge project), and is only needed here
// for the /list_traces "no session_id" raw-Cypher fallback (see MapTraceNode below).
static DateTimeOffset ReadTraceDateTimeOffset(object? value) => value switch
{
    ZonedDateTime zdt => zdt.ToDateTimeOffset(),
    string s => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind),
    null => DateTimeOffset.UtcNow,
    _ => throw new InvalidOperationException($"Unexpected datetime type: {value.GetType()}")
};

static DateTimeOffset? ReadTraceNullableDateTimeOffset(object? value) =>
    value is null ? null : ReadTraceDateTimeOffset(value);

// Maps a raw :ReasoningTrace node (from the /list_traces "all sessions" fallback query) the same way
// Neo4jReasoningTraceRepository.MapToTrace does, minus TaskEmbedding/Metadata/OwnerId (not needed on the
// TckReasoningTrace wire DTO, which only ever sets Steps to an empty list for /list_traces).
static ReasoningTrace MapTraceNode(INode node) => new()
{
    TraceId = node["id"].As<string>(),
    SessionId = node["session_id"].As<string>(),
    Task = node["task"].As<string>(),
    Outcome = node.Properties.TryGetValue("outcome", out var outcome) ? outcome.As<string>() : null,
    Success = node.Properties.TryGetValue("success", out var success) && success is not null
        ? success.As<bool?>()
        : null,
    StartedAtUtc = ReadTraceDateTimeOffset(node["started_at"]),
    CompletedAtUtc = node.Properties.TryGetValue("completed_at", out var completedAt)
        ? ReadTraceNullableDateTimeOffset(completedAt)
        : null,
};

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
