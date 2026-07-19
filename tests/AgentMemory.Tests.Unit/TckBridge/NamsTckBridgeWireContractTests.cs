using System.Text.Json;
using AgentMemory.TckBridge.Nams;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.TckBridge;

// Locks the JSON wire contract of the Platinum TCK bridge (tools/AgentMemory.TckBridge.Nams) at the DTO
// level, without a live server or live NAMS SaaS call. Program.cs's ConfigureHttpJsonOptions configures the
// naming policy actually used at runtime -- this test builds the *same* JsonSerializerOptions by hand and
// asserts against it, same convention as TckBridgeWireContractTests (the direct-backend bridge's sibling).
//
// Unlike the direct-backend bridge, these DTOs are `internal` (see Dtos.cs's own header comment on why they
// are bridge-local translation records, never direct Nams-domain-record reuse) -- AgentMemory.TckBridge.Nams
// grants this test assembly an InternalsVisibleTo for exactly that reason.
//
// Coverage here is deliberately scoped to the DTOs that carry a real, previously-confirmed-the-hard-way wire
// gotcha (documented in Dtos.cs/Program.cs) plus a property-name lock for every response DTO -- not an
// exhaustive round-trip/null-preservation sweep of every record the way the direct-backend bridge's test
// file is, since most of these request DTOs are plain snake_case with no rename or null-handling surprises.
public class NamsTckBridgeWireContractTests
{
    // Mirrors Program.cs's ConfigureHttpJsonOptions exactly: ASP.NET initializes HttpJsonOptions from
    // JsonSerializerDefaults.Web, and Program.cs applies these two overrides on top -- nulls are NOT
    // globally ignored (created_at:null / title:null are legitimate response values the TCK's own parsers
    // read from), so this options instance intentionally omits DefaultIgnoreCondition.
    private static JsonSerializerOptions CreateBridgeJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

    // ---- TckConversation ----

    [Fact]
    public void TckConversation_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var conversation = new TckConversation("conv-1", "conv-1", "some title", DateTimeOffset.UtcNow, null);

