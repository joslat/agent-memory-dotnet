using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Capabilities;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.Context;

/// <summary>Assembles a context envelope from the registered contributors.</summary>
[Experimental("AMEXT001")]
public interface IContextCompiler
{
    /// <summary>Compiles one envelope.</summary>
    Task<ContextEnvelope> CompileAsync(ContextRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Slice A compiler: applicability, then contribution, in priority order.
/// </summary>
/// <remarks>
/// <para>
/// <b>No routing, deliberately.</b> Every applicable contributor runs. The router — which decides
/// that a trivial turn needs none of them — is a later slice with an accuracy guard of its own,
/// because the leg-level router this repository already shipped selected correctly and still harmed
/// answers. Shipping selection before that guard exists would repeat it.
/// </para>
/// <para>
/// <b>A contributor's failure is an omission, not an exception.</b> One module's unreachable store
/// must not cost the host its core memory. Cancellation is the exception to that: a cancelled
/// request is the caller's decision and propagates.
/// </para>
/// <para>
/// Ordering is by <see cref="ContextContributorDescriptor.Priority"/> then by id, so an envelope is
/// reproducible from the same inputs — a section order that depended on registration or on which
/// task finished first would make the rendered prompt vary run to run.
/// </para>
/// </remarks>
[Experimental("AMEXT001")]
public sealed class ContextCompiler(
    IEnumerable<IContextContributor> contributors,
    IMemoryIsolationPolicy isolationPolicy,
    TimeProvider timeProvider,
    ILogger<ContextCompiler> logger) : IContextCompiler
{
    private readonly IReadOnlyList<IContextContributor> _contributors =
        (contributors ?? throw new ArgumentNullException(nameof(contributors)))
            .OrderBy(c => c.Descriptor.Priority)
            .ThenBy(c => c.Descriptor.Id, StringComparer.Ordinal)
            .ToArray();

    /// <inheritdoc/>
    public async Task<ContextEnvelope> CompileAsync(
        ContextRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // THE OWNER IS RESOLVED BEFORE ANY CONTRIBUTOR SEES THE REQUEST. Passing the host's claimed
        // owner to contributor code and only recording the resolved owner in the snapshot would still
        // let a module query on the untrusted claim, which is the boundary this policy exists to set.
        var resolved = isolationPolicy.ResolveReadScope(
            explicitScope: null,
            ownerId: request.Owner,
            operationName: nameof(CompileAsync),
            access: MemoryOperationAccess.Tenant);
        var resolvedRequest = request with { Owner = resolved.OwnerId };

        var sections = new List<ContextSection>();
        var omissions = new List<ContextOmission>();

        foreach (var contributor in _contributors)
        {
            var id = contributor.Descriptor.Id;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!await contributor.AppliesAsync(resolvedRequest, cancellationToken).ConfigureAwait(false))
                {
                    omissions.Add(new ContextOmission(id, ContextOmissionReason.NotApplicable));
                    continue;
                }

                var section = await contributor.ContributeAsync(resolvedRequest, cancellationToken)
                    .ConfigureAwait(false);

                // A contributor that applied and returned nothing is NOT the same as one that did not
                // apply, and the envelope says which by REASON rather than by a detail string: a
                // caller switching on the reason could not tell them apart when both said
                // NotApplicable, which made the public contract contradict its own documentation.
                if (section is null)
                {
                    omissions.Add(new ContextOmission(id, ContextOmissionReason.SearchedAndEmpty));
                    continue;
                }

                sections.Add(section);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Context contributor '{ContributorId}' failed; recorded as omitted.", id);
                omissions.Add(new ContextOmission(
                    id, ContextOmissionReason.Unavailable, exception.GetType().Name));
            }
        }

        // THE OWNER IS RESOLVED, NOT ECHOED, which is what the snapshot's own contract says it is.
        // The request carries what the HOST claims; the policy decides what that means under the
        // configured isolation mode, and the envelope records the decision. Copying the claim through
        // would have made a field documented as evidence of enforcement into a restatement of the
        // input -- true-looking, and unable to show that anything had been enforced.
        var snapshot = new ContextSnapshot(resolved.OwnerId, request.AsOf, request.SystemAsOf);
        return new ContextEnvelope(request, snapshot, sections, omissions, timeProvider.GetUtcNow());
    }
}
