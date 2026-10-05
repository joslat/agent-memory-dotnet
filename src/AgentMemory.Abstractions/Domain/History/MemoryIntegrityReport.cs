namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// G6 (PLAN 40.50): what an integrity check of the store found, rule by rule. Read-only: the check changes nothing.
/// </summary>
public sealed record MemoryIntegrityReport
{
    /// <summary>When the check ran.</summary>
    public required DateTimeOffset CheckedAtUtc { get; init; }

    /// <summary>The owner checked; null checked the whole store.</summary>
    public string? OwnerId { get; init; }

    /// <summary>Each rule and what it found.</summary>
    public required IReadOnlyList<MemoryIntegrityRule> Rules { get; init; }

    /// <summary>True when no rule of severity <c>error</c> found anything (warnings do not fail a store).</summary>
    public bool Passed => Rules.All(rule => rule.Violations == 0 || rule.Severity != MemoryIntegrityRule.Error);
}

/// <summary>One integrity rule and what it found.</summary>
public sealed record MemoryIntegrityRule
{
    /// <summary>A store that breaks this rule is inconsistent.</summary>
    public const string Error = "error";

    /// <summary>A store that breaks this rule may be legitimate (a fact written through the API carries no source message).</summary>
    public const string Warning = "warning";

    /// <summary>A stable id, such as <c>owner.edge-endpoints</c>.</summary>
    public required string Id { get; init; }

    /// <summary>What the rule asks of the store.</summary>
    public required string Description { get; init; }

    /// <summary><see cref="Error"/> or <see cref="Warning"/>.</summary>
    public required string Severity { get; init; }

    /// <summary>How many items or edges break it.</summary>
    public required long Violations { get; init; }

    /// <summary>Up to five of them, by id.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
}
