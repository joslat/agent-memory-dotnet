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
    /// <param name="request">The context request.</param>
    /// <param name="excludedContributorIds">
    /// Contributors that must not run at all for this compilation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <b>Excluded is not the same as omitted.</b> An excluded contributor was never in scope, so it
    /// produces no section AND no omission -- an omission would tell a reader that something was
    /// tried and did not arrive. The case this exists for is a caller that has ALREADY obtained a
    /// contributor's content by another route: the Agent Framework provider inherits core memory from
    /// the shipped provider, and without this the core contributor would run a second full assembly
    /// -- a live query and an embedding call -- whose section the caller then discards.
    /// </remarks>
    Task<ContextEnvelope> CompileAsync(
        ContextRequest request,
        IReadOnlySet<string>? excludedContributorIds = null,
        CancellationToken cancellationToken = default);
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
public sealed partial class ContextCompiler : IContextCompiler
{
    private readonly IReadOnlyList<IContextContributor> _contributors;
    private readonly IMemoryIsolationPolicy _isolationPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContextCompiler> _logger;
    private Modules.ModuleResolution? Resolution { get; }

    /// <summary>Compiles legacy contributors without inferring module identity or emitting module spans.</summary>
    public ContextCompiler(IEnumerable<IContextContributor> contributors, IMemoryIsolationPolicy isolationPolicy,
        TimeProvider timeProvider, ILogger<ContextCompiler> logger)
    {
        _contributors = (contributors ?? throw new ArgumentNullException(nameof(contributors)))
            .OrderBy(c => c.Descriptor.Priority).ThenBy(c => c.Descriptor.Id, StringComparer.Ordinal).ToArray();
        _isolationPolicy = isolationPolicy;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Compiles explicitly host-bound module contributors with frozen metadata identities.</summary>
    /// <remarks>Metadata resolution is not publisher trust or a runtime-set lease. Core uses the existing
    /// CoreMemoryContextContributor; other contributors require distinct ids owned by the supplied modules.</remarks>
    public ContextCompiler(Modules.ModuleResolution resolution, IEnumerable<IContextContributor> contributors,
        IMemoryIsolationPolicy isolationPolicy, TimeProvider timeProvider, ILogger<ContextCompiler> logger)
        : this(Bind(resolution, contributors), isolationPolicy, timeProvider, logger)
    {
        Resolution = resolution;
    }

    /// <inheritdoc/>
    public async Task<ContextEnvelope> CompileAsync(
        ContextRequest request,
        IReadOnlySet<string>? excludedContributorIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var compile = StartCompileActivity(excludedContributorIds);
        try
        {
            // THE OWNER IS RESOLVED BEFORE ANY CONTRIBUTOR SEES THE REQUEST. Passing the host's claimed
            // owner to contributor code and only recording the resolved owner in the snapshot would still
            // let a module query on the untrusted claim, which is the boundary this policy exists to set.
            var resolved = _isolationPolicy.ResolveReadScope(
                explicitScope: null,
                ownerId: request.Owner,
                operationName: nameof(CompileAsync),
                access: MemoryOperationAccess.Tenant);
            var resolvedRequest = request with { Owner = resolved.OwnerId, ResolvedScope = resolved };

            var sections = new List<ContextSection>();
            var omissions = new List<ContextOmission>();

            foreach (var contributor in _contributors)
            {
                var id = contributor.Descriptor.Id;

                // SKIPPED BEFORE APPLIESASYNC, because the cost this avoids is the contributor's own
                // work. Recording nothing is deliberate: see the interface remarks.
                if (excludedContributorIds is not null && excludedContributorIds.Contains(id)) continue;

                cancellationToken.ThrowIfCancellationRequested();
                using var contribution = StartContributionActivity(contributor);

                try
                {
                    if (!await contributor.AppliesAsync(resolvedRequest, cancellationToken).ConfigureAwait(false))
                    {
                        omissions.Add(new ContextOmission(id, ContextOmissionReason.NotApplicable));
                        ObserveOmission(compile, contribution, contributor, ContextOmissionReason.NotApplicable);
                        continue;
                    }

                    var result = await contributor.ContributeResultAsync(resolvedRequest, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((result.Section is null) == (result.OmissionReason is null)
                        || result.OmissionReason is { } invalid && !Enum.IsDefined(invalid))
                        throw new InvalidOperationException("A contribution requires exactly one section or a supported omission.");
                    if (result.OmissionReason is { } reason)
                    {
                        omissions.Add(new ContextOmission(id, reason, result.Detail));
                        ObserveOmission(compile, contribution, contributor, reason);
                        continue;
                    }
                    var section = BindSection(contributor, result.Section!);
                    sections.Add(section);
                    if (contribution is { IsAllDataRequested: true })
                        ObserveOutcome(contribution, "Section", section.Items.Count);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ObserveOutcome(contribution, "Cancelled");
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception, "Context contributor '{ContributorId}' failed; recorded as omitted.", id);
                    omissions.Add(new ContextOmission(
                        id, ContextOmissionReason.Unavailable, exception.GetType().Name));
                    ObserveOmission(compile, contribution, contributor, ContextOmissionReason.Unavailable);
                }
            }

            // THE OWNER IS RESOLVED, NOT ECHOED, which is what the snapshot's own contract says it is.
            // The request carries what the HOST claims; the policy decides what that means under the
            // configured isolation mode, and the envelope records the decision. Copying the claim through
            // would have made a field documented as evidence of enforcement into a restatement of the
            // input -- true-looking, and unable to show that anything had been enforced.
            var snapshot = new ContextSnapshot(resolved.OwnerId, request.AsOf, request.SystemAsOf);
            var envelope = new ContextEnvelope(request, snapshot, sections, omissions, _timeProvider.GetUtcNow());
            if (compile is { IsAllDataRequested: true })
                ObserveOutcome(compile, omissions.Any(o => o.Reason == ContextOmissionReason.Unavailable) ? "Partial" : "Completed",
                    sections.Sum(section => section.Items.Count));
            return envelope;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveOutcome(compile, "Cancelled");
            throw;
        }
        catch
        {
            ObserveOutcome(compile, "Unavailable");
            throw;
        }
    }
}
