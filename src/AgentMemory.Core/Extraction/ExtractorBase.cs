using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Provides a base implementation for memory extractors that derive items of type
/// <typeparamref name="T"/> from a sequence of conversation messages, with built-in
/// error handling that yields an empty result on failure.
/// </summary>
/// <typeparam name="T">The type of item produced by the extractor.</typeparam>
public abstract class ExtractorBase<T>
{
    /// <summary>
    /// Gets the <see cref="ILogger"/> used to record diagnostic and warning messages.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtractorBase{T}"/> class.
    /// </summary>
    /// <param name="logger">The logger used to record diagnostic and warning messages.</param>
    protected ExtractorBase(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// When implemented in a derived class, performs the core extraction logic over the
    /// supplied messages.
    /// </summary>
    /// <param name="messages">The messages to extract items from.</param>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    /// <returns>A task that resolves to the extracted items.</returns>
    protected abstract Task<IReadOnlyList<T>> ExtractCoreAsync(
        IReadOnlyList<Message> messages, CancellationToken cancellationToken);

    /// <summary>
    /// The window-aware core (E2). Reads <see cref="ExtractionWindow.Context"/> to resolve references
    /// and extracts only from <see cref="ExtractionWindow.Targets"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to the context-free core, so an extractor that has not opted in behaves byte-identically
    /// to its pre-E2 self rather than silently widening what it extracts.
    /// </remarks>
    protected virtual Task<IReadOnlyList<T>> ExtractCoreWithContextAsync(
        ExtractionWindow window, CancellationToken cancellationToken)
        => ExtractCoreAsync(window.Targets, cancellationToken);

    /// <summary>
    /// Extracts items from the supplied messages, returning an empty list when no messages
    /// are provided or when the underlying extraction fails.
    /// </summary>
    /// <param name="messages">The messages to extract items from.</param>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    /// <returns>
    /// A task that resolves to the extracted items, or an empty list if there are no
    /// messages or an error occurs during extraction.
    /// </returns>
    public Task<IReadOnlyList<T>> ExtractAsync(
        IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
        => ExtractWithContextAsync(ExtractionWindow.ForTargets(messages), cancellationToken);

    /// <summary>
    /// Extracts from the window's targets, reading its context only to understand them (E2), with the
    /// same failure containment as the message overload.
    /// </summary>
    public async Task<IReadOnlyList<T>> ExtractWithContextAsync(
        ExtractionWindow window, CancellationToken cancellationToken = default)
    {
        // Emptiness is judged on the TARGETS. A window carrying context but nothing to extract from
        // has nothing to do -- calling out with only background would spend a completion on turns the
        // extractor has been told not to extract from.
        if (window.Targets.Count == 0) return Array.Empty<T>();
        try
        {
            return await ExtractCoreWithContextAsync(window, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Honor caller cancellation — do not mask it as a successful empty result.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{ExtractorType} extraction failed; returning empty list.", GetType().Name);
            return Array.Empty<T>();
        }
    }
}
