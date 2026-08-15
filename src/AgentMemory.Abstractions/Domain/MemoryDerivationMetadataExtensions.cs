using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Reads and writes derivation provenance on the <c>Metadata</c> dictionary every long-term memory
/// record already carries.
/// </summary>
/// <remarks>
/// <para>
/// The same mechanism <see cref="MemoryTrustMetadataExtensions"/> uses, and for the same reason: a
/// derived fact is an ordinary <see cref="Fact"/> that happens to have been computed, so it needs no
/// change to the public domain records — which is also what lets it ride the existing vector index,
/// budget, owner scoping and invalidation gate with no recall-path changes at all.
/// </para>
/// <para>
/// <c>Metadata</c> round-trips through Neo4j as one serialized JSON string, so a value read back after
/// persistence arrives as a <see cref="JsonElement"/> rather than its original CLR type. Both shapes
/// are handled; a value in neither shape reads as absent rather than throwing, because provenance that
/// cannot be parsed is provenance the renderer must simply omit — not a reason to fail a recall.
/// </para>
/// </remarks>
public static class MemoryDerivationMetadataExtensions
{
    private const string KindKey = "kind";
    private const string DerivationKey = "derivation";
    private const string OperatorKey = "operator";
    private const string InputFactIdsKey = "input_fact_ids";

    /// <summary>The <c>kind</c> value marking a fact as computed rather than observed.</summary>
    public const string DerivedKind = "derived";

    /// <summary>True when this record was computed by the session accountant.</summary>
    public static bool IsDerived(this IReadOnlyDictionary<string, object> metadata) =>
        string.Equals(ReadString(metadata, KindKey), DerivedKind, StringComparison.Ordinal);

    /// <summary>
    /// The human-readable arithmetic, e.g. <c>800 (fact a1) − 50 (fact b2)</c>, or null when absent.
    /// </summary>
    /// <remarks>
    /// Rendered inline beside the value so the model can <i>check</i> the arithmetic rather than trust
    /// it. A derived number presented bare is a claim; presented with its inputs it is an argument.
    /// </remarks>
    public static string? GetDerivation(this IReadOnlyDictionary<string, object> metadata) =>
        ReadString(metadata, DerivationKey);

    /// <summary>Which operator produced this value, or null when absent or unrecognised.</summary>
    public static DerivationOperators? GetDerivationOperator(
        this IReadOnlyDictionary<string, object> metadata) =>
        Enum.TryParse<DerivationOperators>(ReadString(metadata, OperatorKey), ignoreCase: true, out var parsed)
            ? parsed
            : null;

    /// <summary>The facts this value was computed from. Empty when absent.</summary>
    public static IReadOnlyList<string> GetInputFactIds(
        this IReadOnlyDictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue(InputFactIdsKey, out var value)) return [];

        return value switch
        {
            IReadOnlyList<string> list => list,
            IEnumerable<string> sequence => [.. sequence],
            // Post-persistence shape: the whole metadata dictionary came back as parsed JSON.
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                [.. element.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)],
            // A single id serialised as a bare string rather than a one-element array. Tolerated on
            // read because provenance that exists in a slightly odd shape is still provenance.
            string single => [single],
            JsonElement { ValueKind: JsonValueKind.String } element => [element.GetString()!],
            _ => [],
        };
    }

    /// <summary>
    /// Returns a new metadata dictionary carrying this derivation, preserving every other entry.
    /// </summary>
    public static IReadOnlyDictionary<string, object> WithDerivation(
        this IReadOnlyDictionary<string, object> metadata,
        DerivationOperators derivationOperator,
        string derivation,
        IReadOnlyList<string> inputFactIds)
    {
        ArgumentNullException.ThrowIfNull(derivation);
        ArgumentNullException.ThrowIfNull(inputFactIds);

        return new Dictionary<string, object>(metadata)
        {
            [KindKey] = DerivedKind,
            [OperatorKey] = derivationOperator.ToString(),
            [DerivationKey] = derivation,
            // Materialised, not deferred: the caller's list may be a lazy sequence over state that has
            // moved on by the time this dictionary is serialised.
            [InputFactIdsKey] = inputFactIds.ToArray(),
        };
    }

    /// <summary>Builds a fresh metadata dictionary containing only this derivation.</summary>
    public static IReadOnlyDictionary<string, object> CreateWithDerivation(
        DerivationOperators derivationOperator,
        string derivation,
        IReadOnlyList<string> inputFactIds) =>
        new Dictionary<string, object>().WithDerivation(derivationOperator, derivation, inputFactIds);

    /// <summary>
    /// Strips caller-supplied derivation keys from externally-supplied metadata.
    /// </summary>
    /// <remarks>
    /// The same reserved-key discipline <c>WithoutCallerSuppliedTrustLevel</c> enforces, for the same
    /// reason. A caller who could stamp <c>kind=derived</c> and an invented derivation string on a
    /// hand-written fact would be handing the model arithmetic that no accountant ever performed —
    /// with the inline provenance that makes it look checked.
    /// </remarks>
    public static IReadOnlyDictionary<string, object> WithoutCallerSuppliedDerivation(
        this IReadOnlyDictionary<string, object> metadata)
    {
        if (!metadata.ContainsKey(KindKey) && !metadata.ContainsKey(DerivationKey)
            && !metadata.ContainsKey(OperatorKey) && !metadata.ContainsKey(InputFactIdsKey))
        {
            return metadata;
        }

        var sanitized = new Dictionary<string, object>(metadata);
        sanitized.Remove(KindKey);
        sanitized.Remove(DerivationKey);
        sanitized.Remove(OperatorKey);
        sanitized.Remove(InputFactIdsKey);
        return sanitized;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object> metadata, string key) =>
        metadata.TryGetValue(key, out var value)
            ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            }
            : null;

    /// <summary>Formats a decimal the way every derivation string does, culture-invariantly.</summary>
    /// <remarks>
    /// Shared so a derivation string reads the same in every locale. A derivation rendered with a comma
    /// decimal separator beside a value rendered with a point is a provenance line that appears to
    /// disagree with its own result.
    /// </remarks>
    public static string FormatDerivedNumber(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);
}
