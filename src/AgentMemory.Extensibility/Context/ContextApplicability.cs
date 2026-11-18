// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>Optional, explicit pre-search policy. The compiler calls it only after resolving owner isolation.</summary>
[Experimental("AMEXT001")]
public interface IContextApplicabilityPolicy
{
    /// <summary>When true, scoring starts beside exhaustive contribution; returned exclusions are never applied. False retains pre-search selection.</summary>
    bool ObserveOnly => false;
    /// <summary>Selects within the supplied eligible contributor identities; failures preserve exhaustive compilation.</summary>
    Task<ContextApplicabilityResult> SelectAsync(ContextRequest request, IReadOnlyList<string> contributorIds, CancellationToken cancellationToken = default);
}

/// <summary>A host decision distinct from ordinary contributor omissions.</summary>
[Experimental("AMEXT001")]
public sealed record ContextApplicabilityResult
{
    /// <summary>Eligible identities that should not issue reads in this compilation.</summary>
    public IReadOnlySet<string> ExcludedContributorIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Non-sensitive diagnostic status, for example observed, applied or fallback.</summary>
    public required string Outcome { get; init; }
}
