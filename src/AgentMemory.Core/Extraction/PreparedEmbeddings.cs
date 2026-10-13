using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

internal sealed record PreparedEmbeddings(
    IReadOnlyDictionary<string, Entity> Entities,
    IReadOnlyList<PreparedFact> Facts,
    IReadOnlyList<PreparedPreference> Preferences,
    IReadOnlyList<IngestionItemOutcome> Outcomes);

internal sealed record PreparedFact(ExtractedFact Item, float[] Embedding)
{
    // Merging a statement must not merge away the other stored memories it explicitly replaces.
    public IReadOnlyList<(string Id, bool IsCorrection)> AdditionalClosings { get; init; } = [];

    public IEnumerable<ExtractedFact> WithClosingProposals()
    {
        yield return Item;
        foreach (var closing in AdditionalClosings)
            yield return Item with { ReplacesId = closing.Id, ReplacementIsCorrection = closing.IsCorrection };
    }
}

internal sealed record PreparedPreference(ExtractedPreference Item, float[] Embedding);
