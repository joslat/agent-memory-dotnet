using AgentMemory.Abstractions.Domain;

namespace Spike0.Host;

/// <summary>
/// The five Spike-0 fixtures, defined once and driven from both the wire and the direct path.
/// </summary>
/// <remarks>
/// <para>
/// The gate names two of them explicitly — an isolation case and an as-of case — because those are the
/// two where a naive wire is most likely to lose information silently. The other three cover the shapes
/// a recall ordinarily returns, so a wire that only works on the interesting cases is still caught.
/// </para>
/// <para>
/// Timestamps are <b>fixed constants</b>, never <c>UtcNow</c>. An as-of fixture anchored to wall-clock
/// time answers a different question on every run, and a parity script that passes for that reason has
/// measured nothing.
/// </para>
/// </remarks>
internal static class Fixtures
{
    /// <summary>The instant the fixture world is anchored to.</summary>
    internal static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal const string Alice = "alice";
    internal const string Bob = "bob";

    /// <summary>Deterministic ids, so a re-seed produces the same graph and a diff means a real change.</summary>
    private static string Id(string name) => $"spike0-{name}";

    private static Fact Fact(
        string name,
        string owner,
        string subject,
        string predicate,
        string @object,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        int createdDayOffset = 0) => new()
    {
        FactId = Id(name),
        Subject = subject,
        Predicate = predicate,
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = Epoch.AddDays(createdDayOffset),
        OwnerId = owner,
        ValidFrom = validFrom,
        ValidUntil = validUntil,
    };

    /// <summary>
    /// Alice's employer history: <c>Acme</c> until mid-year, then <c>Initech</c>.
    /// </summary>
    /// <remarks>
    /// The pair the as-of fixture turns on. Asking "as of March" must return Acme and asking "as of
    /// September" must return Initech — a wire that drops <c>validFrom</c>/<c>validUntil</c> makes both
    /// answers look identical and the fixture is what catches that.
    /// </remarks>
    internal static IReadOnlyList<Fact> EmployerHistory =>
    [
        Fact("employer-acme", Alice, "alice", "works_at", "Acme",
            validFrom: Epoch, validUntil: Epoch.AddMonths(6)),
        Fact("employer-initech", Alice, "alice", "works_at", "Initech",
            validFrom: Epoch.AddMonths(6), createdDayOffset: 1),
    ];

    /// <summary>Bob's fact, which must never appear in Alice's recall.</summary>
    internal static IReadOnlyList<Fact> OtherOwner =>
    [
        Fact("bob-employer", Bob, "bob", "works_at", "Globex", validFrom: Epoch),
    ];

    /// <summary>A superseded pair: the loser is closed, the winner stands.</summary>
    /// <remarks>
    /// Supersession is applied through the repository after both are stored, so the graph carries a real
    /// <c>SUPERSEDED_BY</c> edge rather than a hand-set <c>invalidated_at</c> — the wire has to survive
    /// the shape the engine actually produces, not a simplified stand-in.
    /// </remarks>
    internal static IReadOnlyList<Fact> SupersededPair =>
    [
        Fact("city-old", Alice, "alice", "lives_in", "Zurich", validFrom: Epoch),
        Fact("city-new", Alice, "alice", "lives_in", "Lisbon", validFrom: Epoch, createdDayOffset: 2),
    ];

    internal static string SupersessionLoserId => Id("city-old");
    internal static string SupersessionWinnerId => Id("city-new");

    /// <summary>Plain facts with no temporal or ownership subtlety — the ordinary case.</summary>
    internal static IReadOnlyList<Fact> Plain =>
    [
        Fact("diet", Alice, "alice", "prefers", "vegetarian meals", validFrom: Epoch),
        Fact("language", Alice, "alice", "speaks", "Portuguese", validFrom: Epoch),
    ];

    internal static IEnumerable<Fact> All =>
        EmployerHistory.Concat(OtherOwner).Concat(SupersededPair).Concat(Plain);
}
