using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>G6 (PLAN 40.50): the integrity rules, one read query each.</summary>
internal sealed class Neo4jMemoryIntegrityService(INeo4jTransactionRunner tx, IClock clock) : IMemoryIntegrityService
{
    private static readonly (string Id, string Description, string Severity, string Query)[] Rules =
    [
        ("owner.edge-endpoints", "No edge joins two owners' memories", MemoryIntegrityRule.Error, IntegrityQueries.EdgeEndpoints),
        ("owner.edge-owner", "A relationship edge belongs to the owner of its owned endpoints", MemoryIntegrityRule.Error, IntegrityQueries.EdgeOwner),
        ("supersession.closed-has-successor", "A fact closed as a change or a correction has its successor", MemoryIntegrityRule.Error, IntegrityQueries.ClosedWithoutSuccessor),
        ("supersession.live-has-no-successor", "A live fact has no successor", MemoryIntegrityRule.Error, IntegrityQueries.LiveWithSuccessor),
        ("validity.window-ordered", "A validity window ends after it begins", MemoryIntegrityRule.Error, IntegrityQueries.InvertedWindow),
        ("provenance.fact-has-source", "A fact has a source message (a fact written through the API may not)", MemoryIntegrityRule.Warning, IntegrityQueries.FactWithoutSource),
    ];

    public async Task<MemoryIntegrityReport> CheckAsync(string? ownerId = null, CancellationToken cancellationToken = default)
    {
        var owner = string.IsNullOrWhiteSpace(ownerId) ? null : ownerId;
        var parameters = new Dictionary<string, object?> { ["ownerId"] = owner };
        var found = await tx.ReadAsync(async runner =>
        {
            var results = new List<MemoryIntegrityRule>(Rules.Length);
            foreach (var (id, description, severity, query) in Rules)
            {
                var cursor = await runner.RunAsync(query, parameters).ConfigureAwait(false);
                var record = await cursor.SingleAsync().ConfigureAwait(false);
                results.Add(new MemoryIntegrityRule
                {
                    Id = id,
                    Description = description,
                    Severity = severity,
                    Violations = record["violations"].As<long>(),
                    Examples = record["examples"].As<List<object>>().Select(e => e?.ToString() ?? "").ToList(),
                });
            }
            return results;
        }, cancellationToken).ConfigureAwait(false);
        return new MemoryIntegrityReport { CheckedAtUtc = clock.UtcNow, OwnerId = owner, Rules = found };
    }
}
