using Microsoft.Extensions.Logging;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Identity.Internal;

namespace AgentMemory.Nams.Identity;

/// <summary>
/// Implements the engineering plan's §7 Phase 3 resolution algorithm: validate identity -> read existing
/// mapping -> (if absent) acquire a process-local per-(session, local-conversation) creation
/// lock -> re-check under the lock -> create via <see cref="INamsClient.CreateConversationAsync"/> with
/// correlation metadata -> atomically persist -> on a lost cross-store race, reconcile onto the winning
/// mapping instead of returning an orphaned conversation ID.
///
/// The state store's lookup/lock key is (session, local-conversation) -- deliberately NOT including
/// application or user. A session/local-conversation slot is a single logical binding; the mapping's stored
/// <see cref="NamsConversationIdentity.ApplicationId"/>/<see cref="NamsConversationIdentity.UserId"/> are
/// data checked against the requester, not independent key axes. This is what makes "changed
/// user/application rejects stale mapping" meaningful: if either were part of the key, a different
/// user/application could never collide with an existing slot in the first place, and there would be
/// nothing to reject.
/// </summary>
internal sealed class NamsConversationResolver : INamsConversationResolver
{
    private readonly INamsClient _namsClient;
    private readonly INamsConversationStateStore _stateStore;
    private readonly KeyedAsyncLock _creationLock = new();
    private readonly ILogger<NamsConversationResolver> _logger;

    public NamsConversationResolver(
        INamsClient namsClient, INamsConversationStateStore stateStore, ILogger<NamsConversationResolver> logger)
    {
        _namsClient = namsClient;
        _stateStore = stateStore;
        _logger = logger;
    }

    public async Task<NamsConversationResolutionResult> ResolveAsync(
        NamsConversationIdentity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);

        var existing = await ReadAsync(identity, cancellationToken).ConfigureAwait(false);
        if (TryResolveExisting(existing, identity, out var existingId))
            return new NamsConversationResolutionResult(existingId, WasCreated: false);

        var lockKey = BuildCompositeKey(identity);
        using (await _creationLock.AcquireAsync(lockKey, cancellationToken).ConfigureAwait(false))
        {
            existing = await ReadAsync(identity, cancellationToken).ConfigureAwait(false);
            if (TryResolveExisting(existing, identity, out existingId))
                return new NamsConversationResolutionResult(existingId, WasCreated: false);

            var conversation = await _namsClient.CreateConversationAsync(
                identity.UserId, BuildCorrelationMetadata(identity), cancellationToken).ConfigureAwait(false);

            var resolved = identity with { NamsConversationId = conversation.Id };
            var created = await _stateStore.TryCreateAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (created)
                return new NamsConversationResolutionResult(conversation.Id, WasCreated: true);

            // Lost the atomic write -- someone else's mapping already occupies this key (e.g. another
            // process racing via a shared durable store, or -- the crash-window scenario -- a prior process
            // created one but died before its own atomic write landed). Reconcile onto the winner's mapping
            // rather than returning our own now-orphaned conversation; the plan explicitly accepts this
            // residual duplicate-conversation risk rather than claiming an exactly-once guarantee NAMS itself
            // doesn't confirm. TryResolveExisting still enforces the user/application check here, so a
            // cross-tenant race correctly throws instead of silently reconciling onto the wrong tenant.
            existing = await ReadAsync(identity, cancellationToken).ConfigureAwait(false);
            if (TryResolveExisting(existing, identity, out existingId))
            {
                _logger.LogWarning(
                    "Reconciled a concurrent NAMS conversation creation race for application {ApplicationId}, " +
                    "session {SessionId}, local conversation {LocalConversationId}; discarding orphaned " +
                    "conversation {OrphanedId} in favor of {WinnerId}.",
                    identity.ApplicationId, identity.SessionId, identity.LocalConversationId, conversation.Id, existingId);
                return new NamsConversationResolutionResult(existingId, WasCreated: false);
            }

            // The store reported the write lost but a subsequent read found nothing valid -- should not
            // happen with a correctly-implemented store; fail loudly rather than silently return an
            // unpersisted mapping.
            throw new InvalidOperationException(
                "NAMS conversation state store reported a conflicting write but no valid mapping could be read back.");
        }
    }

    private Task<NamsConversationIdentity?> ReadAsync(NamsConversationIdentity identity, CancellationToken cancellationToken) =>
        _stateStore.TryGetAsync(identity.SessionId, identity.LocalConversationId, cancellationToken);

    /// <summary>Returns <c>true</c> with the valid, tenant-matching conversation ID from <paramref name="existing"/>.
    /// Returns <c>false</c> if <paramref name="existing"/> is genuinely absent (caller should proceed to
    /// create). Throws <see cref="NamsConversationIdentityConflictException"/> if <paramref name="existing"/>
    /// belongs to a different user or application. Throws <see cref="InvalidOperationException"/> if
    /// <paramref name="existing"/> matches the tenant but its conversation ID is blank -- a corrupted store
    /// entry the atomic add-if-absent contract can never self-heal, so this fails immediately rather than
    /// falling through to a doomed creation attempt that would just collide on the same occupied key.</summary>
    private static bool TryResolveExisting(
        NamsConversationIdentity? existing, NamsConversationIdentity requested, out string namsConversationId)
    {
        namsConversationId = string.Empty;
        if (existing is null)
            return false;

        if (existing.UserId != requested.UserId || existing.ApplicationId != requested.ApplicationId)
            throw new NamsConversationIdentityConflictException(
                $"A NAMS conversation mapping already exists for session '{requested.SessionId}' / local " +
                $"conversation '{requested.LocalConversationId}', bound to a different user or application -- " +
                "refusing to reuse it.");

        if (string.IsNullOrWhiteSpace(existing.NamsConversationId))
            throw new InvalidOperationException(
                $"NAMS conversation state store contains a corrupted mapping (blank conversation ID) for " +
                $"session '{requested.SessionId}' / local conversation '{requested.LocalConversationId}'. This " +
                "entry must be repaired or removed out-of-band before this identity can be resolved again.");

        namsConversationId = existing.NamsConversationId;
        return true;
    }

    private static void ValidateIdentity(NamsConversationIdentity identity)
    {
        Require(identity.ApplicationId, nameof(identity.ApplicationId));
        Require(identity.UserId, nameof(identity.UserId));
        Require(identity.SessionId, nameof(identity.SessionId));
        Require(identity.LocalConversationId, nameof(identity.LocalConversationId));

        static void Require(string value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"NamsConversationIdentity.{fieldName} is required.", nameof(identity));
        }
    }

    private static string BuildCompositeKey(NamsConversationIdentity identity) =>
        string.Join('\0', identity.SessionId, identity.LocalConversationId);

    private static IReadOnlyDictionary<string, string> BuildCorrelationMetadata(NamsConversationIdentity identity) =>
        new Dictionary<string, string>
        {
            ["agentMemoryApplicationId"] = identity.ApplicationId,
            ["agentMemorySessionId"] = identity.SessionId,
            ["agentMemoryConversationId"] = identity.LocalConversationId,
            ["integration"] = "AgentMemory.NET",
            ["integrationVersion"] = typeof(NamsConversationResolver).Assembly.GetName().Version?.ToString() ?? "unknown"
        };
}
