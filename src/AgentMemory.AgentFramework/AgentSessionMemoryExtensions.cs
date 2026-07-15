using System.Text.Json;
using Microsoft.Agents.AI;

namespace AgentMemory.AgentFramework;

/// <summary>
/// Ergonomic helpers for stamping memory identity onto a MAF <see cref="AgentSession"/>'s state bag,
/// which the Neo4j memory providers read to scope recall/extraction (R1 owner, R1b store).
/// </summary>
public static class AgentSessionMemoryExtensions
{
    /// <summary>
    /// Writes the supplied identity values into the session's <c>StateBag</c> using the configured
    /// key names, so <see cref="Neo4jMemoryContextProvider"/> and <see cref="Neo4jChatHistoryProvider"/>
    /// pick them up. Only non-null values are written. Returns the session for chaining.
    /// </summary>
    /// <param name="session">The MAF agent session.</param>
    /// <param name="userId">Owner/user id (R1), used for BOTH recall scoping and write owner-stamping. On
    /// write, null ⇒ the record is stored as shared/global (owner_id = null). On recall, null ⇒ no owner
    /// filter (returns all owners); set it to confine recall to this owner's plus shared memory.</param>
    /// <param name="sessionId">Session id; defaults to the provider's fallback when omitted.</param>
    /// <param name="conversationId">Conversation id; defaults to the session id when omitted.</param>
    /// <param name="applicationId">Application / memory-store id (R1b). Null ⇒ default store.</param>
    /// <param name="options">
    /// Options supplying the StateBag key names. Pass the same instance you registered if you
    /// customized the keys; otherwise the defaults (<c>user_id</c>/<c>session_id</c>/…) are used.
    /// </param>
    public static AgentSession WithMemoryIdentity(
        this AgentSession session,
        string? userId = null,
        string? sessionId = null,
        string? conversationId = null,
        string? applicationId = null,
        AgentFrameworkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var opts = options ?? new AgentFrameworkOptions();
        var bag = session.StateBag;
        var json = JsonSerializerOptions.Default;

        if (sessionId is not null) bag.SetValue(opts.DefaultSessionIdKey, sessionId, json);
        if (conversationId is not null) bag.SetValue(opts.DefaultConversationIdKey, conversationId, json);
        if (userId is not null) bag.SetValue(opts.DefaultUserIdKey, userId, json);
        if (applicationId is not null) bag.SetValue(opts.DefaultApplicationIdKey, applicationId, json);

        return session;
    }

    /// <summary>
    /// Reads the identity values <see cref="WithMemoryIdentity"/> wrote into the session's
    /// <c>StateBag</c> — the single source of truth both Neo4j memory providers and
    /// <see cref="MemoryOwnerScopingAgent"/> read from, instead of each duplicating the lookup. Blank
    /// values are normalized to <c>null</c>. Does not apply any fallback (e.g. deriving a session id from
    /// the agent) — callers that need that own it themselves.
    /// </summary>
    /// <param name="session">The MAF agent session, or null.</param>
    /// <param name="options">
    /// Options supplying the StateBag key names. Pass the same instance you registered if you
    /// customized the keys; otherwise the defaults (<c>user_id</c>/<c>session_id</c>/…) are used.
    /// </param>
    public static MemoryIdentity GetMemoryIdentity(this AgentSession? session, AgentFrameworkOptions? options = null)
    {
        var opts = options ?? new AgentFrameworkOptions();
        var bag = session?.StateBag;
        if (bag is null) return default;

        var json = JsonSerializerOptions.Default;
        bag.TryGetValue(opts.DefaultSessionIdKey, out string? sessionId, json);
        bag.TryGetValue(opts.DefaultConversationIdKey, out string? conversationId, json);
        bag.TryGetValue(opts.DefaultUserIdKey, out string? userId, json);
        bag.TryGetValue(opts.DefaultApplicationIdKey, out string? applicationId, json);

        return new MemoryIdentity(
            string.IsNullOrWhiteSpace(userId) ? null : userId,
            string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            string.IsNullOrWhiteSpace(conversationId) ? null : conversationId,
            string.IsNullOrWhiteSpace(applicationId) ? null : applicationId);
    }
}

/// <summary>
/// The memory identity (owner, session, conversation, application/store) read off a MAF
/// <see cref="AgentSession"/>'s state bag via <see cref="AgentSessionMemoryExtensions.GetMemoryIdentity"/>.
/// </summary>
public readonly record struct MemoryIdentity(string? UserId, string? SessionId, string? ConversationId, string? ApplicationId);
