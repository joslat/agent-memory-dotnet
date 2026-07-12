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
}
