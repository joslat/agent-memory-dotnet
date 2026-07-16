using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Security;

/// <summary>
/// Gates a recalled conversation-history message's ORIGINALLY-PERSISTED role (#92 Phase 7) -- the one
/// disclosed gap left open since Phase 1: recalled <c>RecentMessages</c>/<c>RelevantMessages</c> keep
/// whatever role they were persisted with, with no delimiting, admission, or trust check at all, unlike
/// entities/facts/preferences/GraphRAG.
/// </summary>
/// <remarks>
/// Deliberately narrow: only "system" and "tool" are treated as privileged -- the two roles most
/// <c>IChatClient</c>s/tool-calling conventions give elevated or special handling. Ordinary "user"/
/// "assistant" messages (and any other custom role) pass through unchanged; demoting a genuine user or
/// assistant turn would be wrong, not a security improvement. This is intentionally NOT applied to plain
/// chat-history replay (e.g. continuing an actual conversation via <c>Neo4jChatMessageStore</c>) -- only to
/// messages rendered as additional RECALLED context alongside the current turn, where a message persisted
/// via a caller-facing tool (e.g. the <c>memory_store_message</c> MCP tool or the Semantic Kernel adapter's
/// <c>add_message</c> function -- both accept an unvalidated, caller-supplied role string) could otherwise
/// resurface with full, undiminished authority.
/// </remarks>
internal static class RecalledMessageRoleGate
{
    private static readonly string[] PrivilegedRoles = ["system", "tool"];

    /// <summary>True when <paramref name="role"/> (case-insensitive) is one this gate treats as privileged.</summary>
    public static bool IsPrivileged(string role) =>
        PrivilegedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <paramref name="role"/> unchanged unless it's privileged and <paramref name="trustLevel"/>
    /// doesn't meet <paramref name="minimumTrustForSystemRole"/>, in which case it returns <c>"user"</c> --
    /// the safe, non-privileged role -- instead.
    /// </summary>
    public static string EffectiveRole(string role, MemoryTrustLevel trustLevel, MemoryTrustLevel minimumTrustForSystemRole) =>
        IsPrivileged(role) && trustLevel < minimumTrustForSystemRole ? "user" : role;
}
