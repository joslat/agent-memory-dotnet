namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// What a fact used to say, and when that stopped being true.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a whole <see cref="Fact"/>. A predecessor is read for one purpose — rendering
/// "current X (since D; previously Y)" — and returning full facts would carry embeddings and every
/// other property across a boundary that has no use for them, on a query that runs once per recall.
/// </para>
/// <para>
/// Both clocks are present because they answer different questions and can genuinely differ.
/// <see cref="InvalidatedAtUtc"/> is the transaction clock ("when we stopped believing it");
/// <see cref="ValidUntilUtc"/> is the valid-time clock ("when it stopped being true in the world").
/// Rendering picks the valid-time date where there is one, because a reader asking "since when?"
/// means the world, not the database.
/// </para>
/// </remarks>
/// <param name="Object">What the superseded fact asserted.</param>
/// <param name="InvalidatedAtUtc">Transaction-time close: when the system stopped believing it.</param>
/// <param name="ValidUntilUtc">Valid-time close: when it stopped holding in the world.</param>
public sealed record SupersededFact(
    string Object,
    DateTimeOffset? InvalidatedAtUtc,
    DateTimeOffset? ValidUntilUtc)
{
    /// <summary>
    /// The date to render, preferring valid time. Null when neither clock was stamped.
    /// </summary>
    public DateTimeOffset? EffectiveDate => ValidUntilUtc ?? InvalidatedAtUtc;
}
