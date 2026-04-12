# Neo4j.AgentMemory.Abstractions — Domain Model and Interfaces Design
**Author:** Roy (Core Memory Domain Engineer)  
**Date:** 2025-01-27  
**Status:** Design Review Draft  
**Reviewer:** Deckard

---

## 1. Overview

This document defines the complete set of domain models, service interfaces, repository interfaces, and configuration models for the `Neo4j.AgentMemory.Abstractions` package.

### Design Principles
- **Zero framework dependencies** — no MAF, no Neo4j.Driver, no GraphRAG types
- **Immutability where appropriate** — use C# records for domain models
- **Strong typing** — no stringly-typed patterns
- **Nullable reference types** — explicit nullability
- **Async-first** — all async methods accept `CancellationToken`
- **XML documentation** — all public types documented
- **Specification compliance** — all required fields from spec section 3 present

### Dependencies
- .NET 9
- C# 13 nullable reference types enabled
- No external package dependencies for Abstractions

---

## 2. Domain Models

All domain models are organized by memory layer as specified in section 3.1 of the specification.

### 2.1 Short-Term Memory Domain

#### Conversation
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a conversation session containing messages.
/// </summary>
public sealed record Conversation
{
    /// <summary>
    /// Unique identifier for the conversation.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Session identifier for grouping related conversations.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// UTC timestamp when the conversation was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp when the conversation was last updated.
    /// </summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata for the conversation.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### Message
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a message in a conversation.
/// </summary>
public sealed record Message
{
    /// <summary>
    /// Unique identifier for the message.
    /// </summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// Identifier of the conversation this message belongs to.
    /// </summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// Session identifier for the message.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Role of the message sender (e.g., "user", "assistant", "system").
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Text content of the message.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// UTC timestamp when the message was created.
    /// </summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Optional embedding vector for semantic search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Optional tool call information if this message involved tool usage.
    /// </summary>
    public IReadOnlyList<string>? ToolCallIds { get; init; }

    /// <summary>
    /// Additional metadata for the message.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### SessionInfo
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents session-level information for memory scoping.
/// </summary>
public sealed record SessionInfo
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier associated with this session.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// UTC timestamp when the session started.
    /// </summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp when the session ended, if applicable.
    /// </summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>
    /// Session-level metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

---

### 2.2 Long-Term Memory Domain

#### Entity
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents an entity extracted from conversations.
/// </summary>
public sealed record Entity
{
    /// <summary>
    /// Unique identifier for the entity.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>
    /// Name of the entity as mentioned.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Canonical or normalized name for deduplication.
    /// </summary>
    public string? CanonicalName { get; init; }

    /// <summary>
    /// Type classification (e.g., "Person", "Organization", "Location").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Optional subtype for finer classification.
    /// </summary>
    public string? Subtype { get; init; }

    /// <summary>
    /// Description or context about the entity.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for the extraction.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional embedding vector for semantic search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Alternative names or aliases.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Additional structured attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object> Attributes { get; init; } = 
        new Dictionary<string, object>();

    /// <summary>
    /// Source message references for provenance.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// UTC timestamp when the entity was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### Fact
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a factual statement extracted from conversations.
/// </summary>
public sealed record Fact
{
    /// <summary>
    /// Unique identifier for the fact.
    /// </summary>
    public required string FactId { get; init; }

    /// <summary>
    /// Subject of the fact (typically an entity or concept).
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Predicate or relationship type.
    /// </summary>
    public required string Predicate { get; init; }

