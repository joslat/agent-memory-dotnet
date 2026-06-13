using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace AgentMemory.Analytics;

/// <summary>
/// Projects a transient, owner-filtered GDS in-memory graph, runs an algorithm over it, and always drops it
/// (even on failure). The graph name is unique per call so concurrent analytics never collide.
/// </summary>
internal static class GdsGraphScope
{
    public static async Task<T> WithProjectionAsync<T>(
        INeo4jTransactionRunner tx,
        string graphName,
        MemoryScope? scope,
        Func<IAsyncQueryRunner, Task<T>> algorithm,
        CancellationToken cancellationToken)
    {
        bool hasOwner = scope?.HasOwnerFilter == true;
        bool includeShared = scope?.IncludeShared ?? true;
        var (nodeQuery, relQuery) = GdsQueries.Projection(hasOwner, includeShared);

        await tx.WriteAsync(async runner =>
        {
            // Defensive drop first: ExecuteWriteAsync auto-retries this lambda on a transient error, and the
            // graph name is fixed per call — without this, a retry after the catalog write would fail with
            // "graph already exists" and leak the partial graph. Drop is a no-op when the graph is absent.
            await (await runner.RunAsync(GdsQueries.DropGraph, new { graphName })).ConsumeAsync();

            var parameters = new Dictionary<string, object?>
            {
                ["graphName"] = graphName,
                ["nodeQuery"] = nodeQuery,
                ["relQuery"] = relQuery,
            };
            if (hasOwner) parameters["ownerId"] = scope!.OwnerId;
            await runner.RunAsync(GdsQueries.ProjectCypher(hasOwner), parameters);
        }, cancellationToken);

        try
        {
            // Run the stream under a WRITE session too: under a routing (cluster/Aura) URI this pins it to
            // the same member (the leader) that holds the just-created, member-local GDS catalog graph.
            return await tx.WriteAsync(algorithm, cancellationToken);
        }
        finally
        {
            // Best-effort cleanup: never let a drop failure mask the algorithm result.
            try
            {
                await tx.WriteAsync(async runner =>
                {
                    await runner.RunAsync(GdsQueries.DropGraph, new { graphName });
                }, CancellationToken.None);
            }
            catch
            {
                // ignored — the projection is named uniquely and GDS expires idle graphs.
            }
        }
    }
}
