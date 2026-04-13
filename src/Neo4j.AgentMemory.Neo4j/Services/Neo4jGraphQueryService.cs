using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Services;

public sealed class Neo4jGraphQueryService : IGraphQueryService
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jGraphQueryService> _logger;

    public Neo4jGraphQueryService(INeo4jTransactionRunner tx, ILogger<Neo4jGraphQueryService> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string cypherQuery,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing graph query: {Query}", cypherQuery);

        return await _tx.ReadAsync<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(async runner =>
        {
            var driverParams = parameters is not null
                ? parameters.ToDictionary(kv => kv.Key, kv => kv.Value)
                : new Dictionary<string, object?>();

            var cursor = await runner.RunAsync(cypherQuery, driverParams);
            var records = await cursor.ToListAsync(cancellationToken);

            return records.Select(r =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var key in r.Keys)
                {
                    dict[key] = ConvertValue(r[key]);
                }
                return (IReadOnlyDictionary<string, object?>)dict;
            }).ToList();
        }, cancellationToken);
    }

    private static object? ConvertValue(object? value)
    {
        return value switch
        {
            null => null,
            INode node => new Dictionary<string, object?>
            {
                ["id"] = node.ElementId,
                ["labels"] = node.Labels.ToList(),
                ["properties"] = node.Properties.ToDictionary(kv => kv.Key, kv => kv.Value)
            },
            IRelationship rel => new Dictionary<string, object?>
            {
                ["id"] = rel.ElementId,
                ["type"] = rel.Type,
                ["startNodeId"] = rel.StartNodeElementId,
                ["endNodeId"] = rel.EndNodeElementId,
                ["properties"] = rel.Properties.ToDictionary(kv => kv.Key, kv => kv.Value)
            },
            IPath path => new Dictionary<string, object?>
            {
                ["nodes"] = path.Nodes.Select(n => ConvertValue(n)).ToList(),
                ["relationships"] = path.Relationships.Select(r => ConvertValue(r)).ToList()
            },
            _ => value
        };
    }
}
