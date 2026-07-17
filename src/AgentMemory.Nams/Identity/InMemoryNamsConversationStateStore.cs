using System.Collections.Concurrent;

namespace AgentMemory.Nams.Identity;

/// <summary>
/// Single-process, in-memory <see cref="INamsConversationStateStore"/>. Suitable for a single-instance host
/// or tests. A horizontally-scaled host MUST supply its own durable implementation instead -- this store
/// provides no cross-process guarantee, matching the engineering plan's explicit warning that an in-process
/// store cannot prevent duplicate NAMS conversation creation across multiple processes.
/// </summary>
public sealed class InMemoryNamsConversationStateStore : INamsConversationStateStore
{
    private readonly ConcurrentDictionary<string, NamsConversationIdentity> _mappings = new();

    public Task<NamsConversationIdentity?> TryGetAsync(
        string sessionId, string localConversationId,
        CancellationToken cancellationToken)
    {
        var key = BuildKey(sessionId, localConversationId);
        return Task.FromResult(_mappings.TryGetValue(key, out var identity) ? identity : null);
    }

    public Task<bool> TryCreateAsync(NamsConversationIdentity identity, CancellationToken cancellationToken)
    {
        var key = BuildKey(identity.SessionId, identity.LocalConversationId);
        return Task.FromResult(_mappings.TryAdd(key, identity)); // ConcurrentDictionary.TryAdd is atomic
    }

    private static string BuildKey(string sessionId, string localConversationId) =>
        string.Join('\0', sessionId, localConversationId);
}
