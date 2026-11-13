namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>Injective storage keys, preserving ordinary owner IDs and the existing shared bucket.</summary>
internal static class OwnerStorageKey
{
    internal const string Shared = "*";

    // A private leading '*' gains one escape character; no private value can become the shared key.
    internal static string Encode(string? ownerId) => ownerId is null ? Shared : ownerId.StartsWith('*') ? "*" + ownerId : ownerId;
}
