namespace AgentMemory.Tests.Integration.Fixtures;

// DisableParallelization: this collection shares a single live Neo4j Testcontainer AND
// TckBridgeHttpRoundTripTests briefly mutates process-wide environment variables to point the hosted
// bridge at that container. xUnit runs collections in parallel by default, so serializing this collection
// against all others removes any chance of a concurrent collection observing those transient env vars (or
// contending on the shared container). Tests within the collection already run sequentially.
[CollectionDefinition("Neo4j Integration", DisableParallelization = true)]
public class Neo4jIntegrationCollection : ICollectionFixture<Neo4jIntegrationFixture>
{
}
