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

    // Mirrors Program.cs's builder.Services.ConfigureHttpJsonOptions block exactly: snake_case
    // wire names, case-insensitive read, and — critically — nulls are NOT globally ignored
    // (embedding:null / title:null / created_at:null are legitimate response values the TCK
    // asserts on), so this options instance intentionally omits DefaultIgnoreCondition.
    private static JsonSerializerOptions CreateBridgeJsonOptions() =>
        new()
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

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
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

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
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
        doc.RootElement.GetProperty("messages")[0].EnumerateObject().Select(p => p.Name).Should().Equal(
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
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
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

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
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

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
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
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
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
}
