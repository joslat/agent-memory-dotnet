namespace AgentMemory.Core.Extraction;

/// <summary>
/// The metadata keys that record where a memory's quoted words stand in its source message (#249): character offsets,
/// start inclusive and end exclusive, into the message named by the memory's first <c>SourceMessageIds</c> entry. They are
/// written only when the store-aware writer located its operation's quote, so every other memory's metadata is unchanged.
/// </summary>
internal static class SourceQuoteMetadata
{
    /// <summary>The start offset's key.</summary>
    internal const string StartKey = "source_quote_start";

    /// <summary>The end offset's key.</summary>
    internal const string EndKey = "source_quote_end";

    /// <summary><paramref name="metadata"/> with the quote's offsets added; the same instance when there are none.</summary>
    internal static IReadOnlyDictionary<string, object> With(IReadOnlyDictionary<string, object> metadata, int? start, int? end)
    {
        if (start is not { } from || end is not { } to || to <= from) return metadata;
        return new Dictionary<string, object>(metadata) { [StartKey] = from, [EndKey] = to };
    }
}
