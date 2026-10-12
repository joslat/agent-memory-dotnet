namespace AgentMemory.McpHost.Coding;

internal sealed class CodingTurnConflictException() : InvalidOperationException("Turn ID already belongs to a different payload.");
