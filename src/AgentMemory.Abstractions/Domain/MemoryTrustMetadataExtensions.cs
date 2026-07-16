using System.Text.Json;

namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Reads/writes <see cref="MemoryTrustLevel"/> from the <c>Metadata</c> dictionary every long-term memory
/// record already carries (#92 Phase 3), rather than adding a new first-class schema property to
/// <see cref="Entity"/>/<see cref="Fact"/>/<see cref="Preference"/>/<see cref="ReasoningTrace"/> --
/// consistent with this repo's own convention of landing a speculative field in <c>Metadata</c> until its
/// shape proves stable. <c>Metadata</c> round-trips through Neo4j as a single serialized JSON string
/// property (see <c>Neo4jRecordMapper.SerializeMetadata</c>/<c>DeserializeMetadata</c>), so a value read
/// back after persistence arrives as a <see cref="JsonElement"/>, not the original CLR type -- both shapes
/// are handled here.
/// </summary>
public static class MemoryTrustMetadataExtensions
{
    private const string TrustLevelKey = "trust_level";

    /// <summary>
    /// Reads the trust level stamped in <paramref name="metadata"/>, or <see cref="MemoryTrustLevel.Untrusted"/>
    /// when absent or unparseable -- the safe default for memory nothing has explicitly vouched for.
    /// </summary>
    public static MemoryTrustLevel GetTrustLevel(this IReadOnlyDictionary<string, object> metadata) =>
        metadata.TryGetValue(TrustLevelKey, out var value) && TryParse(value, out var level)
            ? level
            : MemoryTrustLevel.Untrusted;

    /// <summary>
    /// Returns a new metadata dictionary with <paramref name="trustLevel"/> stamped in, preserving every
    /// other existing entry. <c>Metadata</c> is read-only on the domain records, so this never mutates
    /// <paramref name="metadata"/> in place.
    /// </summary>
    public static IReadOnlyDictionary<string, object> WithTrustLevel(
        this IReadOnlyDictionary<string, object> metadata, MemoryTrustLevel trustLevel)
    {
        var updated = new Dictionary<string, object>(metadata) { [TrustLevelKey] = trustLevel.ToString() };
        return updated;
    }

    /// <summary>
    /// Builds a fresh metadata dictionary containing only <paramref name="trustLevel"/> -- for callers
    /// (e.g. <c>ExtractedFact</c>/<c>ExtractedPreference</c>) with no pre-existing metadata to preserve, so
    /// they don't need to allocate an empty dictionary just to immediately call <see cref="WithTrustLevel"/>
    /// on it.
    /// </summary>
    public static IReadOnlyDictionary<string, object> CreateWithTrustLevel(MemoryTrustLevel trustLevel) =>
        new Dictionary<string, object> { [TrustLevelKey] = trustLevel.ToString() };

    /// <summary>
    /// Strips any caller-supplied <c>trust_level</c> entry from externally-supplied metadata (#92 Phase 3).
    /// <c>trust_level</c> is a framework-reserved key: it must only ever be set by the framework's own
    /// controlled stamping (<c>PersistenceStage</c> during extraction, or an explicit, deliberate host
    /// action), never echoed back verbatim from a lower-level write API that accepts free-form caller
    /// metadata (an MCP tool's <c>metadataJson</c> parameter, a direct <c>IReasoningMemoryService</c> call).
    /// Without this, any caller of such an API could self-assign <see cref="MemoryTrustLevel.ApplicationTrusted"/>
    /// and bypass the admission policy's instruction-like-content detection entirely. Apply this to any
    /// metadata accepted from an external caller before combining it with a framework-assigned trust level
    /// (or before persisting it as-is, if no trust level is assigned at all for that write path).
    /// </summary>
    public static IReadOnlyDictionary<string, object> WithoutCallerSuppliedTrustLevel(
        this IReadOnlyDictionary<string, object> metadata)
    {
        if (!metadata.ContainsKey(TrustLevelKey))
            return metadata;

        var sanitized = new Dictionary<string, object>(metadata);
        sanitized.Remove(TrustLevelKey);
        return sanitized;
    }

    private static bool TryParse(object value, out MemoryTrustLevel level)
    {
        var text = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null
        };

        if (text is not null)
            return Enum.TryParse(text, ignoreCase: true, out level);

        level = MemoryTrustLevel.Untrusted;
        return false;
    }
}
