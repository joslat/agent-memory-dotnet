using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

internal static class ExtractionInputValidation
{
    internal static void Validate(ExtractionRequest request, bool multiSession = false)
    {
        if (request.InputMode == ExtractionInputMode.Unspecified) return;
        if (request.InputMode is not (ExtractionInputMode.ConversationTurn or ExtractionInputMode.Import))
            throw new ArgumentOutOfRangeException(nameof(request), request.InputMode, "Unknown extraction input mode.");
        if (request.DeferIfOnlyAsking)
            throw new ArgumentException("Explicit input modes cannot defer and combine turns. Use Unspecified for legacy turn deferral.", nameof(request));
        if (request.InputMode != ExtractionInputMode.ConversationTurn) return;
        if (multiSession)
            throw new ArgumentException("ConversationTurn cannot use multi-session extraction. Replay requests individually in chronological order.", nameof(request));
        if (request.Messages.Count(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new ArgumentException("ConversationTurn requires exactly one target user message. Put earlier turns in ContextMessages or use Import.", nameof(request));
    }
}
