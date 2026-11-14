using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jFactRepository
{
    public async Task<IReadOnlyList<PersonIdentity>> GetPersonIdentitiesAsync(int limit, MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Person identity reads are bounded to 1000.");
        if (limit == 0) return Array.Empty<PersonIdentity>();
        if (!_identityEventDetailsEnabled)
            throw new NotSupportedException("Enable and migrate identity-evidence before requesting person details.");
        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = limit, ["hasOwner"] = scope?.OwnerId is not null, ["ownerId"] = scope?.OwnerId, ["includeShared"] = scope?.IncludeShared ?? false,
        };
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PersonDetailQueries.Identities, parameters).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<PersonIdentity>)rows.Select(row => new PersonIdentity
            {
                EntityId = row["id"].As<string>(), Surfaces = row["surfaces"].As<List<string>>(), Confirmed = row["confirmed"].As<bool>(),
                Complete = row["complete"].As<bool>(),
            }).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetPersonNamesAsync(int limit, MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit), "Person name reads are bounded to 10,000.");
        if (limit == 0) return Array.Empty<string>();
        if (!_identityEventDetailsEnabled)
            throw new NotSupportedException("Enable and migrate identity-evidence before requesting person details.");
        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = limit, ["hasOwner"] = scope?.OwnerId is not null, ["ownerId"] = scope?.OwnerId, ["includeShared"] = scope?.IncludeShared ?? false,
        };
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PersonDetailQueries.Names, parameters).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<string>)rows.Select(row => row["spelling"].As<string>()).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Fact>> GetPersonDetailsAsync(IReadOnlyList<string> personEntityIds,
        IReadOnlyCollection<string> excludeFactIds, int limit, MemoryScope? scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(personEntityIds); ArgumentNullException.ThrowIfNull(excludeFactIds);
        if (personEntityIds.Count > 3 || excludeFactIds.Count > 512 || limit is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(limit), "Person detail input exceeds its bounds.");
        foreach (var id in personEntityIds.Concat(excludeFactIds))
            if (string.IsNullOrWhiteSpace(id) || id.Length > 4096) throw new ArgumentException("Invalid identifier.");
        if (personEntityIds.Count == 0 || limit == 0) return Array.Empty<Fact>();
        if (!_identityEventDetailsEnabled)
            throw new NotSupportedException("Enable and migrate identity-evidence before requesting person details.");
        var parameters = new Dictionary<string, object?>
        {
            ["personIds"] = personEntityIds.Distinct(StringComparer.Ordinal).ToArray(),
            ["excludeIds"] = excludeFactIds.Distinct(StringComparer.Ordinal).ToArray(),
            ["limit"] = limit, ["hasOwner"] = scope?.OwnerId is not null, ["ownerId"] = scope?.OwnerId, ["includeShared"] = scope?.IncludeShared ?? false,
            ["now"] = _clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(PersonDetailQueries.Read, parameters).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<Fact>)rows.Select(row => MapToFact(row["f"].As<Dictionary<string, object>>(), null)).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }
}
