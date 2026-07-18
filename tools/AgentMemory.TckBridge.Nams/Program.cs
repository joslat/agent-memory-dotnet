using System.Text.Json;
using AgentMemory.Nams;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.TckBridge.Nams;

var builder = WebApplication.CreateBuilder(args);

// ---- Config conventions (mirrors AgentMemory.Sample.NamsAgent; NAMS_DEV_WORKSPACE_ID is also accepted
// since that's the name the live-integration-test credentials use -- see NamsLiveCredentials.cs). ----
var namsApiKey = builder.Configuration["NAMS_API_KEY"] ?? Environment.GetEnvironmentVariable("NAMS_API_KEY");
if (string.IsNullOrWhiteSpace(namsApiKey))
{
    Console.WriteLine("=== AgentMemory.TckBridge.Nams ===\n");
    Console.WriteLine("[!] NAMS is not configured. This bridge calls the real NAMS SaaS -- there is no");
    Console.WriteLine("    offline fallback. Set NAMS_API_KEY and re-run.");
    return;
}
var namsWorkspaceId =
    builder.Configuration["NAMS_WORKSPACE_ID"] ?? Environment.GetEnvironmentVariable("NAMS_WORKSPACE_ID") ??
    Environment.GetEnvironmentVariable("NAMS_DEV_WORKSPACE_ID");

// Default listen URL http://localhost:3002 -- distinct from the direct-backend AgentMemory.TckBridge's
// 3001 default, so both can run side by side. An explicit ASPNETCORE_URLS/--urls always wins.
var explicitUrlsConfigured =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) ||
    !string.IsNullOrWhiteSpace(builder.Configuration["urls"]);
if (!explicitUrlsConfigured)
{
    builder.WebHost.UseUrls("http://localhost:3002");
}

builder.Services.AddNamsAgentMemory(o =>
{
    o.Endpoint = NamsWellKnown.Endpoint;
    o.ApiKey = namsApiKey;
    o.WorkspaceId = namsWorkspaceId;
});

// Wire contract is snake_case; ASP.NET defaults to camelCase -- same convention as the direct-backend bridge.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// ---- Lifecycle: NAMS has no schema/bootstrap step and no bulk-wipe endpoint of any kind (checked the
// pinned OpenAPI snapshot) -- setup/teardown are no-ops; clear_all_data does best-effort cleanup (delete
// every conversation found via ListConversationsAsync). Entities/reasoning traces are NOT wiped -- every
// Platinum assertion is permissive enough that accumulated data across runs doesn't affect the outcome. ----

app.MapPost("/setup", () => Results.Ok(new { ok = true }));

app.MapPost("/teardown", async (INamsClient client, CancellationToken ct) =>
{
    await ClearAllConversationsAsync(client, ct).ConfigureAwait(false);
    return Results.NoContent();
});

app.MapPost("/clear_all_data", async (INamsClient client, CancellationToken ct) =>
{
    await ClearAllConversationsAsync(client, ct).ConfigureAwait(false);
    return Results.NoContent();
});

static async Task ClearAllConversationsAsync(INamsClient client, CancellationToken ct)
{
    // 200 is NAMS's own documented hard maximum for this endpoint's limit param (pinned OpenAPI snapshot:
    // "Max conversations to return (1-200, default 50)") -- there is no offset/cursor param to page beyond
    // it. A disclosed, known limitation (like the entities/reasoning-traces-not-wiped gap in the planning
    // doc): if the shared dev workspace ever exceeds 200 conversations, this cleanup only reaches the
    // newest 200 (confirmed live: list_conversations returns newest-first), leaving an undeleted tail.
    // Acceptable for the same reason as that other gap -- every Platinum assertion is permissive enough
    // that accumulated data doesn't affect the outcome.
    var conversations = await client.ListConversationsAsync(200, ct).ConfigureAwait(false);
    foreach (var conversation in conversations)
        await client.DeleteConversationAsync(conversation.Id, ct).ConfigureAwait(false);
}

// ---- Conversation lifecycle ----

app.MapPost("/create_conversation", async (CreateConversationRequest req, INamsClient client, CancellationToken ct) =>
{
    var conversation = await client.CreateConversationAsync(req.UserId, req.Metadata, ct).ConfigureAwait(false);
    // NAMS's create-response has no session_id (a conversation IS the session) or created_at at all --
    // synthesize both (confirmed live, Phase 10e/TCK bridge research). Title isn't a first-class field on
    // the create-response either -- NAMS reads it from metadata["title"] (confirmed, Phase 10f), same as
    // list_conversations echoes it back -- so echo the submitted metadata here too, for consistency.
    var title = conversation.Metadata is not null && conversation.Metadata.TryGetValue("title", out var t) ? t : null;
    return Results.Ok(new TckConversation(conversation.Id, conversation.Id, title, DateTimeOffset.UtcNow, null));
});

