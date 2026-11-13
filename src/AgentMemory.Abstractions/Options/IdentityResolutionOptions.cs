namespace AgentMemory.Abstractions.Options;

/// <summary>Controls evidence-based identity. Disabled means no new queries, decisions or model calls.</summary>
public sealed class IdentityResolutionOptions
{
    /// <summary>Opt into evidence-based identity; requires an identity-capable backend.</summary>
    public bool Enabled { get; set; }
    /// <summary>Maximum candidates fetched and considered per mention, before contextual work.</summary>
    public int MaxCandidates { get; set; } = 12;
    /// <summary>Maximum accepted entity observations in one ingestion; excess input fails before persistence.</summary>
    public int MaxMentionsPerIngestion { get; set; } = 64;
    /// <summary>Maximum UTF-8 bytes of source context sent to an adjudicator.</summary>
    public int MaxContextUtf8Bytes { get; set; } = 8_192;
    /// <summary>Maximum contextual model calls in one ingestion.</summary>
    public int MaxAdjudicationsPerIngestion { get; set; } = 2;
    /// <summary>Requested output-token cap per contextual call.</summary>
    public int AdjudicatorMaxOutputTokens { get; set; } = 512;
    /// <summary>Maximum time allowed for one contextual call.</summary>
    public TimeSpan AdjudicationTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Rules keyed by entity type. Unknown types use conservative review-only defaults.</summary>
    public IDictionary<string, EntityIdentityPolicy> Policies { get; set; } =
        new Dictionary<string, EntityIdentityPolicy>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The measured conversational policy set (Identity Lifecycle Benchmark, 2026-11): EVENT, PERSON and ORGANIZATION may
    /// be confirmed by checked contextual evidence (a store-aware writer's stored-entity reference or the adjudicator's
    /// quoted verdict) and nominate same-type entities from the extraction window's context turns for review. Every other
    /// type, places and calendar values included, stays review-only. Vetoes, host keys and ambiguity rules still apply.
    /// Existing policies for those three types keep their other settings. Does not enable identity. Returns this instance.
    /// </summary>
    public IdentityResolutionOptions ApplyConversationalPolicies()
    {
        foreach (var type in ConversationalTypes)
        {
            var current = Policies.TryGetValue(type, out var existing) && existing is not null ? existing : new EntityIdentityPolicy();
            Policies[type] = current with { AllowContextualConfirmation = true, ReviewContextRootedCandidates = true };
        }
        return this;
    }

    private static readonly string[] ConversationalTypes = ["EVENT", "PERSON", "ORGANIZATION"];
}

/// <summary>Domain rules for one entity type. Similarity thresholds only select review candidates.</summary>
public sealed record EntityIdentityPolicy
{
    /// <summary>Policy version persisted with identity decisions.</summary>
    public string Version { get; init; } = "1";
    /// <summary>Components of one complete, verified composite key in the same identifier namespace.</summary>
    public IReadOnlyList<string> IdentityProperties { get; init; } = Array.Empty<string>();
    /// <summary>Additional entity-type labels that the host declares compatible with this type.</summary>
    public IReadOnlyList<string> CompatibleTypes { get; init; } = Array.Empty<string>();
    /// <summary>Attribute names eligible as untrusted contextual data; they are not identity proof.</summary>
    public IReadOnlyList<string> ContextProperties { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Exact identity-name/verified-alias comparison. Ordinal by default; only Ordinal or
    /// OrdinalIgnoreCase is supported. A backend must explicitly support a requested comparison.
    /// Candidate review remains broader and never authorises identity by itself.
    /// </summary>
    public StringComparison IdentityNameComparison { get; init; } = StringComparison.Ordinal;
    /// <summary>Optional verified namespace within which the host guarantees name uniqueness.</summary>
    public string? UniqueNameNamespace { get; init; }
    /// <summary>Fuzzy similarity at or above which a candidate can be reviewed; never confirms identity.</summary>
    public double FuzzyReviewThreshold { get; init; } = 0.85;
    /// <summary>Cosine similarity at or above which a candidate can be reviewed; never confirms identity.</summary>
    public double SemanticReviewThreshold { get; init; } = 0.8;
    /// <summary>Context similarity at or above which a candidate can be reviewed; never confirms identity.</summary>
    public double ContextReviewThreshold { get; init; } = 0.8;
    /// <summary>Permit a valid contextual result to confirm; deterministic conflicts still veto it.</summary>
    public bool AllowContextualConfirmation { get; init; }
    /// <summary>
    /// Nominate same-type entities observed in the extraction window's read-only context turns
    /// (<c>ExtractionOptions.ExtractionContextTurns</c>) for review, so a differently named mention of a just-mentioned
    /// entity ("the cutover" after "the go-live") reaches the contextual adjudicator with both turns. Review only, never
    /// confirmation; vetoes still apply. Default false: no context IDs are sent and no extra query runs. Requires a backend
    /// with <c>ContextRootedLookup</c>; costs one bounded query per mention and possibly an adjudication call.
    /// </summary>
    public bool ReviewContextRootedCandidates { get; init; }
}
