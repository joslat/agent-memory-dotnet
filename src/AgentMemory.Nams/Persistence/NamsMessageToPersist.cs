namespace AgentMemory.Nams.Persistence;

/// <summary>
/// A single message's content to persist. Deliberately has no <c>Role</c> field -- the wire role is assigned
/// by <see cref="INamsPersistenceService.PersistTurnAsync"/> from which parameter list a message appears in
/// (<c>userMessages</c> vs <c>assistantMessages</c>), so a caller cannot accidentally submit a system/tool
/// role through this API at all (engineering plan §7 Phase 5: "system prompts are not persisted by default").
/// </summary>
public sealed record NamsMessageToPersist(string Content);