app.MapPost("/list_conversations", async (ListConversationsRequest req, INamsClient client, CancellationToken ct) =>
{
    var conversations = await client.ListConversationsAsync(req.Limit ?? 50, ct).ConfigureAwait(false);
    var dtos = conversations.Select(c => new TckConversation(
        c.Id, c.Id, c.Title,
        ParseOrUtcNow(c.CreatedAt),
        string.IsNullOrEmpty(c.UpdatedAt) ? null : DateTimeOffset.Parse(c.UpdatedAt))).ToList();
    return Results.Ok(dtos);
});

app.MapPost("/delete_conversation", async (DeleteConversationRequest req, INamsClient client, CancellationToken ct) =>
{
    await client.DeleteConversationAsync(req.ConversationId, ct).ConfigureAwait(false);
    return Results.NoContent();
});

app.MapPost("/bulk_add_messages", async (BulkAddMessagesRequest req, INamsClient client, CancellationToken ct) =>
{
    var inputs = req.Messages.Select(m => new NamsMessageInput(m.Content, m.Role)).ToList();
    var messages = await client.AddMessagesAsync(req.ConversationId, inputs, ct).ConfigureAwait(false);
    return Results.Ok(messages.Select(ToTckMessage).ToList());
});

// Single-message add, shared with the Bronze-tier wire contract -- session_id here is the conversation id
// (a NAMS conversation IS the "session"). Needed by test_get_context_shape, which adds one message before
// checking get_context's shape.
app.MapPost("/add_message", async (AddMessageRequest req, INamsClient client, CancellationToken ct) =>
{
    var messages = await client.AddMessagesAsync(
        req.SessionId, [new NamsMessageInput(req.Content, req.Role)], ct).ConfigureAwait(false);
    return Results.Ok(ToTckMessage(messages.Single()));
});

// ---- Context ----

app.MapPost("/get_context", async (GetContextRequest req, INamsClient client, CancellationToken ct) =>
{
    var context = await client.GetContextAsync(req.ConversationId, ct).ConfigureAwait(false);
    var dto = new TckContext(
        context.Reflections.Select(r => new TckReflection(r.Id, r.ConversationId, r.Content, r.CreatedAt)).ToList(),
        context.Observations.Select(o => new TckObservationEntry(o.Id, o.ConversationId, o.Content, o.CreatedAt)).ToList(),
        context.RecentMessages.Select(ToTckMessage).ToList());
    return Results.Ok(dto);
});

app.MapPost("/get_observations", async (GetObservationsRequest req, INamsClient client, CancellationToken ct) =>
{
    var observations = await client.GetObservationsAsync(req.ConversationId, req.Limit ?? 50, ct).ConfigureAwait(false);
    var dtos = observations.Select(o => new TckObservationEntry(o.Id, o.ConversationId, o.Content, o.CreatedAt)).ToList();
    return Results.Ok(dtos);
});

// ---- Entities, feedback, graph ----

app.MapPost("/add_entity", async (AddEntityRequest req, INamsClient client, CancellationToken ct) =>
{
    var result = await client.CreateEntityAsync(req.Name, req.EntityType, req.Description, ct).ConfigureAwait(false);
    // NAMS's own response never includes created_at in any resolution outcome, and on a "merged" outcome
    // (auto-merged into an existing entity) omits name/type/description entirely -- confirmed live, Phase
    // 10j. Synthesize created_at always; fall back to the submitted name/type/description in that case, since
    // the TCK client's _entity_from_dict requires all four fields unconditionally.
    var dto = new TckEntity(
        result.Id,
        result.Name ?? req.Name,
        result.Type ?? req.EntityType,
        result.Description ?? req.Description,
        DateTimeOffset.UtcNow);
    return Results.Ok(dto);
});

app.MapPost("/set_entity_feedback", async (SetEntityFeedbackRequest req, INamsClient client, CancellationToken ct) =>
{
    var result = await client.SetEntityFeedbackAsync(req.EntityId, req.UserScore, req.Confirmed, ct).ConfigureAwait(false);
    // Deliberately a bridge-local DTO, not a direct NamsEntityFeedbackResult reuse -- see Dtos.cs's own
    // comment on why reusing a domain record with explicit JsonPropertyName attributes is unsafe here.
    return Results.Ok(new TckEntityFeedbackResult(result.Id, result.Updated));
});

