using System.Text.Json.Serialization;

namespace AgentMemory.Validation;

/// <summary>
/// A validation pack (format <c>agentmemory-pack/1</c>): a schema, conversations with what extraction yields for each
/// message, the storage they must leave, and the questions they must answer. Deterministic: the pack plays the model, and
/// the run checks the memory layer (persistence, closing, isolation, recall), never a generated answer.
/// </summary>
public sealed record ValidationPack
{
    /// <summary>The format this pack is written in; <see cref="ValidationPackReader.Format"/>.</summary>
    public required string Format { get; init; }

    /// <summary>A stable id, such as <c>core.semantic.changes</c>.</summary>
    public required string Id { get; init; }

    /// <summary>What the pack checks, in a sentence.</summary>
    public required string Title { get; init; }

    /// <summary>The memory kinds the pack exercises (<see cref="PackKinds"/>).</summary>
    public IReadOnlyList<string> Covers { get; init; } = [];

    /// <summary>The configuration the pack runs under.</summary>
    public PackOptions Options { get; init; } = new();

    /// <summary>The ontology the pack's data keeps to.</summary>
    public PackSchema Schema { get; init; } = new();

    /// <summary>The owners the pack writes as. At least two: isolation is checked on every question.</summary>
    public IReadOnlyList<string> Owners { get; init; } = [];

    /// <summary>The conversations, stored and extracted in order.</summary>
    public IReadOnlyList<PackSession> Sessions { get; init; } = [];

    /// <summary>What the store must hold once every session is in.</summary>
    public IReadOnlyList<PackStorageCheck> Storage { get; init; } = [];

    /// <summary>The questions, asked after every session is in.</summary>
    public IReadOnlyList<PackQuestion> Questions { get; init; } = [];
}

/// <summary>The memory kinds a pack covers and a question's answer lives in.</summary>
public static class PackKinds
{
    /// <summary>Every kind a pack may name.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "facts", "entities", "relationships", "preferences", "messages", "working",
    };
}

/// <summary>A pack's configuration: a preset and named switches.</summary>
public sealed record PackOptions
{
    /// <summary><c>default</c> (the library's defaults) or <c>conversational</c> (<c>MemoryOptions.CreateConversational()</c>).</summary>
    public string Preset { get; init; } = "default";

    /// <summary>Switches applied after the preset, by path on <c>MemoryOptions</c>: <c>"Extraction.BitemporalChanges": true</c>.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> Set { get; init; } =
        new Dictionary<string, System.Text.Json.JsonElement>();
}

/// <summary>The ontology: entity types and predicates the data may use.</summary>
public sealed record PackSchema
{
    /// <summary>Entity types the pack's entities may have.</summary>
    public IReadOnlyList<string> EntityTypes { get; init; } = [];

    /// <summary>Predicates the pack's facts may use.</summary>
    public IReadOnlyList<PackPredicate> Predicates { get; init; } = [];

    /// <summary>Relationship types the pack's relationships may use.</summary>
    public IReadOnlyList<string> RelationshipTypes { get; init; } = [];
}

/// <summary>A predicate of the pack's ontology.</summary>
public sealed record PackPredicate
{
    /// <summary>The predicate as facts use it.</summary>
    public required string Name { get; init; }

    /// <summary>One value at a time (a new value replaces the old one), as documentation for the reader.</summary>
    public bool Single { get; init; }
}

/// <summary>A conversation.</summary>
public sealed record PackSession
{
    /// <summary>Whose conversation it is.</summary>
    public required string Owner { get; init; }

    /// <summary>The session id, unique in the pack.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Taught for everyone (<c>ExtractionRequest.ShareWithEveryone</c>): what it says is stored with no owner, recalled by
    /// every owner and listed in nobody's profile. <see cref="Owner"/> is who taught it.
    /// </summary>
    public bool Shared { get; init; }

    /// <summary>The messages, in order.</summary>
    public IReadOnlyList<PackMessage> Messages { get; init; } = [];

