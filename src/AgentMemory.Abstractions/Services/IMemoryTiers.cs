using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// One leg of memory (storage, retrieval, dreaming) and the tier it runs at now: what the configuration and the judges'
/// health let it do, with why when it runs below what was asked.
/// </summary>
/// <param name="Leg"><see cref="MemoryTiers.Storage"/>, <see cref="MemoryTiers.Retrieval"/> or <see cref="MemoryTiers.Dreaming"/>.</param>
/// <param name="Tier">What runs, e.g. <c>writer + update judge</c>, <c>judge (jev)</c>, <c>similarity floor</c>, <c>off</c>.</param>
/// <param name="Note">Why it runs at this tier, or what it falls back to; null when there is nothing to add.</param>
[Experimental("AMREC001")]
public sealed record MemoryTier(string Leg, string Tier, string? Note = null)
{
    /// <summary><c>storage: writer + update judge (a failed writer call falls back to the extractors)</c>.</summary>
    public override string ToString() => Note is null ? $"{Leg}: {Tier}" : $"{Leg}: {Tier} ({Note})";
}

/// <summary>A leg that can say which tier it runs at; registered by the package that provides the leg.</summary>
[Experimental("AMREC001")]
public interface IMemoryTierSource
{
    /// <summary>The leg's tier as it stands now.</summary>
    MemoryTier Describe();
}

/// <summary>
/// Every leg's tier, in one place: what a host shows at startup or in a health check. The library logs <see cref="Line"/>
/// once, on its first write.
/// </summary>
/// <example><c>storage: writer + update judge; retrieval: judge (jev); dreaming: off</c></example>
[Experimental("AMREC001")]
public interface IMemoryTiers
{
    /// <summary>Storage, retrieval and dreaming, in that order.</summary>
    IReadOnlyList<MemoryTier> Describe();

    /// <summary>The three on one line.</summary>
    string Line();
}

/// <summary>The legs' names.</summary>
[Experimental("AMREC001")]
public static class MemoryTiers
{
    /// <summary>How long-term memory is written.</summary>
    public const string Storage = "storage";

    /// <summary>How recall fills the prompt.</summary>
    public const string Retrieval = "retrieval";

    /// <summary>What runs over a store between sessions.</summary>
    public const string Dreaming = "dreaming";
}
