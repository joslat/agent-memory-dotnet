namespace AgentMemory.LongMemEval;

/// <summary>
/// PLAN 40.10. Which memory configuration a prepared pair measures.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sealed"/> is the harness's own profile: every option pinned to the path the sealed August measurements
/// took, so they stay comparable. It is the default and byte-identical to every run before this option existed.
/// </para>
/// <para>
/// <see cref="Defaults"/> and <see cref="Conversational"/> measure what a user gets: the library's shipped defaults, or
/// those plus the Conversational preset (<c>MemoryOptions.CreateConversational()</c> and
/// <c>LlmExtractionOptions.ApplyConversational()</c>, the preset objects themselves, not a copy of their contents).
/// Comparing those two is the question a default flip asks.
/// </para>
/// </remarks>
internal enum LongMemEvalPreset
{
    Sealed,
    Defaults,
    Conversational,
}

internal static class LongMemEvalPresets
{
    internal const string Option = "--preset";

    internal static LongMemEvalPreset Parse(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "sealed" => LongMemEvalPreset.Sealed,
        "defaults" => LongMemEvalPreset.Defaults,
        "conversational" => LongMemEvalPreset.Conversational,
        _ => throw new ArgumentException($"{Option} must be one of: sealed, defaults, conversational."),
    };

    internal static string Token(LongMemEvalPreset preset) => preset.ToString().ToLowerInvariant();

    /// <summary>
    /// The extraction identity a corpus is sealed under: a preset changes what is extracted and how it is written, so a
    /// store built under one is never reused under another. Sealed leaves the identity unchanged.
    /// </summary>
    internal static string Seal(string extractionIdentity, LongMemEvalPreset preset) =>
        preset == LongMemEvalPreset.Sealed ? extractionIdentity : $"{extractionIdentity}+preset:{Token(preset)}";

    /// <summary>
    /// What a prepared pair can and cannot exercise of the Conversational preset. Preparation extracts many sessions in
    /// one batch, so the per-turn gates never run, and a question is asked once, so nothing is held for a later turn.
    /// </summary>
    internal static object Coverage(LongMemEvalPreset preset) => preset != LongMemEvalPreset.Conversational
        ? new { exercised = Array.Empty<string>(), notExercised = Array.Empty<string>() }
        : new
        {
            exercised = new[]
            {
                "SupersedeReplacedFacts", "RenameOnCorrectedName", "CanonicalFactSubjects", "DeduplicateWithinExtraction",
                "LinkFactsToEntities", "EntityResolution.EnablePartialNameMatch", "EntityResolution.TypeStrictFiltering=false",
                "Recall.MaxRelationships=5", "ResolveTemporalQueries", "FanOut", "Isolation=StrictMultiTenant",
                "WorkingMemory.RecentTopicsDays=7", "EmbeddingCacheCapacity", "UseAccessTrackingQueue",
                "SkipEscalationWhenOwnerHasNoRows", "TemporalValidity=Extract", "MarkCorrections", "OwnPreferencesOnly",
                "CaptureEventCompanions",
            },
            notExercised = new[]
            {
                "SkipPlainQuestions (per-turn gate; preparation extracts in batches)",
                "SkipUninformativeTurns (per-turn gate)",
                "DeferQuestionTurns (Agent Framework; no later turn to hold for)",
                "UseUnifiedExtraction (the harness selects its own extractor, the same in every arm)",
            },
        };
}
