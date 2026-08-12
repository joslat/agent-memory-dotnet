using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Extractor for facts from text.
/// </summary>
public interface IFactExtractor
{
    /// <summary>
    /// Extracts facts from messages.
    /// </summary>
    Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts from <see cref="ExtractionWindow.Targets"/>, reading
    /// <see cref="ExtractionWindow.Context"/> only to understand them (E2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The default implementation ignores the context entirely</b> and calls the existing overload
    /// with the targets — byte-identical to pre-E2 behaviour. An extractor that cannot use context is
    /// therefore correct by doing nothing, rather than being broken by a widened window.
    /// </para>
    /// <para>
    /// A default interface method rather than a new required member: the extractor interfaces are
    /// public and locked under SemVer, and a third-party extractor must keep compiling.
    /// </para>
    /// <para>
    /// Implementers must not extract from the context turns. Doing so re-asserts facts already stored,
    /// which now inflates confidence (S2) and <c>mention_count</c> (R7) purely because a fact sat
    /// inside a sliding window — turning corroboration into recency.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ExtractedFact>> ExtractWithContextAsync(
        ExtractionWindow window,
        CancellationToken cancellationToken = default)
        => ExtractAsync(window.Targets, cancellationToken);
}
