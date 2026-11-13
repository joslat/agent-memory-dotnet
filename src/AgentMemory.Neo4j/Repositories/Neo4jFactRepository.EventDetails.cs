using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jFactRepository
{
    public async Task<IReadOnlyList<Fact>> GetEventDetailsAsync(IReadOnlyList<string> seedFactIds,
        IReadOnlyCollection<string> excludeFactIds, int limit, MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(seedFactIds); ArgumentNullException.ThrowIfNull(excludeFactIds);
        if (seedFactIds.Count > 10 || excludeFactIds.Count > 512 || limit is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(limit), "Event detail input exceeds its bounds.");
        foreach (var id in seedFactIds.Concat(excludeFactIds))
            if (string.IsNullOrWhiteSpace(id) || id.Length > 4096) throw new ArgumentException("Invalid fact identifier.");
        if (seedFactIds.Count == 0 || limit == 0) return Array.Empty<Fact>();
        if (!_identityEventDetailsEnabled)
            throw new NotSupportedException("Enable and migrate identity-evidence before requesting event details.");
        var parameters = new Dictionary<string, object?>
        {
            ["seedIds"] = seedFactIds.Distinct(StringComparer.Ordinal).ToArray(),
            ["excludeIds"] = excludeFactIds.Distinct(StringComparer.Ordinal).ToArray(),
            ["limit"] = limit, ["hasOwner"] = scope?.OwnerId is not null, ["ownerId"] = scope?.OwnerId, ["includeShared"] = scope?.IncludeShared ?? false,
            ["now"] = _clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(EventDetailQueries.Read, parameters).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<Fact>)rows.Select(row => MapToFact(row["f"].As<Dictionary<string, object>>(), null)).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }
}