    /// <summary>
    /// Object or value of the fact.
    /// </summary>
    public required string Object { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for the extraction.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional start of validity period.
    /// </summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>
    /// Optional end of validity period.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>
    /// Optional embedding vector for semantic search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Source message references for provenance.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// UTC timestamp when the fact was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### Preference
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a user preference extracted from conversations.
/// </summary>
public sealed record Preference
{
    /// <summary>
    /// Unique identifier for the preference.
    /// </summary>
    public required string PreferenceId { get; init; }

    /// <summary>
    /// Category of the preference (e.g., "communication", "style", "feature").
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Text description of the preference.
    /// </summary>
    public required string PreferenceText { get; init; }

    /// <summary>
    /// Optional context in which the preference applies.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for the extraction.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional embedding vector for semantic search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Source message references for provenance.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// UTC timestamp when the preference was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### Relationship
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a relationship between two entities.
/// </summary>
public sealed record Relationship
{
    /// <summary>
    /// Unique identifier for the relationship.
    /// </summary>
    public required string RelationshipId { get; init; }

    /// <summary>
    /// Source entity identifier.
    /// </summary>
    public required string SourceEntityId { get; init; }

    /// <summary>
    /// Target entity identifier.
    /// </summary>
    public required string TargetEntityId { get; init; }

    /// <summary>
    /// Type of relationship (e.g., "WORKS_FOR", "LOCATED_IN", "KNOWS").
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for the extraction.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Optional description of the relationship.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional start of validity period.
    /// </summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>
    /// Optional end of validity period.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>
    /// Additional structured attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object> Attributes { get; init; } = 
        new Dictionary<string, object>();

    /// <summary>
    /// Source message references for provenance.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// UTC timestamp when the relationship was created.
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

---

### 2.3 Reasoning Memory Domain

#### ReasoningTrace
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a reasoning trace for a task or agent run.
/// </summary>
public sealed record ReasoningTrace
{
    /// <summary>
    /// Unique identifier for the trace.
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// Session identifier for the trace.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Description of the task being performed.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Optional embedding vector for task similarity search.
    /// </summary>
    public float[]? TaskEmbedding { get; init; }

    /// <summary>
    /// Optional outcome description.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>
    /// Whether the task was completed successfully.
    /// </summary>
    public bool? Success { get; init; }

    /// <summary>
    /// UTC timestamp when the trace started.
    /// </summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp when the trace completed, if applicable.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### ReasoningStep
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a single step in a reasoning trace.
/// </summary>
public sealed record ReasoningStep
{
    /// <summary>
    /// Unique identifier for the step.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Identifier of the trace this step belongs to.
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>
    /// Sequential step number within the trace.
    /// </summary>
    public required int StepNumber { get; init; }

    /// <summary>
    /// Optional thought or reasoning for this step.
    /// </summary>
    public string? Thought { get; init; }

    /// <summary>
    /// Optional action taken in this step.
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Optional observation or result of the action.
    /// </summary>
    public string? Observation { get; init; }

    /// <summary>
    /// Optional embedding vector for step similarity search.
    /// </summary>
    public float[]? Embedding { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### ToolCall
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a tool invocation within a reasoning step.
/// </summary>
public sealed record ToolCall
{
    /// <summary>
    /// Unique identifier for the tool call.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Identifier of the step this tool call belongs to.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Name of the tool invoked.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// JSON-serialized arguments passed to the tool.
    /// </summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>
    /// JSON-serialized result from the tool, if available.
    /// </summary>
    public string? ResultJson { get; init; }

    /// <summary>
    /// Status of the tool call (e.g., "pending", "success", "error").
    /// </summary>
    public required ToolCallStatus Status { get; init; }

    /// <summary>
    /// Duration of the tool call in milliseconds.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Error message if the tool call failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}

/// <summary>
/// Status of a tool call.
/// </summary>
public enum ToolCallStatus
{
    /// <summary>
    /// Tool call is pending execution.
    /// </summary>
    Pending,

    /// <summary>
    /// Tool call completed successfully.
    /// </summary>
    Success,

    /// <summary>
    /// Tool call failed with an error.
    /// </summary>
    Error,

    /// <summary>
    /// Tool call was cancelled.
    /// </summary>
    Cancelled
}
```

---

### 2.4 Context and Recall Domain

#### MemoryContext
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents the assembled memory context for an agent run.
/// </summary>
public sealed record MemoryContext
{
    /// <summary>
    /// Session identifier for this context.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Recent conversation messages.
    /// </summary>
    public MemoryContextSection<Message> RecentMessages { get; init; } = 
        MemoryContextSection<Message>.Empty;

    /// <summary>
    /// Semantically relevant past messages.
    /// </summary>
    public MemoryContextSection<Message> RelevantMessages { get; init; } = 
        MemoryContextSection<Message>.Empty;

    /// <summary>
    /// Relevant entities.
    /// </summary>
    public MemoryContextSection<Entity> RelevantEntities { get; init; } = 
        MemoryContextSection<Entity>.Empty;

    /// <summary>
    /// Relevant preferences.
    /// </summary>
    public MemoryContextSection<Preference> RelevantPreferences { get; init; } = 
        MemoryContextSection<Preference>.Empty;

    /// <summary>
    /// Relevant facts.
    /// </summary>
    public MemoryContextSection<Fact> RelevantFacts { get; init; } = 
        MemoryContextSection<Fact>.Empty;

    /// <summary>
    /// Similar past reasoning traces.
    /// </summary>
    public MemoryContextSection<ReasoningTrace> SimilarTraces { get; init; } = 
        MemoryContextSection<ReasoningTrace>.Empty;

    /// <summary>
    /// Optional GraphRAG-derived context.
    /// </summary>
    public string? GraphRagContext { get; init; }

    /// <summary>
    /// UTC timestamp when the context was assembled.
    /// </summary>
    public required DateTimeOffset AssembledAtUtc { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### MemoryContextSection
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Represents a section of memory context with items of a specific type.
/// </summary>
/// <typeparam name="T">Type of items in this section.</typeparam>
public sealed record MemoryContextSection<T>
{
    /// <summary>
    /// Items in this section.
    /// </summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>
    /// Section-level metadata (e.g., retrieval method, scores).
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();

    /// <summary>
    /// Empty section singleton.
    /// </summary>
    public static MemoryContextSection<T> Empty { get; } = new();
}
```

#### RecallRequest
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Request for recalling memory context.
/// </summary>
public sealed record RecallRequest
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Current user query or message.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional query embedding for semantic search.
    /// </summary>
    public float[]? QueryEmbedding { get; init; }

    /// <summary>
    /// Recall options.
    /// </summary>
    public RecallOptions Options { get; init; } = RecallOptions.Default;
}
```

#### RecallResult
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Result of a memory recall operation.
/// </summary>
public sealed record RecallResult
{
    /// <summary>
    /// Assembled memory context.
    /// </summary>
    public required MemoryContext Context { get; init; }

    /// <summary>
    /// Total number of items retrieved across all sections.
    /// </summary>
    public int TotalItemsRetrieved { get; init; }

    /// <summary>
    /// Whether the context was truncated due to budget limits.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Estimated token count of the assembled context.
    /// </summary>
    public int? EstimatedTokenCount { get; init; }

    /// <summary>
    /// Additional metadata about the recall operation.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

---

### 2.5 Extraction Domain

#### ExtractionRequest
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Request to extract structured memory from messages.
/// </summary>
public sealed record ExtractionRequest
{
    /// <summary>
    /// Messages to extract from.
    /// </summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary>
    /// Session context for the extraction.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Types of memory to extract.
    /// </summary>
    public ExtractionTypes TypesToExtract { get; init; } = ExtractionTypes.All;

    /// <summary>
    /// Additional extraction options.
    /// </summary>
    public IReadOnlyDictionary<string, object> Options { get; init; } = 
        new Dictionary<string, object>();
}

/// <summary>
/// Types of memory that can be extracted.
/// </summary>
[Flags]
public enum ExtractionTypes
{
    /// <summary>
    /// No extraction.
    /// </summary>
    None = 0,

    /// <summary>
    /// Extract entities.
    /// </summary>
    Entities = 1 << 0,

    /// <summary>
    /// Extract relationships.
    /// </summary>
    Relationships = 1 << 1,

    /// <summary>
    /// Extract facts.
    /// </summary>
    Facts = 1 << 2,

    /// <summary>
    /// Extract preferences.
    /// </summary>
    Preferences = 1 << 3,

    /// <summary>
    /// Extract all types.
    /// </summary>
    All = Entities | Relationships | Facts | Preferences
}
```

#### ExtractionResult
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Result of a memory extraction operation.
/// </summary>
public sealed record ExtractionResult
{
    /// <summary>
    /// Extracted entities.
    /// </summary>
    public IReadOnlyList<ExtractedEntity> Entities { get; init; } = Array.Empty<ExtractedEntity>();

    /// <summary>
    /// Extracted relationships.
    /// </summary>
    public IReadOnlyList<ExtractedRelationship> Relationships { get; init; } = 
        Array.Empty<ExtractedRelationship>();

    /// <summary>
    /// Extracted facts.
    /// </summary>
    public IReadOnlyList<ExtractedFact> Facts { get; init; } = Array.Empty<ExtractedFact>();

    /// <summary>
    /// Extracted preferences.
    /// </summary>
    public IReadOnlyList<ExtractedPreference> Preferences { get; init; } = 
        Array.Empty<ExtractedPreference>();

    /// <summary>
    /// Source message identifiers.
    /// </summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Extraction metadata (e.g., model used, extraction time).
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}
```

#### ExtractedEntity
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// An entity extracted from text, before persistence.
/// </summary>
public sealed record ExtractedEntity
{
    /// <summary>
    /// Name of the entity.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Type classification.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Optional subtype.
    /// </summary>
    public string? Subtype { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Alternative names or aliases.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Additional attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object> Attributes { get; init; } = 
        new Dictionary<string, object>();
}
```

#### ExtractedRelationship
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// A relationship extracted from text, before persistence.
/// </summary>
public sealed record ExtractedRelationship
{
    /// <summary>
    /// Source entity name.
    /// </summary>
    public required string SourceEntity { get; init; }

    /// <summary>
    /// Target entity name.
    /// </summary>
    public required string TargetEntity { get; init; }

    /// <summary>
    /// Type of relationship.
    /// </summary>
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Additional attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object> Attributes { get; init; } = 
        new Dictionary<string, object>();
}
```

#### ExtractedFact
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// A fact extracted from text, before persistence.
/// </summary>
public sealed record ExtractedFact
{
    /// <summary>
    /// Subject of the fact.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Predicate or relationship type.
    /// </summary>
    public required string Predicate { get; init; }

    /// <summary>
    /// Object or value.
    /// </summary>
    public required string Object { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Optional temporal bounds.
    /// </summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>
    /// Optional temporal bounds.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; init; }
}
```

#### ExtractedPreference
```csharp
namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// A preference extracted from text, before persistence.
/// </summary>
public sealed record ExtractedPreference
{
    /// <summary>
    /// Category of the preference.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Text description of the preference.
    /// </summary>
    public required string PreferenceText { get; init; }

    /// <summary>
    /// Optional context.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 1.0;
}
```

---

## 3. Service Interfaces

All service interfaces are async-first and accept `CancellationToken`.

### 3.1 Main Memory Service Interface

#### IMemoryService
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Facade service for all memory operations.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Recalls memory context for a query.
    /// </summary>
    Task<RecallResult> RecallAsync(
        RecallRequest request, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message to short-term memory.
    /// </summary>
    Task<Message> AddMessageAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch adds messages to short-term memory.
    /// </summary>
    Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts and persists long-term memory from messages.
    /// </summary>
    Task<ExtractionResult> ExtractAndPersistAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all memory for a session.
    /// </summary>
    Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
```

---

### 3.2 Short-Term Memory Service Interface

#### IShortTermMemoryService
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for short-term (conversational) memory operations.
/// </summary>
public interface IShortTermMemoryService
{
    /// <summary>
    /// Adds a conversation.
    /// </summary>
    Task<Conversation> AddConversationAsync(
        string conversationId,
        string sessionId,
        string? userId = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message to a conversation.
    /// </summary>
    Task<Message> AddMessageAsync(
        Message message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch adds messages.
    /// </summary>
    Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent messages for a session.
    /// </summary>
    Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        string sessionId,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets messages for a conversation.
    /// </summary>
    Task<IReadOnlyList<Message>> GetConversationMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches messages semantically.
    /// </summary>
    Task<IReadOnlyList<Message>> SearchMessagesAsync(
        string? sessionId,
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all messages for a session.
    /// </summary>
    Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
```

---

### 3.3 Long-Term Memory Service Interface

#### ILongTermMemoryService
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for long-term (structured knowledge) memory operations.
/// </summary>
public interface ILongTermMemoryService
{
    // Entity operations
    
    /// <summary>
    /// Adds or updates an entity.
    /// </summary>
    Task<Entity> AddEntityAsync(
        Entity entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities by name.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetEntitiesByNameAsync(
        string name,
        bool includeAliases = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities semantically.
    /// </summary>
    Task<IReadOnlyList<Entity>> SearchEntitiesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    // Preference operations

    /// <summary>
    /// Adds or updates a preference.
    /// </summary>
    Task<Preference> AddPreferenceAsync(
        Preference preference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets preferences by category.
    /// </summary>
    Task<IReadOnlyList<Preference>> GetPreferencesByCategoryAsync(
        string category,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches preferences semantically.
    /// </summary>
    Task<IReadOnlyList<Preference>> SearchPreferencesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    // Fact operations

    /// <summary>
    /// Adds or updates a fact.
    /// </summary>
    Task<Fact> AddFactAsync(
        Fact fact,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets facts by subject.
    /// </summary>
    Task<IReadOnlyList<Fact>> GetFactsBySubjectAsync(
        string subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches facts semantically.
    /// </summary>
    Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    // Relationship operations

    /// <summary>
    /// Adds or updates a relationship.
    /// </summary>
    Task<Relationship> AddRelationshipAsync(
        Relationship relationship,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets relationships for an entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetEntityRelationshipsAsync(
        string entityId,
        CancellationToken cancellationToken = default);
}
```

---

### 3.4 Reasoning Memory Service Interface

#### IReasoningMemoryService
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for reasoning trace memory operations.
/// </summary>
public interface IReasoningMemoryService
{
    /// <summary>
    /// Starts a new reasoning trace.
    /// </summary>
    Task<ReasoningTrace> StartTraceAsync(
        string sessionId,
        string task,
        float[]? taskEmbedding = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a reasoning step to a trace.
    /// </summary>
    Task<ReasoningStep> AddStepAsync(
        string traceId,
        int stepNumber,
        string? thought = null,
        string? action = null,
        string? observation = null,
        float[]? embedding = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a tool call for a step.
    /// </summary>
    Task<ToolCall> RecordToolCallAsync(
        string stepId,
        string toolName,
        string argumentsJson,
        string? resultJson = null,
        ToolCallStatus status = ToolCallStatus.Pending,
        long? durationMs = null,
        string? error = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a reasoning trace.
    /// </summary>
    Task<ReasoningTrace> CompleteTraceAsync(
        string traceId,
        string? outcome = null,
        bool? success = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a trace with all its steps.
    /// </summary>
    Task<(ReasoningTrace Trace, IReadOnlyList<ReasoningStep> Steps)> GetTraceWithStepsAsync(
        string traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists traces for a session.
    /// </summary>
    Task<IReadOnlyList<ReasoningTrace>> ListTracesAsync(
        string sessionId,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for similar traces by task embedding.
    /// </summary>
    Task<IReadOnlyList<ReasoningTrace>> SearchSimilarTracesAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);
}
```

---

### 3.5 Memory Context Assembler Interface

#### IMemoryContextAssembler
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for assembling memory context from multiple sources.
/// </summary>
public interface IMemoryContextAssembler
{
    /// <summary>
    /// Assembles memory context for a recall request.
    /// </summary>
    Task<MemoryContext> AssembleContextAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default);
}
```

---

### 3.6 Extraction Pipeline Interface

#### IMemoryExtractionPipeline
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Pipeline for extracting structured memory from messages.
/// </summary>
public interface IMemoryExtractionPipeline
{
    /// <summary>
    /// Extracts structured memory from messages.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);
}
```

---

### 3.7 Individual Extractor Interfaces

#### IEntityExtractor
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for entities from text.
/// </summary>
public interface IEntityExtractor
{
    /// <summary>
    /// Extracts entities from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
```

#### IRelationshipExtractor
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for relationships from text.
/// </summary>
public interface IRelationshipExtractor
{
    /// <summary>
    /// Extracts relationships from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
```

#### IPreferenceExtractor
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for preferences from text.
/// </summary>
public interface IPreferenceExtractor
{
    /// <summary>
    /// Extracts preferences from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
```

#### IFactExtractor
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for facts from text.
/// </summary>
public interface IFactExtractor
{
    /// <summary>
    /// Extracts facts from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);
}
```

---

### 3.8 Embedding Provider Interface

#### IEmbeddingProvider
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Provider for generating text embeddings.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Generates an embedding for a single text.
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embeddings for multiple texts in batch.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the dimensionality of embeddings produced by this provider.
    /// </summary>
    int EmbeddingDimensions { get; }
}
```

---

### 3.9 Entity Resolution Interface

#### IEntityResolver
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Service for resolving and deduplicating entities.
/// </summary>
public interface IEntityResolver
{
    /// <summary>
    /// Resolves an extracted entity to an existing entity, or creates a new one.
    /// </summary>
    Task<Entity> ResolveEntityAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds potential duplicate entities.
    /// </summary>
    Task<IReadOnlyList<Entity>> FindPotentialDuplicatesAsync(
        string name,
        string type,
        CancellationToken cancellationToken = default);
}
```

---

### 3.10 GraphRAG Context Source Interface

#### IGraphRagContextSource
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Source for GraphRAG-derived context.
/// </summary>
public interface IGraphRagContextSource
{
    /// <summary>
    /// Retrieves context from GraphRAG.
    /// </summary>
    Task<GraphRagContextResult> GetContextAsync(
        GraphRagContextRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Request for GraphRAG context.
/// </summary>
public sealed record GraphRagContextRequest
{
    /// <summary>
    /// Session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// User query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Maximum number of results.
    /// </summary>
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Search mode.
    /// </summary>
    public GraphRagSearchMode SearchMode { get; init; } = GraphRagSearchMode.Hybrid;

    /// <summary>
    /// Additional options.
    /// </summary>
    public IReadOnlyDictionary<string, object> Options { get; init; } = 
        new Dictionary<string, object>();
}

/// <summary>
/// Result from GraphRAG context retrieval.
/// </summary>
public sealed record GraphRagContextResult
{
    /// <summary>
    /// Retrieved context items.
    /// </summary>
    public required IReadOnlyList<GraphRagContextItem> Items { get; init; }

    /// <summary>
    /// Retrieval metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}

/// <summary>
/// A single context item from GraphRAG.
/// </summary>
public sealed record GraphRagContextItem
{
    /// <summary>
    /// Context text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Relevance score.
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Source node identifiers.
    /// </summary>
    public IReadOnlyList<string> SourceNodeIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Item metadata.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = 
        new Dictionary<string, object>();
}

/// <summary>
/// GraphRAG search modes.
/// </summary>
public enum GraphRagSearchMode
{
    /// <summary>
    /// Vector similarity search.
    /// </summary>
    Vector,

    /// <summary>
    /// Full-text search.
    /// </summary>
    Fulltext,

    /// <summary>
    /// Hybrid vector + full-text search.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Graph traversal-based search.
    /// </summary>
    Graph
}
```

---

### 3.11 Utility Service Interfaces

#### IClock
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Abstraction for time operations (testability).
/// </summary>
public interface IClock
{
    /// <summary>
    /// Gets the current UTC time.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}
```

#### IIdGenerator
```csharp
namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Abstraction for generating unique identifiers.
/// </summary>
public interface IIdGenerator
{
    /// <summary>
    /// Generates a new unique identifier.
    /// </summary>
    string GenerateId();
}
```

---

## 4. Repository Interfaces

All repository interfaces follow a consistent pattern and are persistence-agnostic.

### 4.1 Conversation Repository

#### IConversationRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for conversation persistence.
/// </summary>
public interface IConversationRepository
{
    /// <summary>
    /// Adds or updates a conversation.
    /// </summary>
    Task<Conversation> UpsertAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a conversation by identifier.
    /// </summary>
    Task<Conversation?> GetByIdAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets conversations for a session.
    /// </summary>
    Task<IReadOnlyList<Conversation>> GetBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a conversation.
    /// </summary>
    Task DeleteAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
}
```

---

### 4.2 Message Repository

#### IMessageRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for message persistence.
/// </summary>
public interface IMessageRepository
{
    /// <summary>
    /// Adds a message.
    /// </summary>
    Task<Message> AddAsync(
        Message message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch adds messages.
    /// </summary>
    Task<IReadOnlyList<Message>> AddBatchAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a message by identifier.
    /// </summary>
    Task<Message?> GetByIdAsync(
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets messages for a conversation.
    /// </summary>
    Task<IReadOnlyList<Message>> GetByConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent messages for a session.
    /// </summary>
    Task<IReadOnlyList<Message>> GetRecentBySessionAsync(
        string sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches messages by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Message Message, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        string? sessionId = null,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all messages for a session.
    /// </summary>
    Task DeleteBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
```

---

### 4.3 Entity Repository

#### IEntityRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for entity persistence.
/// </summary>
public interface IEntityRepository
{
    /// <summary>
    /// Adds or updates an entity.
    /// </summary>
    Task<Entity> UpsertAsync(
        Entity entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an entity by identifier.
    /// </summary>
    Task<Entity?> GetByIdAsync(
        string entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities by name (exact or alias match).
    /// </summary>
    Task<IReadOnlyList<Entity>> GetByNameAsync(
        string name,
        bool includeAliases = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches entities by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Entity Entity, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets entities by type.
    /// </summary>
    Task<IReadOnlyList<Entity>> GetByTypeAsync(
        string type,
        CancellationToken cancellationToken = default);
}
```

---

### 4.4 Preference Repository

#### IPreferenceRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for preference persistence.
/// </summary>
public interface IPreferenceRepository
{
    /// <summary>
    /// Adds or updates a preference.
    /// </summary>
    Task<Preference> UpsertAsync(
        Preference preference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a preference by identifier.
    /// </summary>
    Task<Preference?> GetByIdAsync(
        string preferenceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets preferences by category.
    /// </summary>
    Task<IReadOnlyList<Preference>> GetByCategoryAsync(
        string category,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches preferences by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Preference Preference, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);
}
```

---

### 4.5 Fact Repository

#### IFactRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for fact persistence.
/// </summary>
public interface IFactRepository
{
    /// <summary>
    /// Adds or updates a fact.
    /// </summary>
    Task<Fact> UpsertAsync(
        Fact fact,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a fact by identifier.
    /// </summary>
    Task<Fact?> GetByIdAsync(
        string factId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets facts by subject.
    /// </summary>
    Task<IReadOnlyList<Fact>> GetBySubjectAsync(
        string subject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches facts by vector similarity.
    /// </summary>
    Task<IReadOnlyList<(Fact Fact, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);
}
```

---

### 4.6 Relationship Repository

#### IRelationshipRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for relationship persistence.
/// </summary>
public interface IRelationshipRepository
{
    /// <summary>
    /// Adds or updates a relationship.
    /// </summary>
    Task<Relationship> UpsertAsync(
        Relationship relationship,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a relationship by identifier.
    /// </summary>
    Task<Relationship?> GetByIdAsync(
        string relationshipId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets relationships for an entity (source or target).
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetByEntityAsync(
        string entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets outgoing relationships from a source entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetBySourceEntityAsync(
        string sourceEntityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets incoming relationships to a target entity.
    /// </summary>
    Task<IReadOnlyList<Relationship>> GetByTargetEntityAsync(
        string targetEntityId,
        CancellationToken cancellationToken = default);
}
```

---

### 4.7 Reasoning Trace Repository

#### IReasoningTraceRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for reasoning trace persistence.
/// </summary>
public interface IReasoningTraceRepository
{
    /// <summary>
    /// Adds a reasoning trace.
    /// </summary>
    Task<ReasoningTrace> AddAsync(
        ReasoningTrace trace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a reasoning trace.
    /// </summary>
    Task<ReasoningTrace> UpdateAsync(
        ReasoningTrace trace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a trace by identifier.
    /// </summary>
    Task<ReasoningTrace?> GetByIdAsync(
        string traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists traces for a session.
    /// </summary>
    Task<IReadOnlyList<ReasoningTrace>> ListBySessionAsync(
        string sessionId,
        int limit = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches traces by task embedding similarity.
    /// </summary>
    Task<IReadOnlyList<(ReasoningTrace Trace, double Score)>> SearchByTaskVectorAsync(
        float[] taskEmbedding,
        bool? successFilter = null,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default);
}
```

---

### 4.8 Reasoning Step Repository

#### IReasoningStepRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for reasoning step persistence.
/// </summary>
public interface IReasoningStepRepository
{
    /// <summary>
    /// Adds a reasoning step.
    /// </summary>
    Task<ReasoningStep> AddAsync(
        ReasoningStep step,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets steps for a trace.
    /// </summary>
    Task<IReadOnlyList<ReasoningStep>> GetByTraceAsync(
        string traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a step by identifier.
    /// </summary>
    Task<ReasoningStep?> GetByIdAsync(
        string stepId,
        CancellationToken cancellationToken = default);
}
```

---

### 4.9 Tool Call Repository

#### IToolCallRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for tool call persistence.
/// </summary>
public interface IToolCallRepository
{
    /// <summary>
    /// Adds a tool call.
    /// </summary>
    Task<ToolCall> AddAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a tool call.
    /// </summary>
    Task<ToolCall> UpdateAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets tool calls for a step.
    /// </summary>
    Task<IReadOnlyList<ToolCall>> GetByStepAsync(
        string stepId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tool call by identifier.
    /// </summary>
    Task<ToolCall?> GetByIdAsync(
        string toolCallId,
        CancellationToken cancellationToken = default);
}
```

---

### 4.10 Schema Repository

#### ISchemaRepository
```csharp
namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for schema and index management.
/// </summary>
public interface ISchemaRepository
{
    /// <summary>
    /// Initializes the database schema (constraints, indexes).
    /// </summary>
    Task InitializeSchemaAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the schema is initialized.
    /// </summary>
    Task<bool> IsSchemaInitializedAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current schema version.
    /// </summary>
    Task<int?> GetSchemaVersionAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a schema migration.
    /// </summary>
    Task ApplyMigrationAsync(
        int targetVersion,
        CancellationToken cancellationToken = default);
}
```

---

## 5. Options and Configuration Models

All configuration models use records with init-only properties and sensible defaults.

### 5.1 Memory Options

#### MemoryOptions
```csharp
namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Root configuration for the memory system.
/// </summary>
public sealed record MemoryOptions
{
    /// <summary>
    /// Short-term memory configuration.
    /// </summary>
    public ShortTermMemoryOptions ShortTerm { get; init; } = new();

    /// <summary>
    /// Long-term memory configuration.
    /// </summary>
    public LongTermMemoryOptions LongTerm { get; init; } = new();

    /// <summary>
    /// Reasoning memory configuration.
    /// </summary>
    public ReasoningMemoryOptions Reasoning { get; init; } = new();

    /// <summary>
    /// Recall configuration.
    /// </summary>
    public RecallOptions Recall { get; init; } = RecallOptions.Default;

    /// <summary>
    /// Context budget configuration.
    /// </summary>
    public ContextBudget ContextBudget { get; init; } = ContextBudget.Default;

    /// <summary>
    /// Whether to enable GraphRAG integration.
    /// </summary>
    public bool EnableGraphRag { get; init; }

    /// <summary>
    /// Whether to enable automatic extraction after message save.
    /// </summary>
    public bool EnableAutoExtraction { get; init; } = true;
}
```

---

### 5.2 Short-Term Memory Options

#### ShortTermMemoryOptions
```csharp
namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for short-term memory.
/// </summary>
public sealed record ShortTermMemoryOptions
{
    /// <summary>
    /// Whether to generate embeddings for messages automatically.
    /// </summary>
    public bool GenerateEmbeddings { get; init; } = true;

    /// <summary>
    /// Default number of recent messages to retrieve.
    /// </summary>
    public int DefaultRecentMessageLimit { get; init; } = 10;

    /// <summary>
    /// Maximum number of messages to retrieve in a single query.
    /// </summary>
    public int MaxMessagesPerQuery { get; init; } = 100;

    /// <summary>
    /// Session strategy.
    /// </summary>
    public SessionStrategy SessionStrategy { get; init; } = SessionStrategy.PerConversation;
}

/// <summary>
/// Strategy for session scoping.
/// </summary>
public enum SessionStrategy
{
    /// <summary>
    /// One session per conversation.
    /// </summary>
    PerConversation,

    /// <summary>
    /// One session per day.
    /// </summary>
    PerDay,

    /// <summary>
    /// Persistent session per user.
    /// </summary>
    PersistentPerUser
}
```

---

### 5.3 Long-Term Memory Options

#### LongTermMemoryOptions
```csharp
namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for long-term memory.
/// </summary>
public sealed record LongTermMemoryOptions
{
    /// <summary>
    /// Whether to generate embeddings for entities automatically.
    /// </summary>
    public bool GenerateEntityEmbeddings { get; init; } = true;

    /// <summary>
    /// Whether to generate embeddings for facts automatically.
    /// </summary>
    public bool GenerateFactEmbeddings { get; init; } = true;

    /// <summary>
    /// Whether to generate embeddings for preferences automatically.
    /// </summary>
    public bool GeneratePreferenceEmbeddings { get; init; } = true;

    /// <summary>
    /// Minimum confidence threshold for persisting extracted items.
    /// </summary>
    public double MinConfidenceThreshold { get; init; } = 0.5;

    /// <summary>
    /// Whether to enable entity resolution and deduplication.
    /// </summary>
    public bool EnableEntityResolution { get; init; } = true;
}
```

---

### 5.4 Reasoning Memory Options

#### ReasoningMemoryOptions
```csharp
namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for reasoning memory.
/// </summary>
public sealed record ReasoningMemoryOptions
{
    /// <summary>
    /// Whether to generate embeddings for task descriptions automatically.
    /// </summary>
    public bool GenerateTaskEmbeddings { get; init; } = true;

    /// <summary>
    /// Whether to store tool call details.
    /// </summary>
    public bool StoreToolCalls { get; init; } = true;

    /// <summary>
    /// Maximum number of traces to retain per session.
    /// </summary>
    public int? MaxTracesPerSession { get; init; }
}
```

---

### 5.5 Recall Options

#### RecallOptions
```csharp
namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for memory recall operations.
/// </summary>
public sealed record RecallOptions
{
    /// <summary>
    /// Maximum recent messages to include.
    /// </summary>
    public int MaxRecentMessages { get; init; } = 10;

    /// <summary>
    /// Maximum semantically relevant messages to include.
    /// </summary>
    public int MaxRelevantMessages { get; init; } = 5;

    /// <summary>
    /// Maximum entities to include.
    /// </summary>
    public int MaxEntities { get; init; } = 10;

    /// <summary>
    /// Maximum preferences to include.
    /// </summary>
    public int MaxPreferences { get; init; } = 5;

    /// <summary>
    /// Maximum facts to include.
    /// </summary>
    public int MaxFacts { get; init; } = 10;

    /// <summary>
    /// Maximum reasoning traces to include.
    /// </summary>
    public int MaxTraces { get; init; } = 3;

    /// <summary>
    /// Maximum GraphRAG items to include.
    /// </summary>
    public int MaxGraphRagItems { get; init; } = 5;

    /// <summary>
    /// Minimum similarity score for semantic search (0.0 to 1.0).
    /// </summary>
    public double MinSimilarityScore { get; init; } = 0.7;

    /// <summary>
    /// Retrieval blend mode.
    /// </summary>
    public RetrievalBlendMode BlendMode { get; init; } = RetrievalBlendMode.Blended;

    /// <summary>
    /// Default singleton instance.
    /// </summary>
    public static RecallOptions Default { get; } = new();
}

/// <summary>
/// Mode for blending memory and GraphRAG retrieval.
/// </summary>
public enum RetrievalBlendMode
{
    /// <summary>
    /// Memory only.
    /// </summary>
    MemoryOnly,

    /// <summary>
    /// GraphRAG only.
    /// </summary>
    GraphRagOnly,

    /// <summary>
    /// Memory first, then GraphRAG.
    /// </summary>
    MemoryThenGraphRag,

    /// <summary>
    /// GraphRAG first, then memory.
    /// </summary>
    GraphRagThenMemory,

    /// <summary>
    /// Blended memory and GraphRAG.
    /// </summary>
    Blended
}
```

---

### 5.6 Context Budget

#### ContextBudget
```csharp
namespace Neo4j.AgentMemory.Abstractions.Options;

/// <summary>
/// Configuration for context budget limits.
/// </summary>
public sealed record ContextBudget
{
    /// <summary>
    /// Maximum total tokens for the assembled context.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Maximum total characters for the assembled context.
    /// </summary>
    public int? MaxCharacters { get; init; }

    /// <summary>
    /// Truncation strategy when budget is exceeded.
    /// </summary>
    public TruncationStrategy TruncationStrategy { get; init; } = TruncationStrategy.OldestFirst;

    /// <summary>
    /// Default singleton instance with no limits.
    /// </summary>
    public static ContextBudget Default { get; } = new();
}

/// <summary>
/// Strategy for truncating context when budget is exceeded.
/// </summary>
public enum TruncationStrategy
{
    /// <summary>
    /// Remove oldest items first.
    /// </summary>
    OldestFirst,

    /// <summary>
    /// Remove lowest-scoring items first.
    /// </summary>
    LowestScoreFirst,

    /// <summary>
    /// Proportionally reduce each section.
    /// </summary>
    Proportional,

    /// <summary>
    /// Fail if budget is exceeded.
    /// </summary>
    Fail
}
```

---

## 6. Design Decisions Summary

### 6.1 Key Choices Made

1. **C# Records for Domain Models**
   - Immutability by default
   - Value semantics for comparisons
   - Concise syntax with init-only properties
   - Appropriate for data transfer objects

2. **Required vs. Optional Fields**
   - Used `required` keyword for specification-mandated fields
   - Nullable reference types for optional fields
   - Default empty collections instead of null

3. **Repository Pattern**
   - Persistence-agnostic abstractions
   - Consistent method naming (UpsertAsync, GetByXAsync, SearchByVectorAsync)
   - Return tuples for scored results: `(Entity, double Score)`
   - Batch operations where appropriate

4. **Service Layer Separation**
   - IMemoryService as facade
   - Dedicated services per memory layer
   - Extraction pipeline separate from persistence
   - Context assembly as distinct concern

5. **Async-First Design**
   - All I/O operations are async
   - CancellationToken support throughout
   - Default parameters for optional cancellation tokens

6. **Strong Typing**
   - Enums for status, strategy, mode values
   - No stringly-typed APIs
   - Explicit types for embeddings (float[])

7. **Zero Dependencies**
   - No Neo4j.Driver in Abstractions
   - No Microsoft.Agents.* references
   - Pure .NET types only

8. **Provenance Support**
   - SourceMessageIds on extracted items
   - Traceability built into domain models
   - Metadata dictionaries for extensibility

### 6.2 Open Questions for Deckard Review

1. Should we add a `SessionRepository` for explicit session management?
2. Should embedding dimensions be validated at the interface level?
3. Should we define batch limits in the options or leave them implementation-specific?
4. Do we need a separate `IMemoryMergeService` interface for deduplication policies?
5. Should GraphRAG types live in Abstractions or only in the adapter?

---

## 7. Next Steps

1. **Deckard Review** — validate this design against architectural principles
2. **Scaffold Abstractions Package** — create project and copy these types
3. **Core Implementation Planning** — design orchestration services
4. **Repository Implementation Planning** — design Neo4j Cypher queries
5. **Test Strategy Definition** — plan unit and integration test coverage

---

**End of Design Document**
