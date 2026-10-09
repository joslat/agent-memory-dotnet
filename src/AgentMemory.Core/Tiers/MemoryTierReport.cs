using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Core.Tiers;

/// <summary>
/// Storage's tier: the store-aware writer with or without the update judge, or the extractors; and what a failed writer
/// call falls back to (<see cref="ExtractionOptions.FallBackToExtractorsWhenWriterFails"/>).
/// </summary>
internal sealed class StorageTierSource(IServiceScopeFactory scopes, IOptions<ExtractionOptions> extraction) : IMemoryTierSource
{
    public MemoryTier Describe()
    {
        using var scope = scopes.CreateScope();
        var writer = scope.ServiceProvider.GetServices<IMemoryWriter>().Any(w => w.IsEnabled);
        var judge = scope.ServiceProvider.GetService<IMemoryUpdateJudge>() is { IsEnabled: true };
        var hostJudge = scope.ServiceProvider.GetService<IMemoryUpdateJudgeFallback>() is { IsEnabled: true };
        var fallback = extraction.Value.FallBackToExtractorsWhenWriterFails
            ? "a failed writer call falls back to the extractors"
            : "a failed writer call stores nothing";
        if (writer)
            return (judge, hostJudge) switch
            {
                (true, true) => new MemoryTier(MemoryTiers.Storage, "writer + update judge", $"the host's chat model judges if it fails; {fallback}"),
                (true, false) => new MemoryTier(MemoryTiers.Storage, "writer + update judge", fallback),
                (false, true) => new MemoryTier(MemoryTiers.Storage, "writer + the host's chat model as judge", fallback),
                _ => new MemoryTier(MemoryTiers.Storage, "writer", $"no update judge, so the writer's closings are not applied; {fallback}"),
            };
        return new MemoryTier(MemoryTiers.Storage, judge ? "extractors + update judge" : "extractors");
    }
}

/// <summary>Dreaming's tier: nothing runs between sessions in the library yet.</summary>
internal sealed class DreamingTierSource : IMemoryTierSource
{
    public MemoryTier Describe() => new(MemoryTiers.Dreaming, "off");
}

/// <summary>
/// Every leg's tier from the registered <see cref="IMemoryTierSource"/>s (the last one registered for a leg wins); a leg
/// nobody describes is said as what runs without it: retrieval without the gate is the similarity floor.
/// </summary>
internal sealed class MemoryTierReport(IEnumerable<IMemoryTierSource> sources) : IMemoryTiers
{
    private static readonly string[] Order = [MemoryTiers.Storage, MemoryTiers.Retrieval, MemoryTiers.Dreaming];
    private int _logged;

    public IReadOnlyList<MemoryTier> Describe()
    {
        var byLeg = new Dictionary<string, MemoryTier>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            MemoryTier tier;
            try
            {
                tier = source.Describe();
            }
            catch (Exception ex)
            {
                tier = new MemoryTier(source.GetType().Name, "unknown", ex.Message);
            }
            byLeg[tier.Leg] = tier;
        }
        byLeg.TryAdd(MemoryTiers.Retrieval, new MemoryTier(MemoryTiers.Retrieval, "similarity floor", "no gate added"));
        return [.. byLeg.Values.OrderBy(t => Array.IndexOf(Order, t.Leg) is var i && i >= 0 ? i : Order.Length)];
    }

    public string Line() => string.Join("; ", Describe());

    /// <summary>Logs <see cref="Line"/> once for this provider (the extraction stage calls it on its first write).</summary>
    internal void LogOnce(ILogger logger)
    {
        if (Interlocked.Exchange(ref _logged, 1) == 0)
            logger.LogInformation("Memory tiers: {Tiers}", Line());
    }
}
