namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// RECALL-NAMESAKE-LEAK: one entity a recalled fact or message is about (a fact bound to it by identity or linked to it,
/// a message mentioning it), with what the store knows it as. Data only, read for <see cref="Options.RecallOptions.NamesakeFilter"/>.
/// </summary>
public sealed record RecalledItemEntity
{
    /// <summary>The recalled fact's or message's id.</summary>
    public required string ItemId { get; init; }
    /// <summary>The entity it is about: its name, aliases, description and subtype are read.</summary>
    public required Entity Entity { get; init; }
    /// <summary>The names the entity's bound facts used (its kept names).</summary>
    public IReadOnlyList<string> KeptNames { get; init; } = Array.Empty<string>();
    /// <summary>What its own live facts say it is ("is the designer on Atlas", "has dog").</summary>
    public IReadOnlyList<string> KnownAs { get; init; } = Array.Empty<string>();
    /// <summary>The text of a few of its source messages, where a relation may be said ("My dog Luna is a border collie").</summary>
    public IReadOnlyList<string> SourceTexts { get; init; } = Array.Empty<string>();
}