app.MapPost("/get_entity_graph", async (INamsClient client, CancellationToken ct) =>
{
    var graph = await client.GetEntityGraphAsync(ct).ConfigureAwait(false);
    var dto = new TckEntityGraph(
        graph.Nodes.Select(n => new TckGraphNode(n.Id, n.Name, n.Type)).ToList(),
        // TCK's edge parser requires "source"/"target" (not NAMS's "sourceId"/"targetId") AND requires "id"
        // to be a parseable UUID -- NAMS's real edge id is a compound "sourceId|TYPE|targetId" string, not a
        // UUID at all (confirmed live), so synthesize a fresh one; these are display-only ids for the Memory
        // Browser, never looked up again, so identity stability across calls isn't required.
        graph.Edges.Select(e => new TckGraphEdge(Guid.NewGuid().ToString(), e.SourceId, e.TargetId, e.Type)).ToList());
    return Results.Ok(dto);
});

// ---- Reasoning / provenance ----

app.MapPost("/record_step", async (RecordStepRequest req, INamsClient client, CancellationToken ct) =>
{
    var step = await client.RecordReasoningStepAsync(req.ConversationId, req.Reasoning, req.ActionTaken, req.Result, ct)
        .ConfigureAwait(false);
    // Deliberately a bridge-local DTO, not a direct NamsReasoningStep reuse -- its ConversationId/ActionTaken
    // properties carry explicit camelCase JsonPropertyName attributes that bypass this bridge's snake_case
    // naming policy entirely (confirmed the hard way: this broke both record_step and get_trace_by_conversation
    // on the first real conformance run). See Dtos.cs's own comment for the general rule.
    // Use the request's own conversationId rather than step.ConversationId (nullable on the shared domain
    // type) -- we already know the correct value without needing a null-forgiving operator.
    return Results.Ok(new TckAgentStepResult(step.Id, req.ConversationId, step.Reasoning, step.ActionTaken, step.Result));
});

app.MapPost("/get_trace_by_conversation", async (GetTraceRequest req, INamsClient client, CancellationToken ct) =>
{
    var trace = await client.GetReasoningTraceAsync(req.ConversationId, ct).ConfigureAwait(false);
    // Confirmed live: NAMS's own trace response omits conversationId on each individual step (only the
    // top-level trace object has it), but the TCK client's step parser requires it per-step -- patch it in
    // from the request parameter, which we already know is correct for every step in this trace.
    var steps = trace.Steps
        .Select(s => new TckAgentStep(s.Id, req.ConversationId, s.Reasoning, s.ActionTaken, s.Result, s.CreatedAt))
        .ToList();
    var toolCalls = trace.ToolCalls.Select(tc => new TckToolCall(tc.Id, tc.StepId, tc.ToolName, tc.Status)).ToList();
    var dto = new TckTrace(trace.ConversationId, steps, toolCalls);
    return Results.Ok(dto);
});

// ---- Cypher query console ----

app.MapPost("/cypher_query", async (CypherQueryRequest req, INamsClient client, CancellationToken ct) =>
{
    var result = await client.ExecuteCypherQueryAsync(req.Cypher, req.Params, ct).ConfigureAwait(false);
    // TCK's TCKCypherResult.rows expects each row as a POSITIONAL list of values ordered by columns, not
    // NAMS's own column-name-keyed dict shape -- confirmed the hard way (a Pydantic validation error on the
    // first real conformance run: "rows.0 Input should be a valid list").
    var rows = result.Rows
        .Select(row => (IReadOnlyList<object?>)result.Columns
            .Select(col => row.TryGetValue(col, out var value) ? JsonElementToObject(value) : null)
            .ToList())
        .ToList();
    var stats = result.Stats?.ToDictionary(kv => kv.Key, kv => JsonElementToObject(kv.Value));
    return Results.Ok(new TckCypherResult(result.Columns, rows, stats));
});

app.Run();

static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
{
    JsonValueKind.String => element.GetString(),
    JsonValueKind.Number => element.TryGetInt64(out var asLong) ? asLong : element.GetDouble(),
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    JsonValueKind.Null or JsonValueKind.Undefined => null,
    _ => element.Clone(), // nested arrays/objects -- pass through as JsonElement, re-serializes fine as-is
};

static TckMessage ToTckMessage(NamsMessage m) =>
    new(m.Id, m.Role, m.Content, string.IsNullOrEmpty(m.CreatedAt) ? null : DateTimeOffset.Parse(m.CreatedAt));

static DateTimeOffset ParseOrUtcNow(string? value) =>
    string.IsNullOrEmpty(value) ? DateTimeOffset.UtcNow : DateTimeOffset.Parse(value);
