using System.Security.Cryptography;
using System.Text;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;

// NOT in AgentMemory.Neo4j.Queries, deliberately. That namespace is swept by
// CypherQueryExecutionSweepTests, which invokes every public static string-returning method in it and
// EXPLAINs the result against a live database -- so a helper that returns a SHA-256 hash rather than
// Cypher gets 18 hashes sent to the query planner. The namespace is the convention that says "this is
// Cypher", and this is a key computed in C#.
namespace AgentMemory.Neo4j.Derivation;

/// <summary>
/// The identity of a derived fact: one node per <c>(subject, predicate, operator, owner)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Computed in C#, never in Cypher.</b> The key folds in canonical subject and predicate keys, and
/// <c>MemoryTripleCanonicalizer</c> lowercases <i>and</i> collapses whitespace runs while Cypher's
/// <c>toLower</c> does neither — the two disagree outright on U+0130. A key computed in two places is
/// a key that will eventually be computed two ways, and the symptom would be a second aggregate node
/// silently appearing beside the first.
/// </para>
/// <para>
/// The <b>object</b> is deliberately absent from the key. An aggregate's value changes on every
/// recompute — that is what an aggregate does — so including it would spawn a fresh node per
/// observation and leave one dead aggregate behind each time.
/// </para>
/// </remarks>
internal static class DerivationKey
{
    /// <summary>The shared-owner marker, matching the repositories' <c>owner_key</c> convention.</summary>
    private const string SharedOwner = "__shared__";

    public static string For(Fact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var op = fact.Metadata.GetDerivationOperator()?.ToString()
            ?? throw new ArgumentException(
                "A derived fact must carry its operator in metadata; without it two different "
                + "aggregates over one group would share an identity.", nameof(fact));

        return For(fact.Subject, fact.Predicate, op, fact.OwnerId);
    }

    public static string For(string subject, string predicate, string derivationOperator, string? ownerId)
    {
        var material = string.Join(
            '|',
            MemoryTripleCanonicalizer.CanonicalValue(subject),
            MemoryTripleCanonicalizer.Canonical(predicate),
            derivationOperator,
            ownerId ?? SharedOwner);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }
}
