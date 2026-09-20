using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;

namespace AgentMemory.Extensibility.Contributors;

/// <summary>
/// The existing memory, as ONE opaque section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opaque on purpose, and this is the whole safety argument for Slice A.</b> The section carries
/// the assembled <see cref="MemoryContext"/> as metadata and no text of its own, so the renderer can
/// hand the core block to the provider that already renders it. Nothing re-renders core memory, and
/// with no modules registered the output is byte-identical to that provider by construction rather
/// than by comparison.
/// </para>
/// <para>
/// Typed core sections — <c>core.relevant_facts</c> and friends, rendered by the compiler — are a
/// later increment, admitted only when they reproduce the delegated output byte for byte. The reason
/// to wait is not access: the formatter is internal because the 1.0 lockdown internalised it
/// deliberately, and a second rendering path is a second place for the #92 admission gate to drift,
/// which the facade's own comment records having happened before.
/// </para>
/// <para>
/// <b>Priority 0</b>, so core sorts before every module section and appending is what modules do.
/// </para>
/// </remarks>
[Experimental("AMEXT001")]
public sealed class CoreMemoryContextContributor(IMemoryContextAssembler assembler) : IContextContributor
{
    /// <summary>The section type id the core block carries.</summary>
    public const string SectionType = "core.rendered";

    /// <summary>The contributor id.</summary>
    public const string ContributorId = "core.memory";

    /// <inheritdoc/>
    public ContextContributorDescriptor Descriptor { get; } = new(
        ContributorId,
        new HashSet<string>(StringComparer.Ordinal) { SectionType },
        Priority: 0,
        Mandatory: true);

    /// <inheritdoc/>
    /// <remarks>Always. Core memory is not a routing decision in Slice A.</remarks>
    public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    /// <inheritdoc/>
    public async Task<ContextSection?> ContributeAsync(
        ContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var recall = new RecallRequest
        {
            SessionId = request.SessionId,
            UserId = request.Owner,
            Query = request.Query ?? string.Empty,
        };

        // THE TWO CLOCKS TRAVEL TOGETHER OR NOT AT ALL. A point-in-time read needs both, and passing
        // one while defaulting the other answers a question nobody asked -- what was true then, as
        // believed now. The live path stays byte-identical to what it always was.
        var context = request.AsOf is { } asOf
            ? await assembler
                .AssembleContextAsOfAsync(recall, asOf, request.SystemAsOf ?? asOf, cancellationToken)
                .ConfigureAwait(false)
            : await assembler.AssembleContextAsync(recall, cancellationToken).ConfigureAwait(false);

        return new ContextSection(
            SectionType,
            ContributorId,
            SchemaVersion: 1,
            Items: [],
            Metadata: context);
    }
}
