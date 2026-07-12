using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

internal interface INeo4jSessionFactory
{
    /// <summary>
    /// Opens a session against the store database resolved from the ambient
    /// <see cref="AgentMemory.Abstractions.Services.IMemoryStoreContext"/> and
    /// <see cref="MemoryStoreOptions"/> (R1b). With the default SharedDatabase strategy this is the
    /// configured <see cref="Neo4jOptions.Database"/>, reproducing the original single-store behavior.
    /// </summary>
    IAsyncSession OpenSession(AccessMode accessMode = AccessMode.Write);

    /// <summary>
    /// Opens a session against an explicit database, bypassing store resolution. Used for
    /// administrative work (the <c>system</c> database) and per-store provisioning/bootstrap.
    /// </summary>
    IAsyncSession OpenSession(string database, AccessMode accessMode = AccessMode.Write);
}
