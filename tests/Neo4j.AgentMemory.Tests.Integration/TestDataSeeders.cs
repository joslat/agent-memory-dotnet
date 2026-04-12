using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Tests.Integration;

public static class TestDataSeeders
{
    private static readonly DateTimeOffset DefaultTimestamp =
        new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Conversation CreateConversation(
        string? id = null,
        string? agentId = null)
    {
        return new Conversation
        {
            ConversationId = id ?? $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            UserId = "test-user",
            CreatedAtUtc = DefaultTimestamp,
            UpdatedAtUtc = DefaultTimestamp,
            Metadata = agentId is not null
                ? new Dictionary<string, object> { ["agentId"] = agentId }
                : new Dictionary<string, object>()
        };
    }

    public static Message CreateMessage(
        string? id = null,
        string? conversationId = null,
        string? content = null)
    {
        var convId = conversationId ?? $"conv-{Guid.NewGuid():N}";
        return new Message
        {
            MessageId = id ?? $"msg-{Guid.NewGuid():N}",
            ConversationId = convId,
            SessionId = $"session-{Guid.NewGuid():N}",
            Role = "user",
            Content = content ?? "Hello, this is a test message.",
            TimestampUtc = DefaultTimestamp
        };
    }

    public static Entity CreateEntity(
        string? id = null,
        string? name = null)
    {
        return new Entity
        {
            EntityId = id ?? $"entity-{Guid.NewGuid():N}",
            Name = name ?? "Test Entity",
            Type = "Person",
            Confidence = 0.95,
            CreatedAtUtc = DefaultTimestamp
        };
    }

    public static Fact CreateFact(
        string? id = null,
        string? subject = null,
        string? predicate = null,
        string? obj = null)
    {
        return new Fact
        {
            FactId = id ?? $"fact-{Guid.NewGuid():N}",
            Subject = subject ?? "Alice",
            Predicate = predicate ?? "works_at",
            Object = obj ?? "Acme Corp",
            Confidence = 0.9,
            CreatedAtUtc = DefaultTimestamp
        };
    }

    public static Preference CreatePreference(
        string? id = null,
        string? preferenceText = null)
    {
        return new Preference
        {
            PreferenceId = id ?? $"pref-{Guid.NewGuid():N}",
            Category = "communication",
            PreferenceText = preferenceText ?? "Prefers concise responses.",
            Confidence = 0.85,
            CreatedAtUtc = DefaultTimestamp
        };
    }

    public static Relationship CreateRelationship(
        string? id = null,
        string? sourceEntityId = null,
        string? targetEntityId = null)
    {
        return new Relationship
        {
            RelationshipId = id ?? $"rel-{Guid.NewGuid():N}",
            SourceEntityId = sourceEntityId ?? $"entity-{Guid.NewGuid():N}",
            TargetEntityId = targetEntityId ?? $"entity-{Guid.NewGuid():N}",
            RelationshipType = "KNOWS",
            Confidence = 0.8,
            CreatedAtUtc = DefaultTimestamp
        };
    }

    public static ReasoningTrace CreateReasoningTrace(
        string? id = null,
        string? task = null)
    {
        return new ReasoningTrace
        {
            TraceId = id ?? $"trace-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            Task = task ?? "Test reasoning task",
            StartedAtUtc = DefaultTimestamp
        };
    }

    public static ReasoningStep CreateReasoningStep(
        string? id = null,
        string? traceId = null,
        int stepNumber = 1)
    {
        return new ReasoningStep
        {
            StepId = id ?? $"step-{Guid.NewGuid():N}",
            TraceId = traceId ?? $"trace-{Guid.NewGuid():N}",
            StepNumber = stepNumber,
            Thought = "Analyzing the problem.",
            Action = "Searching knowledge base."
        };
    }

    public static ToolCall CreateToolCall(
        string? id = null,
        string? stepId = null,
        string? toolName = null)
    {
        return new ToolCall
        {
            ToolCallId = id ?? $"tool-{Guid.NewGuid():N}",
            StepId = stepId ?? $"step-{Guid.NewGuid():N}",
            ToolName = toolName ?? "search_tool",
            ArgumentsJson = """{"query": "test query"}""",
            Status = ToolCallStatus.Success
        };
    }
}
