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
    public static string BuildNumbered(IReadOnlyList<Message> messages)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < messages.Count; index++)
        {
            if (index > 0) builder.Append('\n');
            builder.Append('[')
                .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append("] ")
                .Append(messages[index].Role)
                .Append(": ")
                .Append(messages[index].Content);
        }
        return builder.ToString();
    }
}
