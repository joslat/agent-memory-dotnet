namespace AgentMemory.Abstractions.Domain;

/// <summary>G3 (PLAN 40.47): what erasing an owner deleted, by node label.</summary>
public sealed record OwnerErasure
{
    /// <summary>The owner erased.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Nodes deleted, by label (Fact, Entity, Preference, Message, Conversation, …).</summary>
    public required IReadOnlyDictionary<string, long> Deleted { get; init; }

    /// <summary>Every node deleted.</summary>
    public long Total => Deleted.Values.Sum();
}

/// <summary>
/// G3 (PLAN 40.47): an owner's long-term memory, live and closed, with the links between its parts, as plain data
/// (format <c>agentmemory-export/1</c>). Embeddings are left out: an import embeds again with the receiving store's model.
/// Conversations and messages are not exported (they are the transcript, not the memory).
/// </summary>
public sealed record OwnerMemoryExport
{
    /// <summary>The format of an export without identity state.</summary>
    public const string CurrentFormat = "agentmemory-export/1";

    /// <summary>
    /// The format of an export that carries identity state (<see cref="IdentityState"/>); an importer that knows only
    /// <see cref="CurrentFormat"/> refuses it rather than dropping that state.
    /// </summary>
    public const string IdentityFormat = "agentmemory-export/2";

    /// <summary>The format, <see cref="CurrentFormat"/> or <see cref="IdentityFormat"/>.</summary>
    public string Format { get; init; } = CurrentFormat;

    /// <summary>The owner exported.</summary>
    public required string OwnerId { get; init; }

    /// <summary>When it was exported.</summary>
    public required DateTimeOffset ExportedAtUtc { get; init; }

    /// <summary>The owner's live entities (merged-away and forgotten ones are left out).</summary>
    public IReadOnlyList<Entity> Entities { get; init; } = [];

    /// <summary>The owner's facts, live and closed.</summary>
    public IReadOnlyList<Fact> Facts { get; init; } = [];

    /// <summary>The owner's preferences, live and closed.</summary>
    public IReadOnlyList<Preference> Preferences { get; init; } = [];

    /// <summary>The owner's relationships between exported or shared entities.</summary>
    public IReadOnlyList<Relationship> Relationships { get; init; } = [];

    /// <summary>Supersession: a closed fact (<see cref="MemoryLink.From"/>) and the fact that replaced it.</summary>
    public IReadOnlyList<MemoryLink> Supersessions { get; init; } = [];

    /// <summary>A fact (<see cref="MemoryLink.From"/>) and an entity it is about (an exported one, or a shared one).</summary>
    public IReadOnlyList<MemoryLink> About { get; init; } = [];

    /// <summary>
    /// With evidence-based identity storage: each exported observation and the identity a confirmed decision resolved it
    /// to (<see cref="MemoryLink.From"/> observation, <see cref="MemoryLink.To"/> identity), so a reader can tell which
    /// exported entities are one identity. Import folds each observation into its identity, which keeps the observation's
    /// name as an alias. Null, and not serialized, when the store keeps no identity state.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MemoryLink>? IdentityResolutions { get; init; }

    /// <summary>
    /// True when the store keeps identity state the export does not carry: decisions, review history, evidence and open
    /// proposals were left out. Import brings back the current state: each confirmed identity as one entity, an open
    /// proposal's two entities apart, and nothing joined to what the destination holds. False, and not serialized,
    /// otherwise, including when <see cref="IdentityState"/> carries that state.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IdentityStateOmitted { get; init; }

    /// <summary>
    /// ILC-30: the owner's identity state, so a moved or restored store behaves as the original: decisions with their
    /// evidence and quotes, review history, open proposals, fact bindings, host assertions and relationship observations,
    /// and the retired entities they point at. Messages stay out: decisions keep their quotes, and on import every source
    /// message id becomes an imported marker of the destination's own, so the original owner's messages are never the
    /// copy's sources. Null, and not serialized, when the store keeps no identity state.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public OwnerIdentityState? IdentityState { get; init; }
}

/// <summary>
/// ILC-30: an owner's identity records and links as stored, each property with its type, so an import can recreate them
/// for another owner with every reference rewritten.
/// </summary>
public sealed record OwnerIdentityState
{
    /// <summary>The identity records: decisions, revisions, fact bindings, evidence assertions, relationship observations.</summary>
    public IReadOnlyList<OwnerGraphNode> Nodes { get; init; } = [];

