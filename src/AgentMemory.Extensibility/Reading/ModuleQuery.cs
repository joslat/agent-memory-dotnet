using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Reading;

/// <summary>A bounded, typed read of one module-owned record type.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleQuery
{
    /// <summary>Maximum records a single query can request; views may apply a smaller host budget.</summary>
    public const int MaximumLimit = 1000;
    /// <summary>The owning module, which must be the reader's bound caller or a direct dependency.</summary>
    public required string ModuleId { get; init; }
    /// <summary>Declared module type; never a caller-supplied Cypher fragment.</summary>
    public required string Type { get; init; }
    /// <summary>Optional exact logical key.</summary>
    public string? Key { get; init; }
    /// <summary>Conjunctive predicates using the same closed language as manifest views.</summary>
    public IReadOnlyList<ModuleViewFilter> Where { get; init; } = [];
    /// <summary>Conjunctive exact targets of declared outgoing relations, checked before the result limit.</summary>
    public IReadOnlyList<ModuleLinkFilter> Links { get; init; } = [];
    /// <summary>Optional declared-property sort; ties are resolved by record identity.</summary>
    public ModuleViewOrder? OrderBy { get; init; }
    /// <summary>Positive record limit, at most <see cref="MaximumLimit"/>.</summary>
    public required int Limit { get; init; }
}

/// <summary>Host-supplied owner, session and time context; no administrative isolation bypass.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleReadContext
{
    /// <summary>Requested owner/shared visibility, resolved by the host isolation policy before database work.</summary>
    public MemoryScope? Scope { get; init; }
    /// <summary>Session substitution for filters using $session; optional when no such filter is used.</summary>
    public string? SessionId { get; init; }
    /// <summary>Transaction-time pin. Null requests current live versions.</summary>
    public DateTimeOffset? SystemAsOfUtc { get; init; }
    /// <summary>Valid-time pin. Module records do not support it and return NotApplicable.</summary>
    public DateTimeOffset? ValidAsOfUtc { get; init; }
}

/// <summary>Explicit distinction between a refused read, a successful empty read and an unsupported clock.</summary>
[Experimental("AMEXT001")]
public enum ModuleReadStatus
{
    /// <summary>The query or scope was refused; no records are returned.</summary>
    Refused,
    /// <summary>The read completed, possibly with no matching records.</summary>
    Succeeded,
    /// <summary>The requested clock is unsupported; current data is not substituted.</summary>
    NotApplicable
}
