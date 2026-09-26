namespace AgentMemory.Extraction.Llm.Internal;

/// <summary>
/// The provider's content filter stopped the extraction reply (<c>finish_reason = content_filter</c>).
/// A <see cref="FormatException"/> like every other unusable reply, so existing handling still applies,
/// but distinguishable: asking again gets the same refusal, and the batch splitter treats it as content
/// to skip rather than a batch shape to split.
/// </summary>
internal sealed class ContentFilteredException(string message) : FormatException(message);