        var json = JsonSerializer.Serialize(conversation, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "session_id", "title", "created_at", "updated_at");
    }

    [Fact]
    public void TckConversation_NullTitleAndUpdatedAt_SerializeAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var conversation = new TckConversation("conv-1", "conv-1", null, DateTimeOffset.UtcNow, null);

        var json = JsonSerializer.Serialize(conversation, options);

        // The TCK's own _conversation_from_dict indexes created_at/session_id directly (KeyError if
        // absent) -- title/updated_at being present-but-null (rather than omitted) is what this bridge
        // relies on to satisfy that parser without inventing values it doesn't have.
        json.Should().Contain("\"title\":null");
        json.Should().Contain("\"updated_at\":null");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("updated_at").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- TckMessage (Nams-bridge shape: no embedding/metadata, unlike the direct-backend TckMessage) ----

    [Fact]
    public void TckMessage_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var message = new TckMessage("msg-1", "user", "hello world", DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(message, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "role", "content", "timestamp");
    }

    [Fact]
    public void TckMessage_NullTimestamp_SerializesAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var message = new TckMessage("msg-1", "user", "hello world", null);

        var json = JsonSerializer.Serialize(message, options);

        json.Should().Contain("\"timestamp\":null");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- TckReflection / TckObservationEntry / TckContext ----

    [Fact]
    public void TckReflection_Serializes_PropertyNamesAsSnakeCase()
    {
        // TckReflection shares TckObservationEntry's exact shape (Id/ConversationId/Content/CreatedAt) --
        // this standalone lock exists so a rename/naming-policy regression on any of its fields is caught
        // here directly, rather than only through the nested TckContext test below (which only asserts
        // on the "id" value, not the full property set).
        var options = CreateBridgeJsonOptions();
        var reflection = new TckReflection("r-1", "conv-1", "reflected", "2026-07-19T00:00:00Z");

        var json = JsonSerializer.Serialize(reflection, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "conversation_id", "content", "created_at");
    }

    [Fact]
    public void TckObservationEntry_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var observation = new TckObservationEntry("obs-1", "conv-1", "noted something", "2026-07-19T00:00:00Z");

        var json = JsonSerializer.Serialize(observation, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "conversation_id", "content", "created_at");
    }

    [Fact]
    public void TckContext_Serializes_WithNestedReflectionsObservationsAndMessages()
    {
        var options = CreateBridgeJsonOptions();
        var context = new TckContext(
            [new TckReflection("r-1", "conv-1", "reflected", null)],
            [new TckObservationEntry("obs-1", "conv-1", "observed", null)],
            [new TckMessage("msg-1", "user", "hi", DateTimeOffset.UtcNow)]);

        var json = JsonSerializer.Serialize(context, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "reflections", "observations", "recent_messages");
        doc.RootElement.GetProperty("reflections")[0].GetProperty("id").GetString().Should().Be("r-1");
        doc.RootElement.GetProperty("observations")[0].GetProperty("id").GetString().Should().Be("obs-1");
        doc.RootElement.GetProperty("recent_messages")[0].GetProperty("id").GetString().Should().Be("msg-1");
    }

    // ---- TckEntity (Nams-bridge shape: id/name/type/description/created_at only) ----

    [Fact]
    public void TckEntity_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var entity = new TckEntity("e-1", "Alice", "Person", "a person", DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(entity, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "name", "type", "description", "created_at");
    }

    // ---- TckGraphNode / TckGraphEdge / TckEntityGraph ----
    //
    // Regression lock: the TCK's edge parser requires "source"/"target" -- NAMS's own edge shape is
    // sourceId/targetId. Confirmed live that using the NAMS names here breaks get_entity_graph outright
    // (KeyError in the TCK's own parser), so TckGraphEdge deliberately renames these on the wire.

    [Fact]
    public void TckGraphEdge_Serializes_AsSourceAndTarget_NotSourceIdTargetId()
    {
        var options = CreateBridgeJsonOptions();
        var edge = new TckGraphEdge("edge-1", "e-1", "e-2", "KNOWS");

        var json = JsonSerializer.Serialize(edge, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "source", "target", "type");
        doc.RootElement.GetProperty("source").GetString().Should().Be("e-1");
        doc.RootElement.GetProperty("target").GetString().Should().Be("e-2");
    }

    [Fact]
    public void TckGraphNode_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var node = new TckGraphNode("e-1", "Alice", "Person");

        var json = JsonSerializer.Serialize(node, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "name", "type");
    }

    [Fact]
    public void TckEntityGraph_Serializes_WithNestedNodesAndEdges()
    {
        var options = CreateBridgeJsonOptions();
        var graph = new TckEntityGraph(
            [new TckGraphNode("e-1", "Alice", "Person")],
            [new TckGraphEdge("edge-1", "e-1", "e-2", "KNOWS")]);

        var json = JsonSerializer.Serialize(graph, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("nodes", "edges");
        doc.RootElement.GetProperty("nodes")[0].GetProperty("name").GetString().Should().Be("Alice");
        doc.RootElement.GetProperty("edges")[0].GetProperty("source").GetString().Should().Be("e-1");
    }

    // ---- TckAgentStep / TckToolCall (Nams-bridge shape) / TckTrace ----
    //
    // Regression lock: NamsReasoningStep/NamsToolCall carry explicit camelCase JsonPropertyName attributes
    // in NAMS's own wire casing, which bypasses a PropertyNamingPolicy entirely -- confirmed the hard way
    // (this broke record_step/get_trace_by_conversation on the first real conformance run, per Dtos.cs's own
    // "IMPORTANT" comment). These bridge-local records carry NO such attributes, so the policy applies
    // uniformly -- this test locks that in.

    [Fact]
    public void TckAgentStep_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var step = new TckAgentStep("step-1", "conv-1", "thinking", "search", "found it", "2026-07-19T00:00:00Z");

        var json = JsonSerializer.Serialize(step, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "conversation_id", "reasoning", "action_taken", "result", "created_at");
    }

    [Fact]
    public void TckToolCall_NamsBridgeShape_Serializes_PropertyNamesAsSnakeCase()
    {
        // Distinct, smaller shape from the direct-backend bridge's TckToolCall (no arguments/result/
        // duration_ms/error here) -- easy to confuse the two since the type name is identical.
        var options = CreateBridgeJsonOptions();
        var toolCall = new TckToolCall("tc-1", "step-1", "search", "success");

        var json = JsonSerializer.Serialize(toolCall, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "step_id", "tool_name", "status");
    }

    [Fact]
    public void TckTrace_Serializes_WithNestedStepsAndToolCalls()
    {
        var options = CreateBridgeJsonOptions();
        var trace = new TckTrace(
            "conv-1",
            [new TckAgentStep("step-1", "conv-1", "thinking", "search", "found it", null)],
            [new TckToolCall("tc-1", "step-1", "search", "success")]);

        var json = JsonSerializer.Serialize(trace, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "conversation_id", "steps", "tool_calls");
        doc.RootElement.GetProperty("steps")[0].GetProperty("action_taken").GetString().Should().Be("search");
        doc.RootElement.GetProperty("tool_calls")[0].GetProperty("tool_name").GetString().Should().Be("search");
    }

    // ---- TckAgentStepResult / TckEntityFeedbackResult ----

    [Fact]
    public void TckAgentStepResult_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var result = new TckAgentStepResult("step-1", "conv-1", "thinking", "search", "found it");

        var json = JsonSerializer.Serialize(result, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "conversation_id", "reasoning", "action_taken", "result");
    }

    [Fact]
    public void TckEntityFeedbackResult_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var result = new TckEntityFeedbackResult("e-1", true);

        var json = JsonSerializer.Serialize(result, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "updated");
    }

    // ---- TckCypherResult ----
    //
    // Regression lock: the TCK's TCKCypherResult.rows expects each row as a POSITIONAL list of values
    // ordered by columns -- NOT NAMS's own column-name-keyed dict shape. Confirmed the hard way: a Pydantic
    // validation error ("rows.0 Input should be a valid list") on the first real conformance run.

    [Fact]
    public void TckCypherResult_Serializes_RowsAsPositionalArrays_NotKeyedObjects()
    {
        var options = CreateBridgeJsonOptions();
        var result = new TckCypherResult(
            ["name", "age"],
            [["Alice", 30], ["Bob", 25]],
            new Dictionary<string, object?> { ["nodesCreated"] = 0 });

        var json = JsonSerializer.Serialize(result, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("columns", "rows", "stats");

        var rows = doc.RootElement.GetProperty("rows");
        rows.ValueKind.Should().Be(JsonValueKind.Array);
        var firstRow = rows[0];
        firstRow.ValueKind.Should().Be(JsonValueKind.Array,
            "each row must be a positional JSON array matching the columns order, not a keyed object");
        firstRow[0].GetString().Should().Be("Alice");
        firstRow[1].GetInt32().Should().Be(30);
    }

    [Fact]
    public void TckCypherResult_NullStats_SerializesAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var result = new TckCypherResult(["name"], [["Alice"]], null);

        var json = JsonSerializer.Serialize(result, options);

        json.Should().Contain("\"stats\":null");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("stats").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- Request DTOs with a documented, non-obvious wire key ----

    [Fact]
    public void AddEntityRequest_Deserializes_EntityTypeField_NotType()
    {
        // Regression lock: the wire key is "entity_type" (matching the TCK client's own add_entity call
        // parameter name), not "type" -- naming the property Type would snake_case to "type" and silently
        // leave EntityType null/empty, breaking add_entity's downstream CreateEntityAsync call.
        var options = CreateBridgeJsonOptions();
        const string json = """{"name":"Alice","entity_type":"Person","description":"a person"}""";

        var request = JsonSerializer.Deserialize<AddEntityRequest>(json, options);

        request.Should().NotBeNull();
        request!.Name.Should().Be("Alice");
        request.EntityType.Should().Be("Person");
        request.Description.Should().Be("a person");
    }

    [Fact]
    public void CypherQueryRequest_Deserializes_SnakeCaseFields()
    {
        var options = CreateBridgeJsonOptions();
        const string json = """{"cypher":"MATCH (n) RETURN n LIMIT $limit","params":{"limit":5}}""";

        var request = JsonSerializer.Deserialize<CypherQueryRequest>(json, options);

        request.Should().NotBeNull();
        request!.Cypher.Should().Be("MATCH (n) RETURN n LIMIT $limit");
        request.Params.Should().NotBeNull();
    }

    [Fact]
    public void SetEntityFeedbackRequest_Deserializes_SnakeCaseFields()
    {
        var options = CreateBridgeJsonOptions();
        const string json = """{"entity_id":"e-1","user_score":0.9,"confirmed":true}""";

        var request = JsonSerializer.Deserialize<SetEntityFeedbackRequest>(json, options);

        request.Should().NotBeNull();
        request!.EntityId.Should().Be("e-1");
        request.UserScore.Should().Be(0.9);
        request.Confirmed.Should().BeTrue();
    }

    // ---- Case-insensitive read (PropertyNameCaseInsensitive = true, same as the direct-backend bridge) ----

    [Fact]
    public void Deserialize_IsCaseInsensitive_ForPropertyNames()
    {
        var options = CreateBridgeJsonOptions();
        const string json = """{"ENTITY_ID":"e-1","USER_SCORE":0.5,"CONFIRMED":true}""";

        var request = JsonSerializer.Deserialize<SetEntityFeedbackRequest>(json, options);

        request.Should().NotBeNull();
        request!.EntityId.Should().Be("e-1");
    }
}
