using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace AgentMemory.TckBridge;

/// <summary>
/// Full-graph wipe helper shared by <c>/clear_all_data</c> and <c>/teardown</c>. There is no
/// high-level service for an unscoped wipe (short-term/long-term services are session- or
/// owner-scoped by design), so this runs the same raw Cypher the integration test fixture uses
/// (<see cref="AgentMemory.Neo4j.Infrastructure.ISchemaBootstrapper"/> sibling —
/// mirrors <c>Neo4jIntegrationFixture.CleanDatabaseAsync</c>).
/// </summary>
internal sealed class BridgeAdmin
{
    private readonly INeo4jSessionFactory _sessionFactory;

    public BridgeAdmin(INeo4jSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory;
    }

    /// <summary>Deletes every node and relationship in the configured store database.</summary>
    public async Task WipeAllDataAsync(CancellationToken ct = default)
    {
        // Uses the resolved store-tier session (honors Neo4jOptions.Database) rather than a bare
        // driver session, so the wipe targets the same database the rest of the bridge writes to.
        await using var session = _sessionFactory.OpenSession(AccessMode.Write);
        await session.RunAsync("MATCH (n) DETACH DELETE n").WaitAsync(ct).ConfigureAwait(false);
    }
}
