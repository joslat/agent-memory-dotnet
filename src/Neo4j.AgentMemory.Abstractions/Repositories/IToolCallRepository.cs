using Neo4j.AgentMemory.Abstractions.Domain;

namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for tool call persistence.
/// </summary>
public interface IToolCallRepository
{
    /// <summary>Adds a tool call.</summary>
    Task<ToolCall> AddAsync(ToolCall toolCall, CancellationToken cancellationToken = default);

    /// <summary>Updates a tool call.</summary>
    Task<ToolCall> UpdateAsync(ToolCall toolCall, CancellationToken cancellationToken = default);

    /// <summary>Gets tool calls for a step.</summary>
    Task<IReadOnlyList<ToolCall>> GetByStepAsync(string stepId, CancellationToken cancellationToken = default);

    /// <summary>Gets a tool call by identifier.</summary>
    Task<ToolCall?> GetByIdAsync(string toolCallId, CancellationToken cancellationToken = default);

    /// <summary>Creates a TRIGGERED_BY relationship from a tool call to the message that triggered it.</summary>
    Task CreateTriggeredByRelationshipAsync(string toolCallId, string messageId, CancellationToken cancellationToken = default);
}
