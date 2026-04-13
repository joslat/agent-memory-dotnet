using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.McpServer.Tools;

/// <summary>
/// Graph query tool: execute arbitrary Cypher queries.
/// </summary>
[McpServerToolType]
public sealed class GraphQueryTools
{
    [McpServerTool(Name = "graph_query"), Description("Execute a Cypher query against the Neo4j knowledge graph. Only available when explicitly enabled in server configuration.")]
    public static async Task<string> GraphQuery(
        IGraphQueryService graphQueryService,
        IOptions<McpServerOptions> options,
        [Description("The Cypher query to execute")] string cypherQuery,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.EnableGraphQuery)
        {
            throw new McpException("The graph_query tool is disabled. Enable it in McpServerOptions.EnableGraphQuery.");
        }

        var results = await graphQueryService.QueryAsync(cypherQuery, cancellationToken: cancellationToken);
        return ToolJsonContext.Serialize(new
        {
            rowCount = results.Count,
            rows = results
        });
    }
}
