namespace AgentMemory.Nams.Recall;

/// <summary>A sanitized, non-fatal degradation observed during a recall -- e.g. entity search failed but
/// context retrieval succeeded. Never contains raw exception details or secrets.</summary>
public sealed record NamsRecallWarning(string Category, string Message);
