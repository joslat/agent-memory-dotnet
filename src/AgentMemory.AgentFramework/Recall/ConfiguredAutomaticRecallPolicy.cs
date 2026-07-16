namespace AgentMemory.AgentFramework.Recall;

/// <summary>
/// The default <see cref="IAutomaticRecallPolicy"/> (#88): always recalls, using every category the host
/// has configured a nonzero limit for -- reproducing the deterministic, task-agnostic behavior that
/// predates #88 exactly. Registered by <c>AddAgentMemoryFramework</c>; a host replaces it by registering
/// its own <see cref="IAutomaticRecallPolicy"/> (e.g. <see cref="HeuristicAutomaticRecallPolicy"/> or a
/// custom implementation) after calling <c>AddAgentMemoryFramework</c>.
/// </summary>
public sealed class ConfiguredAutomaticRecallPolicy : IAutomaticRecallPolicy
{
    /// <inheritdoc/>
    public ValueTask<AutomaticRecallDecision> DecideAsync(
        AutomaticRecallContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AutomaticRecallDecision.Recall);
}
