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

    /// <summary>
    /// <paramref name="metadata"/> with the quote's offsets added. Without offsets of its own, any an earlier message left
    /// are dropped (RVW-REST-WRITER, the review's writer-5): the write replaces the record's source messages, so they would
    /// point into the wrong one. The same instance when there is nothing to add or drop.
    /// </summary>
    internal static IReadOnlyDictionary<string, object> With(IReadOnlyDictionary<string, object> metadata, int? start, int? end)
    {
        if (start is { } from && end is { } to && to > from)
            return new Dictionary<string, object>(metadata) { [StartKey] = from, [EndKey] = to };
        if (!metadata.ContainsKey(StartKey) && !metadata.ContainsKey(EndKey)) return metadata;
        var without = new Dictionary<string, object>(metadata);
        without.Remove(StartKey);
        without.Remove(EndKey);
        return without;
    }
}
