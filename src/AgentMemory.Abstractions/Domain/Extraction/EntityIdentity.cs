namespace AgentMemory.Abstractions.Domain;

/// <summary>The identity action, distinct from similarity and retrieval relevance.</summary>
public enum IdentityDecisionKind
{
    /// <summary>Use an existing identity on the stated evidence.</summary>
    Confirmed,
    /// <summary>Preserve a separate identity with a reviewable proposal.</summary>
    Pending,
    /// <summary>Preserve a separate identity without an admissible proposal.</summary>
    New
}

/// <summary>Semantic role of an identity subject, distinct from its POLE entity-type label.</summary>
public enum IdentitySubjectKind
{
    /// <summary>The role has not been established.</summary>
    Unknown,
    /// <summary>A reference, including a product whose name happens to look like a date.</summary>
    Reference,
    /// <summary>A Gregorian calendar-day value.</summary>
    CalendarDate,
    /// <summary>A Gregorian interval with calendar-day endpoints.</summary>
    CalendarInterval
}

/// <summary>A semantic-kind assertion supplied by a host authority, never extracted from Attributes.</summary>
public sealed record VerifiedIdentityKind
{
    /// <summary>The asserted semantic role.</summary>
    public required IdentitySubjectKind Kind { get; init; }
    /// <summary>Host authority responsible for this assertion.</summary>
    public required string Authority { get; init; }
    /// <summary>Stable source reference supporting it.</summary>
    public required string SourceId { get; init; }
}

/// <summary>
/// An identity assertion supplied by an application authority, not copied from extractor Attributes.
/// Namespace defines the identifier's meaning; Values contain the configured composite key.
/// </summary>
public sealed record VerifiedIdentityKey
{
    /// <summary>Identifier namespace, for example the company's employee directory.</summary>
    public required string Namespace { get; init; }
    /// <summary>Host authority responsible for the assertion.</summary>
    public required string Authority { get; init; }
    /// <summary>Stable source reference that makes the assertion auditable.</summary>
    public required string SourceId { get; init; }
    /// <summary>Exact, host-normalised component values; strings are compared ordinally.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
}

/// <summary>An alias whose target and namespace have been verified by the host or a recorded review.</summary>
public sealed record VerifiedIdentityAlias
{
    /// <summary>Alias surface form.</summary>
    public required string Surface { get; init; }
    /// <summary>Namespace in which the alias was confirmed.</summary>
    public required string Namespace { get; init; }
    /// <summary>Source or decision reference supporting the alias.</summary>
    public required string SourceId { get; init; }
}

/// <summary>Candidate-generation signals. None is a probability or proof of identity.</summary>
public sealed record IdentityCandidateSignals
{
    /// <summary>Optional fuzzy name similarity in [0,1].</summary>
    public double? FuzzySimilarity { get; init; }
    /// <summary>Optional raw cosine similarity in [-1,1].</summary>
    public double? SemanticSimilarity { get; init; }
    /// <summary>Optional context similarity in [0,1], used only to select candidates for review.</summary>
    public double? ContextSimilarity { get; init; }
    /// <summary>
    /// The backend found the candidate observed in a read-only context turn of the current extraction window. Selects it
    /// for review only when the type policy opts in (<c>ReviewContextRootedCandidates</c>); never identity proof.
    /// </summary>
    public bool ContextRooted { get; init; }
}

/// <summary>A scoped candidate plus evidence supplied by an identity-capable backend or host.</summary>
public sealed record IdentityCandidate
{
    /// <summary>The candidate entity; the resolver rechecks type and visibility.</summary>
    public required Entity Entity { get; init; }
    /// <summary>Optional independently verified semantic role from the backend or host.</summary>
    public VerifiedIdentityKind? VerifiedKind { get; init; }
    /// <summary>False when bounded hydration omitted active identity evidence; it cannot authorise identity.</summary>
    public bool EvidenceComplete { get; init; } = true;
    /// <summary>Backend evidence revision, for a persistence boundary to revalidate.</summary>
    public long EvidenceVersion { get; init; }
    /// <summary>Separate candidate-generation scores; correlated signals remain correlated.</summary>
    public IdentityCandidateSignals Signals { get; init; } = new();
    /// <summary>Active verified keys; retired keys must not be returned as active identity.</summary>
    public IReadOnlyList<VerifiedIdentityKey> VerifiedKeys { get; init; } = Array.Empty<VerifiedIdentityKey>();
    /// <summary>Backed aliases, distinct from the entity's unverified surface forms.</summary>
    public IReadOnlyList<VerifiedIdentityAlias> VerifiedAliases { get; init; } = Array.Empty<VerifiedIdentityAlias>();
    /// <summary>Verified namespace for a policy that declares names unique within it.</summary>
    public string? NameNamespace { get; init; }
}

/// <summary>A source fragment and its root origins, allowing correlated evidence to stay identifiable.</summary>
public sealed record IdentitySourceFragment
{
    /// <summary>Source message or observation identifier.</summary>
    public required string SourceId { get; init; }
    /// <summary>Source text, treated as untrusted data by contextual adjudication.</summary>
    public required string Text { get; init; }
    /// <summary>Original roots; a summary and its source must not become two independent proofs.</summary>
    public IReadOnlyList<string> OriginIds { get; init; } = Array.Empty<string>();
}

/// <summary>Provenance for a name namespace explicitly asserted by an application authority.</summary>
public sealed record VerifiedIdentityNameNamespace
{
    /// <summary>The host-controlled namespace, compared ordinally.</summary>
    public required string Namespace { get; init; }
    /// <summary>Authority responsible for the namespace assertion.</summary>
    public required string Authority { get; init; }
    /// <summary>Stable source reference supporting the assertion.</summary>
    public required string SourceId { get; init; }
}

/// <summary>Context supplied separately from the model's extracted entity.</summary>
public sealed record EntityIdentityResolutionContext
{
    /// <summary>Optional host-verified kind, separate from the model's type label or attributes.</summary>
    public VerifiedIdentityKind? VerifiedKind { get; init; }
    /// <summary>The target messages from which the mention was extracted.</summary>
    public IReadOnlyList<string> SourceMessageIds { get; init; } = Array.Empty<string>();
    /// <summary>Source fragments eligible for bounded contextual adjudication.</summary>
    public IReadOnlyList<IdentitySourceFragment> Sources { get; init; } = Array.Empty<IdentitySourceFragment>();
    /// <summary>Host-verified keys; extractor attributes are never promoted into this collection.</summary>
    public IReadOnlyList<VerifiedIdentityKey> VerifiedKeys { get; init; } = Array.Empty<VerifiedIdentityKey>();
    /// <summary>Verified name namespace, when the host knows it.</summary>
    public string? NameNamespace { get; init; }
    /// <summary>Host provenance for NameNamespace; when present its namespace must agree.</summary>
    public VerifiedIdentityNameNamespace? NameNamespaceEvidence { get; init; }
}
