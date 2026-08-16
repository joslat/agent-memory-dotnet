using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// One projection feature: decides whether it is on, then contributes annotations or blocks.
/// </summary>
/// <remarks>
/// <para>
/// Registered in DI <b>unconditionally</b> and enumerable, each owning its own <see cref="IsEnabled"/>
/// gate — the <c>IMemoryReranker</c> precedent. Gating the registration on the flag instead would mean
/// a host that turns a flag on through <c>IOptions</c> reconfiguration still gets nothing, silently:
/// both rerankers shipped that way, registered by nobody, while their options sat in the public
/// surface documenting behaviour no consumer could obtain.
/// </para>
/// <para>
/// Execution order is registration order, which is fixed and therefore deterministic. Features are
/// additive over one shared state and must not depend on each other's output.
/// </para>
/// </remarks>
internal interface IProjectionFeature
{
    /// <summary>Whether these options turn this feature on.</summary>
    bool IsEnabled(MemoryProjectionOptions options);

    /// <summary>Contributes this feature's annotations and blocks to the shared state.</summary>
    Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken);
}
