using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Context;

namespace AgentMemory.Extensibility.Capabilities;

/// <summary>
/// Something that can add a typed section to a context envelope.
/// </summary>
/// <remarks>
/// <para>
/// The unit a module registers, and the unit the router will later schedule. A contributor answers
/// two questions: cheaply, whether it applies to this request; and then, what it contributes.
/// </para>
/// <para>
/// <b>A contributor that fails does not fail the compile.</b> It becomes an omission with a reason.
/// One module's unreachable store must not cost the host its core memory, which is the same rule the
/// core assembler already applies to its own sections.
/// </para>
/// </remarks>
[Experimental("AMEXT001")]
public interface IContextContributor
{
    /// <summary>What this contributor is and when it applies.</summary>
    ContextContributorDescriptor Descriptor { get; }

    /// <summary>
    /// Whether this contributor applies to the request, cheaply.
    /// </summary>
    /// <remarks>
    /// Must not query a store or call a model: the router calls this for every candidate on every
    /// turn, so anything expensive here is paid on turns where the contributor then does not run.
    /// </remarks>
    ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken);

    /// <summary>Produces this contributor's section.</summary>
    Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken);
}

/// <summary>What a contributor is, and when it applies.</summary>
/// <param name="Id">Stable contributor id, e.g. <c>core.memory</c>.</param>
/// <param name="SectionTypes">Section type ids it can produce.</param>
/// <param name="Priority">
/// Render order, ascending. The core contributor sits at 0 so module sections append after it —
/// which is what keeps an empty module set byte-identical to the provider being composed.
/// </param>
/// <param name="Mandatory">
/// Whether omitting it invalidates the envelope. A mandatory contributor's omission is surfaced to
/// the reader rather than absorbed silently.
/// </param>
[Experimental("AMEXT001")]
public sealed record ContextContributorDescriptor(
    string Id,
    IReadOnlySet<string> SectionTypes,
    int Priority = 100,
    bool Mandatory = false);
