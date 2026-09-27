using System.Globalization;
using System.Text;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Provides helpers for rendering a sequence of conversation messages into a single
/// plain-text transcript suitable for extraction and processing.
/// </summary>
internal static class ConversationTextBuilder
{
    /// <summary>
    /// Builds a newline-separated transcript from the supplied messages, formatting each
    /// message as <c>Role: Content</c>.
    /// </summary>
    /// <param name="messages">The ordered collection of conversation messages to render.</param>
    /// <returns>A single string containing one line per message in the form <c>Role: Content</c>.</returns>
    public static string Build(IReadOnlyList<Message> messages)
        => string.Join("\n", messages.Select(m => $"{m.Role}: {m.Content}"));

    /// <inheritdoc cref="Build(IReadOnlyList{Message})"/>
    /// <param name="messages">The ordered collection of conversation messages to render.</param>
    /// <param name="stamped">Prefix each turn with its time (<see cref="Stamp"/>).</param>
    public static string Build(IReadOnlyList<Message> messages, bool stamped)
        => stamped ? string.Join("\n", messages.Select(m => $"{Stamp(m)}{m.Role}: {m.Content}")) : Build(messages);

    /// <summary>
    /// 36.1. The time a turn was said, as its transcript prefix: <c>[2026-09-27T22:05:01.8447034+00:00] </c>. The one
    /// rendering every extractor uses, so "last week" and "in April" resolve against the turn that said them.
    /// </summary>
    /// <remarks>
    /// The temporal instruction tells the model each turn carries its time. Only the multi-session extractor
    /// rendered it; the single-session ones (every agent turn) sent none, and the model guessed the year: "a half
    /// marathon in April", said in September 2026, was stored as April 2025 (found in simulated conversations).
    /// </remarks>
    public static string Stamp(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return "[" + message.TimestampUtc.ToString("O", CultureInfo.InvariantCulture) + "] ";
    }

    /// <summary>
    /// Builds the transcript with each turn numbered from 1, as <c>[N] Role: Content</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The numbering is what makes a per-item provenance answer expressible: without it the model has
    /// no way to name a turn, and <c>EXTRACTED_FROM</c> can only be written for the whole batch.
    /// </para>
    /// <para>
    /// <b>1-based, and positional.</b> Turn <c>N</c> is <c>messages[N-1]</c>, which is the same order
    /// the caller derives its source-message ids in, so resolution is a direct index rather than a
    /// lookup that could silently mismatch. Kept as a separate method rather than a flag on
    /// <see cref="Build(IReadOnlyList{Message})"/> so the unnumbered rendering — the one every recorded
    /// measurement used — cannot change by accident.
    /// </para>
    /// </remarks>
    public static string BuildNumbered(IReadOnlyList<Message> messages) => BuildNumbered(messages, stamped: false);

    /// <inheritdoc cref="BuildNumbered(IReadOnlyList{Message})"/>
    /// <param name="messages">The ordered collection of conversation messages to render.</param>
    /// <param name="stamped">Prefix each turn with its time, after its number (<see cref="Stamp"/>).</param>
    public static string BuildNumbered(IReadOnlyList<Message> messages, bool stamped)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < messages.Count; index++)
        {
            if (index > 0) builder.Append('\n');
            builder.Append('[')
                .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append("] ")
                .Append(stamped ? Stamp(messages[index]) : string.Empty)
                .Append(messages[index].Role)
                .Append(": ")
                .Append(messages[index].Content);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Renders a window as a fenced, unnumbered context block followed by the turns to extract from
    /// (E2). Falls back to the plain rendering when the window carries no context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The context block is deliberately not numbered.</b> Per-item provenance (L3c) numbers turns
    /// from 1 and resolves turn <c>N</c> positionally to <c>Targets[N-1]</c>. Numbering the context
    /// into the same sequence would shift every target index by the context length, and the failure
    /// would not look like a crash — each fact would simply be attributed to a turn a few places
    /// earlier, which afterwards is indistinguishable from correct attribution.
    /// </para>
    /// <para>
    /// The fence is explicit text rather than a formatting convention because the model has to act on
    /// it: everything inside is to be read and nothing inside is to be extracted.
    /// </para>
    /// </remarks>
    public static string BuildWindow(ExtractionWindow window, bool numbered) => BuildWindow(window, numbered, stamped: false);

    /// <inheritdoc cref="BuildWindow(ExtractionWindow, bool)"/>
    /// <param name="window">The turns to extract from, and the read-only context before them.</param>
    /// <param name="numbered">Number the target turns.</param>
    /// <param name="stamped">Prefix every turn, context included, with its time (<see cref="Stamp"/>).</param>
    public static string BuildWindow(ExtractionWindow window, bool numbered, bool stamped)
    {
        var targets = numbered ? BuildNumbered(window.Targets, stamped) : Build(window.Targets, stamped);
        if (!window.HasContext) return targets;

        var builder = new StringBuilder();
        builder.Append(ContextOpen).Append('\n')
               .Append(Build(window.Context, stamped)).Append('\n')
               .Append(ContextClose).Append("\n\n")
               .Append(targets);
        return builder.ToString();
    }

    /// <summary>Opening fence of the read-only context block.</summary>
    internal const string ContextOpen =
        "--- EARLIER CONVERSATION (for reference only — do NOT extract anything from these turns) ---";

    /// <summary>Closing fence of the read-only context block.</summary>
    internal const string ContextClose =
        "--- END OF EARLIER CONVERSATION. Extract ONLY from the turns below. ---";
}
