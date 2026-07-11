using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.TckBridge;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.TckBridge;

// Locks the JSON wire contract of the Bronze TCK bridge (tools/AgentMemory.TckBridge) at the DTO
// level, without a live server or Neo4j. Program.cs's ConfigureHttpJsonOptions configures the
// naming policy actually used at runtime — this test builds the *same* JsonSerializerOptions by
// hand and asserts against it, so a drift in either place (DTO shape or serializer config) would
// break this test.
//
// Deliberately NOT a JsonSerializerOptions test against the hosted app (no WebApplicationFactory
// here) — this is a pure, fast, Neo4j-free unit test of the DTO <-> JSON mapping.
public class TckBridgeWireContractTests
{
    private static readonly Regex Iso8601Pattern =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.Compiled);

    // Mirrors runtime exactly: ASP.NET initializes HttpJsonOptions.SerializerOptions from
    // JsonSerializerDefaults.Web, and Program.cs's ConfigureHttpJsonOptions then applies these two
    // overrides on top — so start from the same Web baseline here. Snake_case wire names,
    // case-insensitive read, and — critically — nulls are NOT globally ignored (embedding:null /
    // title:null / created_at:null are legitimate response values the TCK asserts on), so this options
    // instance intentionally omits DefaultIgnoreCondition.
    private static JsonSerializerOptions CreateBridgeJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

    // ---- TckMessage ----

    [Fact]
    public void TckMessage_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var message = new TckMessage(
            Id: "msg-1",
            Role: "user",
            Content: "hello world",
            Timestamp: new DateTimeOffset(2026, 7, 11, 10, 30, 0, TimeSpan.Zero),
            Embedding: null,
            Metadata: new Dictionary<string, object> { ["source"] = "manual-entry" });

        var json = JsonSerializer.Serialize(message, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "role", "content", "timestamp", "embedding", "metadata");
    }

    [Fact]
    public void TckMessage_NullEmbedding_SerializesAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var message = new TckMessage(
            Id: "msg-1",
            Role: "user",
            Content: "hello world",
            Timestamp: DateTimeOffset.UtcNow,
            Embedding: null,
            Metadata: new Dictionary<string, object> { ["source"] = "manual-entry" });

        var json = JsonSerializer.Serialize(message, options);

        // Raw-string guard: the property must be present with a literal JSON null, not dropped —
        // this is the exact behavior a stray [JsonIgnore]/DefaultIgnoreCondition change would break
        // silently (round-trip equality alone would not catch an omitted-vs-null regression here,
        // since Embedding would still deserialize back to null either way).
        json.Should().Contain("\"embedding\":null");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("embedding", out var embeddingProp).Should().BeTrue(
            "the embedding property must be present on the wire even when null");
        embeddingProp.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TckMessage_PopulatedEmbedding_SerializesAsJsonArrayOfNumbers()
    {
        var options = CreateBridgeJsonOptions();
        var message = new TckMessage(
            Id: "msg-1",
            Role: "user",
            Content: "hello world",
            Timestamp: DateTimeOffset.UtcNow,
            Embedding: [0.1f, 0.2f, 0.3f],
            Metadata: new Dictionary<string, object>());

        var json = JsonSerializer.Serialize(message, options);
        using var doc = JsonDocument.Parse(json);

        var embeddingProp = doc.RootElement.GetProperty("embedding");
        embeddingProp.ValueKind.Should().Be(JsonValueKind.Array);
        embeddingProp.EnumerateArray().Select(e => e.GetSingle()).Should().Equal(0.1f, 0.2f, 0.3f);
    }

    [Fact]
    public void TckMessage_Timestamp_SerializesAsIso8601()
    {
        var options = CreateBridgeJsonOptions();
        var timestamp = new DateTimeOffset(2026, 7, 11, 10, 30, 45, TimeSpan.Zero);
        var message = new TckMessage(
            Id: "msg-1",
            Role: "user",
            Content: "hello world",
            Timestamp: timestamp,
            Embedding: null,
            Metadata: new Dictionary<string, object>());

        var json = JsonSerializer.Serialize(message, options);
        using var doc = JsonDocument.Parse(json);
        var timestampText = doc.RootElement.GetProperty("timestamp").GetString();

        timestampText.Should().NotBeNull();
        Iso8601Pattern.IsMatch(timestampText!).Should().BeTrue(
            $"'{timestampText}' should be an ISO-8601 timestamp");
        DateTimeOffset.Parse(timestampText!, null, System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().Be(timestamp);
    }

    [Fact]
    public void TckMessage_MetadataDictionaryKeys_AreNotAffectedByPropertyNamingPolicy()
    {
        // Dictionary<string, TValue> keys are governed by DictionaryKeyPolicy, not
        // PropertyNamingPolicy — Program.cs sets only the latter, so arbitrary (e.g. multi-word)
        // metadata keys must pass through verbatim rather than being snake_cased like the DTO's
        // own declared properties.
        var options = CreateBridgeJsonOptions();
        var message = new TckMessage(
            Id: "msg-1",
            Role: "user",
            Content: "hello world",
            Timestamp: DateTimeOffset.UtcNow,
            Embedding: null,
            Metadata: new Dictionary<string, object> { ["sourceSystem"] = "cli" });

        var json = JsonSerializer.Serialize(message, options);
        using var doc = JsonDocument.Parse(json);

        var metadata = doc.RootElement.GetProperty("metadata");
        metadata.TryGetProperty("sourceSystem", out var value).Should().BeTrue(
            "metadata keys must not be snake_cased");
        value.GetString().Should().Be("cli");
    }

    [Fact]
    public void TckMessage_RoundTrip_ReproducesOriginalValues()
    {
        var options = CreateBridgeJsonOptions();
        var timestamp = new DateTimeOffset(2026, 7, 11, 10, 30, 0, TimeSpan.Zero);
        var original = new TckMessage(
            Id: "msg-1",
            Role: "assistant",
            Content: "hello world",
            Timestamp: timestamp,
            Embedding: null,
            Metadata: new Dictionary<string, object> { ["source"] = "manual-entry", ["confidence"] = 0.87 });

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<TckMessage>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(original.Id);
        roundTripped.Role.Should().Be(original.Role);
        roundTripped.Content.Should().Be(original.Content);
        roundTripped.Timestamp.Should().Be(original.Timestamp);
        roundTripped.Embedding.Should().BeNull();

        // Metadata's declared value type is `object`, so round-tripped values come back boxed as
        // JsonElement rather than their original CLR types (string/double) — extract before
        // comparing rather than asserting reference/type equality on the raw dictionary.
        roundTripped.Metadata.Should().HaveCount(2);
        ((JsonElement)roundTripped.Metadata["source"]).GetString().Should().Be("manual-entry");
        ((JsonElement)roundTripped.Metadata["confidence"]).GetDouble().Should().Be(0.87);
    }

    // ---- TckConversation ----

    [Fact]
    public void TckConversation_Serializes_PropertyNamesAsSnakeCase_IncludingMultiWordProperties()
    {
        var options = CreateBridgeJsonOptions();
        var conversation = new TckConversation(
            Id: "conv-1",
            SessionId: "session-1",
            Messages: [],
            Title: "some title",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(conversation, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "session_id", "messages", "title", "created_at", "updated_at");
    }

    [Fact]
    public void TckConversation_NullTitle_SerializesAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var conversation = new TckConversation(
            Id: "conv-1",
            SessionId: "session-1",
            Messages: [],
            Title: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(conversation, options);

        json.Should().Contain("\"title\":null");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("title", out var titleProp).Should().BeTrue(
            "the title property must be present on the wire even when null");
        titleProp.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TckConversation_RoundTrip_ReproducesOriginalValues_IncludingNestedMessages()
    {
        var options = CreateBridgeJsonOptions();
        var createdAt = new DateTimeOffset(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);
        var updatedAt = new DateTimeOffset(2026, 7, 11, 9, 5, 0, TimeSpan.Zero);
        var nestedMessage = new TckMessage(
            Id: "msg-1",
            Role: "user",
            Content: "hello",
            Timestamp: createdAt,
            Embedding: [1.0f, 2.0f],
            Metadata: new Dictionary<string, object> { ["k"] = "v" });
        var original = new TckConversation(
            Id: "conv-1",
            SessionId: "session-1",
            Messages: [nestedMessage],
            Title: null,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt);

        var json = JsonSerializer.Serialize(original, options);
        using var doc = JsonDocument.Parse(json);
        // Nested list must retain the same snake_case mapping as the top-level DTO.
        doc.RootElement.GetProperty("messages")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "role", "content", "timestamp", "embedding", "metadata");

        var roundTripped = JsonSerializer.Deserialize<TckConversation>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(original.Id);
        roundTripped.SessionId.Should().Be(original.SessionId);
        roundTripped.Title.Should().BeNull();
        roundTripped.CreatedAt.Should().Be(createdAt);
        roundTripped.UpdatedAt.Should().Be(updatedAt);
        roundTripped.Messages.Should().ContainSingle();
        roundTripped.Messages[0].Id.Should().Be(nestedMessage.Id);
        roundTripped.Messages[0].Content.Should().Be(nestedMessage.Content);
        roundTripped.Messages[0].Embedding.Should().Equal(nestedMessage.Embedding);
    }

    // ---- TckSessionInfo ----

    [Fact]
    public void TckSessionInfo_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var sessionInfo = new TckSessionInfo(
            SessionId: "session-1",
            MessageCount: 5,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(sessionInfo, options);
        using var doc = JsonDocument.Parse(json);

        // Must match the TCK TCKSessionInfo contract exactly (session_id, message_count, created_at,
        // updated_at) — the runner reads created_at as a required key.
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "session_id", "message_count", "created_at", "updated_at");
    }

    [Fact]
    public void TckSessionInfo_NullUpdatedAt_SerializesAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var sessionInfo = new TckSessionInfo(
            SessionId: "session-1",
            MessageCount: 0,
            CreatedAt: null,
            UpdatedAt: null);

        var json = JsonSerializer.Serialize(sessionInfo, options);

        // created_at must be present even when null (the adapter reads d["created_at"] and would
        // KeyError if the key were dropped); updated_at is an optional field.
        json.Should().Contain("\"created_at\":null");
        json.Should().Contain("\"updated_at\":null");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("created_at", out var createdAtProp).Should().BeTrue(
            "the created_at property must be present on the wire even when null");
        createdAtProp.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TckSessionInfo_RoundTrip_ReproducesOriginalValues()
    {
        var options = CreateBridgeJsonOptions();
        var createdAt = new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);
        var original = new TckSessionInfo(
            SessionId: "session-1",
            MessageCount: 12,
            CreatedAt: createdAt,
            UpdatedAt: null);

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<TckSessionInfo>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.SessionId.Should().Be(original.SessionId);
        roundTripped.MessageCount.Should().Be(original.MessageCount);
        roundTripped.CreatedAt.Should().Be(createdAt);
        roundTripped.UpdatedAt.Should().BeNull();
    }

    // ---- Long-term DTOs (Bronze schema tier) ----

    [Fact]
    public void TckEntity_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var entity = new TckEntity(
            Id: "e-1",
            Name: "Alice",
            Type: "PERSON",
            Subtype: null,
            Description: "a person",
            Embedding: null,
            CanonicalName: null,
            CreatedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(entity, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "name", "type", "subtype", "description", "embedding", "canonical_name", "created_at");
    }

    [Fact]
    public void TckPreference_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var preference = new TckPreference(
            Id: "p-1", Category: "language", Preference: "Prefers Python", Context: null, Embedding: null);

        var json = JsonSerializer.Serialize(preference, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "category", "preference", "context", "embedding");
    }

    [Fact]
    public void TckFact_Serializes_WithObjectFieldName()
    {
        var options = CreateBridgeJsonOptions();
        var fact = new TckFact(
            Id: "f-1", Subject: "Alice", Predicate: "WORKS_AT", Object: "Acme", Embedding: null);

        var json = JsonSerializer.Serialize(fact, options);
        using var doc = JsonDocument.Parse(json);

        // The response fact field is "object" (matching the TCK TCKFact model), not "obj".
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "subject", "predicate", "object", "embedding");
    }

    [Fact]
    public void AddFactRequest_Deserializes_ObjField_NotObject()
    {
        // Regression lock: the bridge protocol sends the fact object as "obj" (add_fact params:
        // subject/predicate/obj), so the request property must bind from "obj". Naming it "Object"
        // would snake_case to "object", silently leaving the value null and breaking add_fact.
        var options = CreateBridgeJsonOptions();
        const string json = """{"subject":"Alice","predicate":"WORKS_AT","obj":"Acme"}""";

        var request = JsonSerializer.Deserialize<AddFactRequest>(json, options);

        request.Should().NotBeNull();
        request!.Subject.Should().Be("Alice");
        request.Predicate.Should().Be("WORKS_AT");
        request.Obj.Should().Be("Acme");
    }

    // ---- TckRelationship (Silver tier: add_relationship / get_related_entities) ----

    [Fact]
    public void TckRelationship_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var relationship = new TckRelationship(
            Id: "rel-1",
            SourceId: "e-1",
            TargetId: "e-2",
            RelationshipType: "WORKS_AT",
            Properties: new Dictionary<string, object> { ["since"] = "2020" });

        var json = JsonSerializer.Serialize(relationship, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "source_id", "target_id", "relationship_type", "properties");
    }

    [Fact]
    public void TckRelationship_RoundTrip_ReproducesOriginalValues()
    {
        var options = CreateBridgeJsonOptions();
        var original = new TckRelationship(
            Id: "rel-1",
            SourceId: "e-1",
            TargetId: "e-2",
            RelationshipType: "WORKS_AT",
            Properties: new Dictionary<string, object> { ["since"] = "2020" });

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<TckRelationship>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(original.Id);
        roundTripped.SourceId.Should().Be(original.SourceId);
        roundTripped.TargetId.Should().Be(original.TargetId);
        roundTripped.RelationshipType.Should().Be(original.RelationshipType);
        roundTripped.Properties.Should().ContainKey("since");
        ((JsonElement)roundTripped.Properties["since"]).GetString().Should().Be("2020");
    }

    // ---- Reasoning DTOs (Silver tier: start_trace / add_step / record_tool_call / complete_trace /
    // get_trace_with_steps / list_traces / get_tool_stats) ----

    [Fact]
    public void TckReasoningTrace_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var trace = new TckReasoningTrace(
            Id: "trace-1",
            SessionId: "session-1",
            Task: "find the answer",
            Steps: [],
            Outcome: "solved",
            Success: true,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(trace, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "session_id", "task", "steps", "outcome", "success", "started_at", "completed_at");
    }

    [Fact]
    public void TckReasoningTrace_NullOutcomeSuccessCompletedAt_SerializeAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var trace = new TckReasoningTrace(
            Id: "trace-1",
            SessionId: "session-1",
            Task: "find the answer",
            Steps: [],
            Outcome: null,
            Success: null,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null);

        var json = JsonSerializer.Serialize(trace, options);

        json.Should().Contain("\"outcome\":null");
        json.Should().Contain("\"success\":null");
        json.Should().Contain("\"completed_at\":null");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("outcome").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("success").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("completed_at").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TckReasoningTrace_StartedAt_SerializesAsIso8601()
    {
        var options = CreateBridgeJsonOptions();
        var startedAt = new DateTimeOffset(2026, 7, 11, 10, 30, 45, TimeSpan.Zero);
        var trace = new TckReasoningTrace(
            Id: "trace-1",
            SessionId: "session-1",
            Task: "find the answer",
            Steps: [],
            Outcome: null,
            Success: null,
            StartedAt: startedAt,
            CompletedAt: null);

        var json = JsonSerializer.Serialize(trace, options);
        using var doc = JsonDocument.Parse(json);
        var startedAtText = doc.RootElement.GetProperty("started_at").GetString();

        startedAtText.Should().NotBeNull();
        Iso8601Pattern.IsMatch(startedAtText!).Should().BeTrue(
            $"'{startedAtText}' should be an ISO-8601 timestamp");
        DateTimeOffset.Parse(startedAtText!, null, System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().Be(startedAt);
    }

    [Fact]
    public void TckReasoningTrace_RoundTrip_ReproducesOriginalValues_IncludingNestedSteps()
    {
        var options = CreateBridgeJsonOptions();
        var startedAt = new DateTimeOffset(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);
        var nestedStep = new TckReasoningStep(
            Id: "step-1",
            TraceId: "trace-1",
            StepNumber: 1,
            Thought: "thinking",
            Action: "search",
            Observation: "found it",
            ToolCalls: []);
        var original = new TckReasoningTrace(
            Id: "trace-1",
            SessionId: "session-1",
            Task: "find the answer",
            Steps: [nestedStep],
            Outcome: "solved",
            Success: true,
            StartedAt: startedAt,
            CompletedAt: null);

        var json = JsonSerializer.Serialize(original, options);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("steps")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "trace_id", "step_number", "thought", "action", "observation", "tool_calls");

        var roundTripped = JsonSerializer.Deserialize<TckReasoningTrace>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(original.Id);
        roundTripped.SessionId.Should().Be(original.SessionId);
        roundTripped.Task.Should().Be(original.Task);
        roundTripped.Outcome.Should().Be(original.Outcome);
        roundTripped.Success.Should().Be(original.Success);
        roundTripped.StartedAt.Should().Be(startedAt);
        roundTripped.CompletedAt.Should().BeNull();
        roundTripped.Steps.Should().ContainSingle();
        roundTripped.Steps[0].Id.Should().Be(nestedStep.Id);
        roundTripped.Steps[0].Thought.Should().Be(nestedStep.Thought);
    }

    [Fact]
    public void TckReasoningStep_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var step = new TckReasoningStep(
            Id: "step-1",
            TraceId: "trace-1",
            StepNumber: 1,
            Thought: "thinking",
            Action: "search",
            Observation: "found it",
            ToolCalls: []);

        var json = JsonSerializer.Serialize(step, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "trace_id", "step_number", "thought", "action", "observation", "tool_calls");
    }

    [Fact]
    public void TckReasoningStep_NullThoughtActionObservation_SerializeAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var step = new TckReasoningStep(
            Id: "step-1",
            TraceId: "trace-1",
            StepNumber: 1,
            Thought: null,
            Action: null,
            Observation: null,
            ToolCalls: []);

        var json = JsonSerializer.Serialize(step, options);

        json.Should().Contain("\"thought\":null");
        json.Should().Contain("\"action\":null");
        json.Should().Contain("\"observation\":null");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("thought").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("action").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("observation").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TckReasoningStep_RoundTrip_ReproducesOriginalValues_IncludingNestedToolCalls()
    {
        var options = CreateBridgeJsonOptions();
        using var argsDoc = JsonDocument.Parse("""{"query":"weather"}""");
        var toolCall = new TckToolCall(
            Id: "tc-1",
            ToolName: "search",
            Arguments: argsDoc.RootElement.Clone(),
            Result: null,
            Status: "success",
            DurationMs: 42,
            Error: null);
        var original = new TckReasoningStep(
            Id: "step-1",
            TraceId: "trace-1",
            StepNumber: 1,
            Thought: "thinking",
            Action: "search",
            Observation: "found it",
            ToolCalls: [toolCall]);

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<TckReasoningStep>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(original.Id);
        roundTripped.TraceId.Should().Be(original.TraceId);
        roundTripped.StepNumber.Should().Be(original.StepNumber);
        roundTripped.ToolCalls.Should().ContainSingle();
        roundTripped.ToolCalls[0].Id.Should().Be(toolCall.Id);
        roundTripped.ToolCalls[0].ToolName.Should().Be(toolCall.ToolName);
        roundTripped.ToolCalls[0].Arguments.GetProperty("query").GetString().Should().Be("weather");
    }

    // ---- TckToolCall (Arguments/Result are JsonElement wire-level JSON, not JSON-escaped strings —
    // the TCK asserts `tc.arguments == {...}` as a dict, so these must serialize as JSON objects) ----

    [Fact]
    public void TckToolCall_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        using var argsDoc = JsonDocument.Parse("{}");
        var toolCall = new TckToolCall(
            Id: "tc-1",
            ToolName: "search",
            Arguments: argsDoc.RootElement.Clone(),
            Result: null,
            Status: "success",
            DurationMs: 42,
            Error: null);

        var json = JsonSerializer.Serialize(toolCall, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "tool_name", "arguments", "result", "status", "duration_ms", "error");
    }

    [Fact]
    public void TckToolCall_PopulatedArguments_SerializesAsJsonObject_NotEscapedString()
    {
        // Regression lock: Arguments is a JsonElement (re-parsed from ArgumentsJson in
        // Program.cs's ToToolCallDto), so it must appear as a nested JSON object on the wire —
        // not as a JSON string containing escaped JSON — because the TCK asserts
        // `tool_call.arguments == {"query": "weather", "limit": 5}` (a dict equality check that
        // would fail against a string).
        var options = CreateBridgeJsonOptions();
        using var argsDoc = JsonDocument.Parse("""{"query":"weather","limit":5}""");
        var toolCall = new TckToolCall(
            Id: "tc-1",
            ToolName: "search",
            Arguments: argsDoc.RootElement.Clone(),
            Result: null,
            Status: "success",
            DurationMs: 42,
            Error: null);

        var json = JsonSerializer.Serialize(toolCall, options);
        using var doc = JsonDocument.Parse(json);

        var argumentsProp = doc.RootElement.GetProperty("arguments");
        argumentsProp.ValueKind.Should().Be(JsonValueKind.Object,
            "arguments must be a JSON object on the wire, not a JSON-escaped string");
        argumentsProp.GetProperty("query").GetString().Should().Be("weather");
        argumentsProp.GetProperty("limit").GetInt32().Should().Be(5);

        // Round-trip: re-parsing must keep Arguments as a JsonElement object, not a string.
        var roundTripped = JsonSerializer.Deserialize<TckToolCall>(json, options);
        roundTripped.Should().NotBeNull();
        roundTripped!.Arguments.ValueKind.Should().Be(JsonValueKind.Object);
        roundTripped.Arguments.GetProperty("query").GetString().Should().Be("weather");
        roundTripped.Arguments.GetProperty("limit").GetInt32().Should().Be(5);
    }

    [Fact]
    public void TckToolCall_NullResultAndError_SerializeAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        using var argsDoc = JsonDocument.Parse("{}");
        var toolCall = new TckToolCall(
            Id: "tc-1",
            ToolName: "search",
            Arguments: argsDoc.RootElement.Clone(),
            Result: null,
            Status: "success",
            DurationMs: null,
            Error: null);

        var json = JsonSerializer.Serialize(toolCall, options);

        json.Should().Contain("\"result\":null");
        json.Should().Contain("\"duration_ms\":null");
        json.Should().Contain("\"error\":null");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("duration_ms").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TckToolCall_PopulatedResult_SerializesAsJsonObject_NotEscapedString()
    {
        var options = CreateBridgeJsonOptions();
        using var argsDoc = JsonDocument.Parse("{}");
        using var resultDoc = JsonDocument.Parse("""{"temperature":72,"conditions":"sunny"}""");
        var toolCall = new TckToolCall(
            Id: "tc-1",
            ToolName: "search",
            Arguments: argsDoc.RootElement.Clone(),
            Result: resultDoc.RootElement.Clone(),
            Status: "success",
            DurationMs: 100,
            Error: null);

        var json = JsonSerializer.Serialize(toolCall, options);
        using var doc = JsonDocument.Parse(json);

        var resultProp = doc.RootElement.GetProperty("result");
        resultProp.ValueKind.Should().Be(JsonValueKind.Object,
            "result must be a JSON object on the wire, not a JSON-escaped string");
        resultProp.GetProperty("temperature").GetInt32().Should().Be(72);
        resultProp.GetProperty("conditions").GetString().Should().Be("sunny");
    }

    [Fact]
    public void TckToolCall_RoundTrip_ReproducesOriginalValues()
    {
        var options = CreateBridgeJsonOptions();
        using var argsDoc = JsonDocument.Parse("""{"query":"weather"}""");
        var original = new TckToolCall(
            Id: "tc-1",
            ToolName: "search",
            Arguments: argsDoc.RootElement.Clone(),
            Result: null,
            Status: "error",
            DurationMs: 250,
            Error: "timeout");

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<TckToolCall>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(original.Id);
        roundTripped.ToolName.Should().Be(original.ToolName);
        roundTripped.Status.Should().Be(original.Status);
        roundTripped.DurationMs.Should().Be(original.DurationMs);
        roundTripped.Error.Should().Be(original.Error);
        roundTripped.Result.Should().BeNull();
        roundTripped.Arguments.GetProperty("query").GetString().Should().Be("weather");
    }

    // ---- TckToolStats ----

    [Fact]
    public void TckToolStats_Serializes_PropertyNamesAsSnakeCase()
    {
        var options = CreateBridgeJsonOptions();
        var stats = new TckToolStats(
            Name: "search",
            TotalCalls: 10,
            SuccessfulCalls: 8,
            FailedCalls: 2,
            SuccessRate: 0.8,
            AvgDurationMs: 123.45);

        var json = JsonSerializer.Serialize(stats, options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "name", "total_calls", "successful_calls", "failed_calls", "success_rate", "avg_duration_ms");
    }

    [Fact]
    public void TckToolStats_NullAvgDurationMs_SerializesAsJsonNull_NotOmitted()
    {
        var options = CreateBridgeJsonOptions();
        var stats = new TckToolStats(
            Name: "search",
            TotalCalls: 0,
            SuccessfulCalls: 0,
            FailedCalls: 0,
            SuccessRate: 0,
            AvgDurationMs: null);

        var json = JsonSerializer.Serialize(stats, options);

        json.Should().Contain("\"avg_duration_ms\":null");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("avg_duration_ms").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void TckToolStats_RoundTrip_ReproducesOriginalValues()
    {
        var options = CreateBridgeJsonOptions();
        var original = new TckToolStats(
            Name: "search",
            TotalCalls: 10,
            SuccessfulCalls: 8,
            FailedCalls: 2,
            SuccessRate: 0.8,
            AvgDurationMs: 123.45);

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<TckToolStats>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.Name.Should().Be(original.Name);
        roundTripped.TotalCalls.Should().Be(original.TotalCalls);
        roundTripped.SuccessfulCalls.Should().Be(original.SuccessfulCalls);
        roundTripped.FailedCalls.Should().Be(original.FailedCalls);
        roundTripped.SuccessRate.Should().Be(original.SuccessRate);
        roundTripped.AvgDurationMs.Should().Be(original.AvgDurationMs);
    }

    // ---- Case-insensitive read (PropertyNameCaseInsensitive = true) ----

    [Fact]
    public void Deserialize_IsCaseInsensitive_ForPropertyNames()
    {
        var options = CreateBridgeJsonOptions();
        const string json = """
            {"ID":"msg-1","ROLE":"user","Content":"hi","TIMESTAMP":"2026-07-11T10:30:00+00:00","embedding":null,"metadata":{}}
            """;

        var message = JsonSerializer.Deserialize<TckMessage>(json, options);

        message.Should().NotBeNull();
        message!.Id.Should().Be("msg-1");
        message.Role.Should().Be("user");
        message.Content.Should().Be("hi");
    }

    // ---- Gold tier (merge_duplicate_entities / get_similar_traces) ----

    [Fact]
    public void MergeDuplicateEntitiesRequest_Deserializes_SnakeCaseFields()
    {
        var options = CreateBridgeJsonOptions();
        const string json =
            """{"source_id":"e-2","target_id":"e-1","canonical_name":"Alice Johnson"}""";

        var request = JsonSerializer.Deserialize<MergeDuplicateEntitiesRequest>(json, options);

        request.Should().NotBeNull();
        request!.SourceId.Should().Be("e-2");
        request.TargetId.Should().Be("e-1");
        request.CanonicalName.Should().Be("Alice Johnson");
    }

    [Fact]
    public void MergeDuplicateEntitiesRequest_OmittedCanonicalName_IsNull()
    {
        // The TCK sends canonical_name as null (BaseAdapter.merge_duplicate_entities defaults it to None),
        // and http_bridge includes the key with a null value — either way the property must bind to null.
        var options = CreateBridgeJsonOptions();
        const string json = """{"source_id":"e-2","target_id":"e-1"}""";

        var request = JsonSerializer.Deserialize<MergeDuplicateEntitiesRequest>(json, options);

        request.Should().NotBeNull();
        request!.SourceId.Should().Be("e-2");
        request.TargetId.Should().Be("e-1");
        request.CanonicalName.Should().BeNull();
    }

    [Fact]
    public void GetSimilarTracesRequest_Deserializes_SnakeCaseFields()
    {
        var options = CreateBridgeJsonOptions();
        const string json = """{"task":"What is Alice's role?","limit":2,"success_only":false}""";

        var request = JsonSerializer.Deserialize<GetSimilarTracesRequest>(json, options);

        request.Should().NotBeNull();
        request!.Task.Should().Be("What is Alice's role?");
        request.Limit.Should().Be(2);
        request.SuccessOnly.Should().BeFalse();
    }

    [Fact]
    public void GetSimilarTracesRequest_OmittedOptionals_AreNull()
    {
        // limit and success_only are optional on the wire; when omitted the bridge falls back to its
        // defaults (limit 5, success_only treated as true), so the DTO must leave them null, not 0/false.
        var options = CreateBridgeJsonOptions();
        const string json = """{"task":"anything"}""";

        var request = JsonSerializer.Deserialize<GetSimilarTracesRequest>(json, options);

        request.Should().NotBeNull();
        request!.Task.Should().Be("anything");
        request.Limit.Should().BeNull();
        request.SuccessOnly.Should().BeNull();
    }
}
