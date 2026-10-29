using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// The contributor slot of a module the trial gate switched off (review 2). The host registered one slot per contributor of
/// every module it added, before the gate decided; this keeps the slot, contributes nothing, and is dropped by the context
/// compiler, so the slots after it stay on their own modules' contributors.
/// </summary>
[Experimental("AMEXT001")]
internal sealed class SwitchedOffContributor(string moduleId, int slot, string reason) : IContextContributor
{
    /// <summary>The module that is off.</summary>
    internal string ModuleId { get; } = moduleId;

    public ContextContributorDescriptor Descriptor { get; } = new($"{moduleId}.switched-off-{slot}", new HashSet<string>(StringComparer.Ordinal));

    public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) => Task.FromResult<ContextSection?>(null);

    public Task<ContextContributionResult> ContributeResultAsync(ContextRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ContextContributionResult { OmissionReason = ContextOmissionReason.NotApplicable, Detail = $"{ModuleId} is off: {reason}" });
}