    /// <summary>
    /// Reasoning traces recorded in this conversation (40.72: the procedural and reasoning memory types): how a task was
    /// done, step by step, and how it went. Written after the messages, each at its own time.
    /// </summary>
    public IReadOnlyList<PackTrace> Traces { get; init; } = [];
}

/// <summary>A reasoning trace: the task, its steps, its outcome; a <c>procedure</c> is promoted to a reusable procedure.</summary>
public sealed record PackTrace
{
    /// <summary>When the trace was recorded.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The task the agent worked on.</summary>
    public required string Task { get; init; }

    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<PackTraceStep> Steps { get; init; } = [];

    /// <summary>How it went.</summary>
    public string? Outcome { get; init; }

    /// <summary>Whether it worked; null when not known.</summary>
    public bool? Success { get; init; }

    /// <summary><c>episode</c> (the default: something done once) or <c>procedure</c> (promoted: how this is done).</summary>
    public string Kind { get; init; } = "episode";
}

/// <summary>One step of a trace.</summary>
public sealed record PackTraceStep
{
    /// <summary>What the agent thought.</summary>
    public string? Thought { get; init; }

    /// <summary>What it did.</summary>
    public string? Action { get; init; }

    /// <summary>What it saw.</summary>
    public string? Observation { get; init; }
}

/// <summary>A message and what extraction yields for it.</summary>
public sealed record PackMessage
{
    /// <summary>When it was said: the store's clock is moved here before it is stored and extracted.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    public string Role { get; init; } = "user";

    /// <summary>What was said.</summary>
    public required string Text { get; init; }

    /// <summary>The entities extraction yields.</summary>
    public IReadOnlyList<PackEntity> Entities { get; init; } = [];

    /// <summary>The facts extraction yields.</summary>
    public IReadOnlyList<PackFact> Facts { get; init; } = [];

    /// <summary>The relationships extraction yields.</summary>
    public IReadOnlyList<PackRelationship> Relationships { get; init; } = [];

    /// <summary>The preferences extraction yields.</summary>
    public IReadOnlyList<PackPreference> Preferences { get; init; } = [];
}

/// <summary>An extracted entity.</summary>
public sealed record PackEntity
{
    /// <summary>Its name.</summary>
    public required string Name { get; init; }

    /// <summary>Its type, one of the schema's.</summary>
    public required string Type { get; init; }

    /// <summary>Other names it goes by.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>An extracted fact.</summary>
public sealed record PackFact
{
    /// <summary>Subject.</summary>
    [JsonPropertyName("s")]
    public required string Subject { get; init; }

    /// <summary>Predicate, one of the schema's.</summary>
    [JsonPropertyName("p")]
    public required string Predicate { get; init; }

    /// <summary>Object.</summary>
    [JsonPropertyName("o")]
    public required string Object { get; init; }

    /// <summary>From when it holds.</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>Until when it holds.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>When the event happened.</summary>
    public DateTimeOffset? OccurredOn { get; init; }

    /// <summary>The precision of the dates given: <c>day</c>, <c>month</c>, <c>year</c>.</summary>
    public string? Precision { get; init; }

    /// <summary>The value this one replaces, as the speaker named it.</summary>
    public string? Replaces { get; init; }
}

/// <summary>An extracted relationship.</summary>
public sealed record PackRelationship
{
    /// <summary>The source entity's name.</summary>
    public required string Source { get; init; }

    /// <summary>The relationship type, one of the schema's.</summary>
    public required string Type { get; init; }

    /// <summary>The target entity's name.</summary>
    public required string Target { get; init; }
}

/// <summary>An extracted preference.</summary>
public sealed record PackPreference
{
    /// <summary>Its category.</summary>
    public required string Category { get; init; }

    /// <summary>What is preferred.</summary>
    public required string Text { get; init; }

