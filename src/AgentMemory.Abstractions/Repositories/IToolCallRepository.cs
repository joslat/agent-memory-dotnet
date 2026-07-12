using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for tool call persistence.
/// <para><b>R1 scoping note:</b> tool calls are grandchildren of a <c>ReasoningTrace</c> (via a step) and
/// carry no owner of their own. Reads are keyed by a random parent handle (a step id or call id) reachable
/// only through an owner-scoped trace search, so they are <i>by-handle</i> reads — the same intentional
/// exemption applied to every <c>GetByIdAsync</c>. Owner isolation lives at the trace tier.</para>
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

    /// <summary>
    /// Aggregates tool-usage statistics grouped by tool name, over tool calls reachable through reasoning
    /// traces within the given owner scope (R1). Tool calls carry no owner of their own, so the scope is
    /// applied at the owning <c>ReasoningTrace</c> tier — the aggregate never spans owners. When
    /// <paramref name="toolName"/> is set, returns at most the single matching tool's stats; otherwise one
    /// entry per tool, ordered by name. Deliberately does not read the global aggregate <c>:Tool</c> node
    /// (which accumulates across all owners). <paramref name="scope"/> null ⇒ unscoped (all owners).
    /// </summary>
    Task<IReadOnlyList<ToolCallStats>> GetStatsAsync(
        string? toolName = null,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default);
}