    /// <summary>The links that tie them to each other and to the exported entities and facts.</summary>
    public IReadOnlyList<OwnerGraphEdge> Edges { get; init; } = [];

    /// <summary>The identity properties of exported entities (observation key, evidence version, calendar key).</summary>
    public IReadOnlyList<OwnerGraphNode> EntityProperties { get; init; } = [];

    /// <summary>
    /// The owner's merged-away or forgotten entities that identity records still point at (an open proposal's candidate
    /// that was retired later, say), so those links come back to a retired copy rather than to nothing or to the source.
    /// </summary>
    public IReadOnlyList<Entity> RetiredEntities { get; init; } = [];

    /// <summary>How each of <see cref="RetiredEntities"/> was retired: <c>invalidated_at</c> and <c>merged_into</c>, by id.</summary>
    public IReadOnlyList<OwnerGraphNode> Retirements { get; init; } = [];

    /// <summary>
    /// The confirmed decisions whose target still matched its fingerprint at export. Import gives these the fingerprint of
    /// their imported target; any other keeps a fingerprint no target matches, so it needs re-evaluation, as in the source.
    /// </summary>
    public IReadOnlyList<string> CurrentConfirmations { get; init; } = [];
}

/// <summary>One stored record: its label, its id and its properties.</summary>
public sealed record OwnerGraphNode
{
    /// <summary>The record's kind, e.g. IdentityDecision, or Entity for <see cref="OwnerIdentityState.EntityProperties"/>.</summary>
    public required string Label { get; init; }

    /// <summary>The record's id.</summary>
    public required string Key { get; init; }

    /// <summary>Its properties, each with its type.</summary>
    public IReadOnlyDictionary<string, OwnerGraphValue> Properties { get; init; } = new Dictionary<string, OwnerGraphValue>();
}

/// <summary>One stored link between two records, by kind and id.</summary>
public sealed record OwnerGraphEdge
{
    /// <summary>The link's type, e.g. RESOLVES_TO.</summary>
    public required string Type { get; init; }

    /// <summary>The kind of record it starts at.</summary>
    public required string FromLabel { get; init; }

    /// <summary>The id of the record it starts at.</summary>
    public required string From { get; init; }

    /// <summary>The kind of record it points to.</summary>
    public required string ToLabel { get; init; }

    /// <summary>The id of the record it points to.</summary>
    public required string To { get; init; }

    /// <summary>Its properties, each with its type.</summary>
    public IReadOnlyDictionary<string, OwnerGraphValue> Properties { get; init; } = new Dictionary<string, OwnerGraphValue>();
}

/// <summary>A stored value with its type, so an integer stays an integer and a timestamp stays a timestamp.</summary>
public sealed record OwnerGraphValue
{
    /// <summary>Text.</summary>
    public const string String = "string";
    /// <summary>A 64-bit integer.</summary>
    public const string Integer = "integer";
    /// <summary>A floating-point number.</summary>
    public const string Number = "number";
    /// <summary>True or false.</summary>
    public const string Boolean = "boolean";
    /// <summary>A timestamp with its offset, in ISO 8601.</summary>
    public const string DateTime = "datetime";
    /// <summary>A list of values.</summary>
    public const string List = "list";

    /// <summary>One of the kinds above.</summary>
    public required string Kind { get; init; }

    /// <summary>The value of a <see cref="String"/> or <see cref="DateTime"/>.</summary>
    public string? Text { get; init; }

    /// <summary>The value of an <see cref="Integer"/>.</summary>
    public long? Long { get; init; }

    /// <summary>The value of a <see cref="Number"/>.</summary>
    public double? Double { get; init; }

    /// <summary>The value of a <see cref="Boolean"/>.</summary>
    public bool? Flag { get; init; }

    /// <summary>The items of a <see cref="List"/>.</summary>
    public IReadOnlyList<OwnerGraphValue>? Items { get; init; }
}

/// <summary>A link between two memories, by id.</summary>
/// <param name="From">The memory the link starts at.</param>
/// <param name="To">The memory it points to.</param>
public sealed record MemoryLink(string From, string To);

/// <summary>G3: what an import wrote, and the id each exported memory received.</summary>
public sealed record OwnerImportResult
{
    /// <summary>The owner imported into.</summary>
    public required string OwnerId { get; init; }

    /// <summary>Memories written, by kind (entities, facts, preferences, relationships, links).</summary>
    public required IReadOnlyDictionary<string, int> Written { get; init; }

    /// <summary>Each exported id and the id it received.</summary>
    public required IReadOnlyDictionary<string, string> Ids { get; init; }
}
