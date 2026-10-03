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
    /// <summary>The format this export is written in.</summary>
    public const string CurrentFormat = "agentmemory-export/1";

    /// <summary>The format, <see cref="CurrentFormat"/>.</summary>
    public string Format { get; init; } = CurrentFormat;

    /// <summary>The owner exported.</summary>
    public required string OwnerId { get; init; }

    /// <summary>When it was exported.</summary>
    public required DateTimeOffset ExportedAtUtc { get; init; }

    /// <summary>The owner's entities.</summary>
    public IReadOnlyList<Entity> Entities { get; init; } = [];

    /// <summary>The owner's facts, live and closed.</summary>
    public IReadOnlyList<Fact> Facts { get; init; } = [];

    /// <summary>The owner's preferences, live and closed.</summary>
    public IReadOnlyList<Preference> Preferences { get; init; } = [];

    /// <summary>The owner's relationships.</summary>
    public IReadOnlyList<Relationship> Relationships { get; init; } = [];

    /// <summary>Supersession: a closed fact (<see cref="MemoryLink.From"/>) and the fact that replaced it.</summary>
    public IReadOnlyList<MemoryLink> Supersessions { get; init; } = [];

    /// <summary>A fact (<see cref="MemoryLink.From"/>) and an entity it is about (the owner's, or a shared one).</summary>
    public IReadOnlyList<MemoryLink> About { get; init; } = [];
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
