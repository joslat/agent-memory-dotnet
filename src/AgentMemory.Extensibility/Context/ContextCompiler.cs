using System.Diagnostics.CodeAnalysis;
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

        var sections = new List<ContextSection>();
        var omissions = new List<ContextOmission>();

        foreach (var contributor in _contributors)
        {
            var id = contributor.Descriptor.Id;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!await contributor.AppliesAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    omissions.Add(new ContextOmission(id, ContextOmissionReason.NotApplicable));
                    continue;
                }

                var section = await contributor.ContributeAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                // A contributor that applied and returned nothing is NOT the same as one that did not
                // apply, and the envelope says which: "searched and found nothing" against "was never
                // asked". Collapsing them is how a reader stops being able to tell whether an answer
                // was complete.
                if (section is null)
                {
                    omissions.Add(new ContextOmission(
                        id, ContextOmissionReason.NotApplicable, "applied, produced no section"));
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

        var snapshot = new ContextSnapshot(request.Owner, request.AsOf, request.SystemAsOf);
        return new ContextEnvelope(request, snapshot, sections, omissions, timeProvider.GetUtcNow());
    }
}
