using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// What changed in memory between two checkpoints — the inverse of full recall.
/// </summary>
/// <remarks>
/// <para>
/// An agent resuming work re-receives everything it already processed. Full recall re-assembles the
/// same facts every session start, and there was no way to ask "what is different since I last
/// looked". The ingredients all existed and were enforced on the live path — <c>created_at</c> stamped
/// on create only, <c>invalidated_at</c> stamped idempotently, <c>SUPERSEDED_BY</c> edges,
/// <c>valid_from</c>/<c>valid_until</c> — and nothing read them as a diff.
/// </para>
/// <para>
/// <b>The window is half-open: <c>(Since, TakenAtUtc]</c>.</b> <see cref="TakenAtUtc"/> is read once
/// from the clock and handed back as the next checkpoint, so consecutive deltas partition time
/// exactly and every change appears <b>exactly once, by construction</b>. That property is also what
/// makes the feature measurable without a judge.
/// </para>
/// <para>
/// A delta <b>complements</b> recall on a resume turn; it never replaces it. The current question
/// still needs relevance-ranked context.
/// </para>
/// </remarks>
public sealed record MemoryDelta
{
    /// <summary>The exclusive lower bound — the caller's previous checkpoint.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>The inclusive upper bound, and the token to pass as <c>Since</c> next time.</summary>
    public required DateTimeOffset TakenAtUtc { get; init; }

    /// <summary>Facts the system newly knows: <c>created_at</c> in the window.</summary>
    /// <remarks>
    /// Transaction clock, never valid time — a fact learned yesterday about 2019 must still surface —
    /// and never <c>updated_at</c>, which every restatement bumps, so restatements would replay as
    /// "new" forever.
    /// </remarks>
    public IReadOnlyList<Fact> NewFacts { get; init; } = Array.Empty<Fact>();

    /// <summary>Facts replaced in the window, paired old → new.</summary>
    public IReadOnlyList<SupersededFactPair> SupersededPairs { get; init; } =
        Array.Empty<SupersededFactPair>();

    /// <summary>Facts closed in the window with <b>no</b> successor.</summary>
    public IReadOnlyList<Fact> InvalidatedFacts { get; init; } = Array.Empty<Fact>();

    /// <summary>Facts whose real-world validity window closed in the window, still live on the transaction clock.</summary>
    public IReadOnlyList<Fact> ExpiredValidity { get; init; } = Array.Empty<Fact>();

    /// <summary>Facts that became true during the window, having been known before it.</summary>
    public IReadOnlyList<Fact> NewlyDueProspective { get; init; } = Array.Empty<Fact>();

    /// <summary>Preferences the system newly knows.</summary>
    public IReadOnlyList<Preference> NewPreferences { get; init; } = Array.Empty<Preference>();

    /// <summary>Preferences replaced in the window, paired old → new.</summary>
    public IReadOnlyList<SupersededPreferencePair> SupersededPreferences { get; init; } =
        Array.Empty<SupersededPreferencePair>();

    /// <summary>Entities the system newly knows.</summary>
    public IReadOnlyList<Entity> NewEntities { get; init; } = Array.Empty<Entity>();

    /// <summary>
    /// Buckets that hit their per-section cap, so truncation is visible rather than silent.
    /// </summary>
    /// <remarks>
    /// A large import makes the next delta huge. Capping is necessary; capping <i>quietly</i> would
    /// let a caller believe they had seen everything that changed.
    /// </remarks>
    public IReadOnlyList<string> TruncatedSections { get; init; } = Array.Empty<string>();

    /// <summary>True when nothing changed in the window.</summary>
    public bool IsEmpty =>
        NewFacts.Count == 0 &&
        SupersededPairs.Count == 0 &&
        InvalidatedFacts.Count == 0 &&
        ExpiredValidity.Count == 0 &&
        NewlyDueProspective.Count == 0 &&
        NewPreferences.Count == 0 &&
        SupersededPreferences.Count == 0 &&
        NewEntities.Count == 0;
}

/// <summary>A fact and the fact that replaced it.</summary>
public sealed record SupersededFactPair(Fact Old, Fact New);

/// <summary>A preference and the preference that replaced it.</summary>
public sealed record SupersededPreferencePair(Preference Old, Preference New);

/// <summary>Asks what changed since a checkpoint.</summary>
public sealed record MemoryDeltaRequest
{
    /// <summary>The caller's previous checkpoint, exclusive.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>
    /// The owner this delta is being read for, resolved through the isolation policy exactly as
    /// <see cref="RecallRequest.UserId"/> is.
    /// </summary>
    /// <remarks>
    /// <b>Not optional in practice.</b> A delta reads facts, preferences and entities straight out of
    /// the repositories; without an owner the isolation policy resolves to global and one tenant is told
    /// what changed in another's memory. Under
    /// <see cref="Abstractions.Options.MemoryIsolationMode.StrictMultiTenant"/> omitting it throws
    /// rather than resolving, which is the point.
    /// </remarks>
    public string? UserId { get; init; }

    /// <summary>
    /// An explicit scope, which wins over <see cref="UserId"/> when set — the same precedence
    /// <c>RecallOptions.Scope</c> has.
    /// </summary>
    public MemoryScope? Scope { get; init; }

    /// <summary>Per-bucket cap. Exceeding it is reported in <see cref="MemoryDelta.TruncatedSections"/>.</summary>
    public int MaxItemsPerSection { get; init; } = 20;
}

/// <summary>The five fact buckets, as one repository result.</summary>
public sealed record FactDeltaRows
{
    /// <summary>Facts created in the window.</summary>
    public IReadOnlyList<Fact> NewFacts { get; init; } = Array.Empty<Fact>();

    /// <summary>Facts replaced in the window, old → new.</summary>
    public IReadOnlyList<SupersededFactPair> SupersededPairs { get; init; } = Array.Empty<SupersededFactPair>();

    /// <summary>Facts closed in the window with no successor.</summary>
    public IReadOnlyList<Fact> InvalidatedFacts { get; init; } = Array.Empty<Fact>();

    /// <summary>Facts whose validity window closed in the window.</summary>
    public IReadOnlyList<Fact> ExpiredValidity { get; init; } = Array.Empty<Fact>();

    /// <summary>Facts that became due in the window, known before it.</summary>
    public IReadOnlyList<Fact> NewlyDueProspective { get; init; } = Array.Empty<Fact>();
}

/// <summary>The two preference buckets. <c>Preference</c> carries no valid-time window.</summary>
public sealed record PreferenceDeltaRows
{
    /// <summary>Preferences created in the window.</summary>
    public IReadOnlyList<Preference> NewPreferences { get; init; } = Array.Empty<Preference>();

    /// <summary>Preferences replaced in the window, old → new.</summary>
    public IReadOnlyList<SupersededPreferencePair> SupersededPreferences { get; init; } =
        Array.Empty<SupersededPreferencePair>();
}
