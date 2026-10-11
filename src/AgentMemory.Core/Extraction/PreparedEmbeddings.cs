using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

internal sealed record PreparedEmbeddings(
    IReadOnlyDictionary<string, Entity> Entities,
    IReadOnlyList<PreparedFact> Facts,
    IReadOnlyList<PreparedPreference> Preferences,
    IReadOnlyList<IngestionItemOutcome> Outcomes);

internal sealed record PreparedFact(ExtractedFact Item, float[] Embedding);

internal sealed record PreparedPreference(ExtractedPreference Item, float[] Embedding);
