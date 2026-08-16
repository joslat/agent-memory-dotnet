using System.Globalization;
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

    /// <summary>
    /// Reads the delta checkpoint — the instant this session last <b>acknowledged</b> memory changes —
    /// from the state bag, or <see langword="null"/> when this session has never acknowledged any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stored as a round-trip ISO-8601 string rather than a <see cref="DateTimeOffset"/> so it survives
    /// any state-bag serializer a host plugs in, and read back with
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/> so a host running under a
    /// non-Gregorian calendar cannot silently shift the window.
    /// </para>
    /// <para>
    /// Unparseable content returns <see langword="null"/>, which degrades to "brand-new session": full
    /// recall, no delta. Throwing here would take down a turn over a cosmetic token, and guessing a
    /// window from a corrupt value is how an agent ends up asserting a change set it never verified.
    /// </para>
    /// </remarks>
    public static DateTimeOffset? GetDeltaCheckpoint(
        this AgentSession? session, AgentFrameworkOptions? options = null)
    {
        var opts = options ?? new AgentFrameworkOptions();
        var bag = session?.StateBag;
        if (bag is null) return null;

        string? raw;
        try
        {
            bag.TryGetValue(opts.DefaultDeltaCheckpointKey, out raw, JsonSerializerOptions.Default);
        }
        catch (JsonException)
        {
            // A value of the wrong SHAPE (an object where a string belongs) throws rather than
            // returning false, and it means the same thing as an unparseable string here.
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw)) return null;

        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Writes the delta checkpoint into the state bag. Returns the session for chaining.
    /// </summary>
    /// <remarks>
    /// Advancing the checkpoint is an <b>acknowledgement</b>, not a read receipt: the provider advances
    /// it after a turn completes successfully, never at the moment the delta is fetched. A crash between
    /// the two replays the same delta, which is the harmless direction — the other one loses a change
    /// set permanently.
    /// </remarks>
    public static AgentSession SetDeltaCheckpoint(
        this AgentSession session, DateTimeOffset checkpoint, AgentFrameworkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var opts = options ?? new AgentFrameworkOptions();
        session.StateBag.SetValue(
            opts.DefaultDeltaCheckpointKey,
            checkpoint.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            JsonSerializerOptions.Default);

        return session;
    }
}

/// <summary>
/// The memory identity (owner, session, conversation, application/store) read off a MAF
/// <see cref="AgentSession"/>'s state bag via <see cref="AgentSessionMemoryExtensions.GetMemoryIdentity"/>.
/// </summary>
public readonly record struct MemoryIdentity(string? UserId, string? SessionId, string? ConversationId, string? ApplicationId);
