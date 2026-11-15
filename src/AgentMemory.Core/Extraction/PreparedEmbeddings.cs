using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

internal sealed record PreparedEmbeddings(
    IReadOnlyDictionary<string, Entity> Entities,
    IReadOnlyList<PreparedFact> Facts,
    IReadOnlyList<PreparedPreference> Preferences,
    IReadOnlyList<IngestionItemOutcome> Outcomes);

internal sealed record PreparedFact(ExtractedFact Item, float[] Embedding)
{
    // Merging a statement must not merge away the other stored memories it explicitly replaces. It starts with the ones
    // the writer named beside ReplacesId (the other half of a connection written with its statement fact).
    public IReadOnlyList<(string Id, bool IsCorrection)> AdditionalClosings { get; init; } =
        [.. Item.AlsoReplacesIds.Select(id => (id, Item.ReplacementIsCorrection))];

    public IEnumerable<ExtractedFact> WithClosingProposals()
    {
        yield return Item;
        foreach (var closing in AdditionalClosings)
            yield return Item with { ReplacesId = closing.Id, ReplacementIsCorrection = closing.IsCorrection };
    }
}

internal sealed record PreparedPreference(ExtractedPreference Item, float[] Embedding);