    /// <summary>The preference this one replaces.</summary>
    public string? Replaces { get; init; }
}

/// <summary>
/// What the store must hold. Exactly one of <see cref="Fact"/> (<c>"subject | predicate | object"</c>) and
/// <see cref="Entity"/> (a name) is set.
/// </summary>
public sealed record PackStorageCheck
{
    /// <summary>Whose store.</summary>
    public required string Owner { get; init; }

    /// <summary>A fact, <c>"subject | predicate | object"</c>.</summary>
    public string? Fact { get; init; }

    /// <summary>An entity, by name.</summary>
    public string? Entity { get; init; }

    /// <summary>
    /// For a fact: <c>live</c>, <c>closed</c> (stored and closed, none live) or <c>absent</c>. For an entity: <c>live</c>
    /// (an entity of that name), <c>absent</c>, or <c>alias</c> (the name finds another entity, <see cref="Of"/>).
    /// </summary>
    public required string State { get; init; }

    /// <summary>For a closed fact: why it was closed (<c>change</c>, <c>correction</c>), when the pack cares.</summary>
    public string? ClosedAs { get; init; }

    /// <summary>For an entity in state <c>alias</c>: the name of the entity the alias finds.</summary>
    public string? Of { get; init; }
}

/// <summary>A question, and what its recall must and must not contain.</summary>
public sealed record PackQuestion
{
    /// <summary>A stable id, unique in the pack.</summary>
    public required string Id { get; init; }

    /// <summary>Who asks.</summary>
    public required string Owner { get; init; }

    /// <summary>When it is asked: the store's clock, and the reference for a date in the question.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The question.</summary>
    public required string Ask { get; init; }

    /// <summary>
    /// The pack session it is asked in, the asker's own: messages are recalled from the current session only (recent and
    /// relevant). Null asks in a fresh session, where only long-term memory answers.
    /// </summary>
    public string? Session { get; init; }

    /// <summary>Recall as of these instants instead of live; null recalls live.</summary>
    public PackAsOf? AsOf { get; init; }

    /// <summary>Items the recalled context must contain.</summary>
    public IReadOnlyList<PackItem> Expect { get; init; } = [];

    /// <summary>Items it must not contain.</summary>
    public IReadOnlyList<PackItem> Exclude { get; init; } = [];

    /// <summary>The memory kinds that hold the answer: the gold a router is scored on (40.61). Recorded, not checked.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];
}

/// <summary>The two clocks of an as-of question; either defaults to the question's <see cref="PackQuestion.At"/>.</summary>
public sealed record PackAsOf
{
    /// <summary>The time the question is about.</summary>
    public DateTimeOffset? Valid { get; init; }

    /// <summary>What was believed then.</summary>
    public DateTimeOffset? System { get; init; }
}

/// <summary>
/// An item in a recalled context. Exactly one is set: a fact (<c>"s | p | o"</c>), an entity name, a relationship
/// (<c>"Source -[TYPE]-> Target"</c>), a preference's text, a message's text, or text in the working-memory block.
/// </summary>
public sealed record PackItem
{
    /// <summary>A fact, <c>"subject | predicate | object"</c>.</summary>
    public string? Fact { get; init; }

    /// <summary>An entity, by name or alias.</summary>
    public string? Entity { get; init; }

    /// <summary>A relationship, <c>"Source -[TYPE]-> Target"</c>.</summary>
    public string? Relationship { get; init; }

    /// <summary>Text a recalled preference contains.</summary>
    public string? Preference { get; init; }

    /// <summary>Text a recalled message contains.</summary>
    public string? Message { get; init; }

    /// <summary>Text the working-memory block contains.</summary>
    public string? Working { get; init; }

    /// <summary>The item as the report prints it.</summary>
    public override string ToString() =>
        Fact is not null ? $"fact '{Fact}'"
        : Entity is not null ? $"entity '{Entity}'"
        : Relationship is not null ? $"relationship '{Relationship}'"
        : Preference is not null ? $"preference '{Preference}'"
        : Message is not null ? $"message '{Message}'"
        : Working is not null ? $"working '{Working}'"
        : "(empty item)";
}
