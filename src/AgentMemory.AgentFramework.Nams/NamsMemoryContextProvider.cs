using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Nams.Mapping;
using AgentMemory.AgentFramework.Recall;
using AgentMemory.AgentFramework.Security;
using AgentMemory.Nams.Identity;
using AgentMemory.Nams.Persistence;
using AgentMemory.Nams.Recall;

namespace AgentMemory.AgentFramework.Nams;

/// <summary>
/// The NAMS-backed equivalent of <c>AgentMemory.AgentFramework.Neo4jMemoryContextProvider</c> -- injects
/// NAMS-hosted memory into the agent's context before each run and persists the turn afterward. Lives in its
/// own package (ADR-9) rather than either <c>AgentMemory.Nams</c> (framework-free by design, B9) or
/// <c>AgentMemory.AgentFramework</c> (backend-neutral for the direct provider).
/// </summary>
public sealed class NamsMemoryContextProvider : AIContextProvider
{
    private readonly INamsConversationResolver _conversationResolver;
    private readonly INamsRecallService _recallService;
    private readonly INamsPersistenceService _persistenceService;
    private readonly ContextFormatOptions _formatOptions;
    private readonly AgentFrameworkOptions _agentOptions;
    private readonly IAutomaticRecallPolicy _recallPolicy;
    private readonly IMemoryContextAdmissionPolicy _admissionPolicy;
    private readonly ILogger<NamsMemoryContextProvider> _logger;

    public NamsMemoryContextProvider(
        INamsConversationResolver conversationResolver,
        INamsRecallService recallService,
        INamsPersistenceService persistenceService,
        IOptions<ContextFormatOptions> formatOptions,
        IOptions<AgentFrameworkOptions> agentOptions,
        ILogger<NamsMemoryContextProvider> logger,
        IAutomaticRecallPolicy? recallPolicy = null,
        IMemoryContextAdmissionPolicy? admissionPolicy = null)
        : base(null, null, null)
    {
        _conversationResolver = conversationResolver ?? throw new ArgumentNullException(nameof(conversationResolver));
        _recallService = recallService ?? throw new ArgumentNullException(nameof(recallService));
        _persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));
        _formatOptions = formatOptions?.Value ?? new ContextFormatOptions();
        _agentOptions = agentOptions?.Value ?? new AgentFrameworkOptions();
        _recallPolicy = recallPolicy ?? new ConfiguredAutomaticRecallPolicy();
        _admissionPolicy = admissionPolicy ?? new DefaultMemoryContextAdmissionPolicy();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Identifies this provider in the MAF pipeline for introspection.</summary>
    public string StateKey => "NamsMemory";

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var messages = context.AIContext?.Messages ?? Enumerable.Empty<ChatMessage>();
        var ids = ExtractIds(context.Session, context.Agent);
        return await BuildContextAsync(messages, ids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Core context-building logic, exposed internally for unit testing.</summary>
    internal async Task<AIContext> BuildContextAsync(
        IEnumerable<ChatMessage> messages, NamsIdentity ids, CancellationToken cancellationToken)
    {
        try
        {
            var userMessages = messages
                .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
                .ToList();

            if (userMessages.Count == 0)
                return BuildResult();

            if (ids.UserId is null)
            {
                // NAMS conversations are inherently user-scoped (Phase 3) -- there is no "anonymous NAMS
                // conversation" concept to fall back to, unlike the direct backend's shared/global memory.
                // Degrading (not fabricating an identity) matches this repo's established multi-tenant
                // isolation stance (#100): never silently merge anonymous callers into one identity.
                _logger.LogWarning("No UserId available for session {SessionId}; skipping NAMS recall.", ids.SessionId);
                return BuildResult();
            }

            var queryText = string.Join("\n", userMessages.Select(m => m.Text));

            // Partial IAutomaticRecallPolicy honoring: only ShouldRecall is used. Categories/RecallOptions/
            // Intent are direct-backend-specific concepts (RecallOptions.MaxX limits) with no NAMS
            // equivalent -- Phase 4's own NamsRecallOptions already governs category inclusion, statically,
            // at a different layer than this per-turn policy.
            var decision = await _recallPolicy
                .DecideAsync(
                    new AutomaticRecallContext
                    {
                        Messages = userMessages,
                        SessionId = ids.SessionId,
                        ConversationId = ids.LocalConversationId,
                        UserId = ids.UserId
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!decision.ShouldRecall)
                return BuildResult();

            var identity = new NamsConversationIdentity
            {
                ApplicationId = ids.ApplicationId,
                UserId = ids.UserId,
                SessionId = ids.SessionId,
                LocalConversationId = ids.LocalConversationId
            };

            NamsRecallResult recallResult;
            try
            {
                var resolution = await _conversationResolver.ResolveAsync(identity, cancellationToken).ConfigureAwait(false);
                recallResult = await _recallService.RecallAsync(resolution.NamsConversationId, queryText, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NAMS recall failed for session {SessionId}; returning empty context.", ids.SessionId);
                return BuildResult();
            }

            var contextMessages = NamsMafTypeMapper.ToContextMessages(recallResult, _formatOptions, _admissionPolicy, _logger);
            return contextMessages.Count == 0 ? BuildResult() : BuildResult(contextMessages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error in NamsMemoryContextProvider for session {SessionId}.", ids.SessionId);
            return BuildResult();
        }
    }

    /// <summary>No NAMS-backed <c>MemoryToolFactory</c> equivalent exists yet -- always null, trivially
    /// satisfying "expose tools only when explicitly enabled" by exposing none.</summary>
    private static AIContext BuildResult(IReadOnlyList<ChatMessage>? messages = null) => new() { Messages = messages, Tools = null };

    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
        {
            _logger.LogDebug("Skipping NAMS memory persistence: invocation failed with exception.");
            return;
        }

        var requestMessages = context.RequestMessages ?? Enumerable.Empty<ChatMessage>();
        var responseMessages = context.ResponseMessages ?? Enumerable.Empty<ChatMessage>();
        var ids = ExtractIds(context.Session, context.Agent);

        await PerformStoreAsync(requestMessages, responseMessages, ids, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Internal helper exposed for unit testing.</summary>
    internal async Task PerformStoreAsync(
        IEnumerable<ChatMessage> requestMessages, IEnumerable<ChatMessage> responseMessages,
        NamsIdentity ids, CancellationToken cancellationToken)
    {
        if (ids.UserId is null)
        {
            _logger.LogWarning("No UserId available for session {SessionId}; skipping NAMS persistence.", ids.SessionId);
            return;
        }

        try
        {
            var (userMessages, assistantMessages) = NamsMafTypeMapper.ToPersistenceMessages(requestMessages, responseMessages);
            if (userMessages.Count == 0 && assistantMessages.Count == 0)
                return;

            var identity = new NamsConversationIdentity
            {
                ApplicationId = ids.ApplicationId,
                UserId = ids.UserId,
                SessionId = ids.SessionId,
                LocalConversationId = ids.LocalConversationId
            };

            var resolution = await _conversationResolver.ResolveAsync(identity, cancellationToken).ConfigureAwait(false);

            // AgentFrameworkOptions.AutoExtractOnPersist is deliberately never read on this path -- NAMS owns
            // asynchronous extraction server-side, and nothing here calls any extraction-related method, so
            // the option is inertly ignored by construction rather than accidentally honored.
            await _persistenceService
                .PersistTurnAsync(resolution.NamsConversationId, userMessages, assistantMessages, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist NAMS messages after run for session {SessionId}.", ids.SessionId);
        }
    }

    internal NamsIdentity ExtractIds(AgentSession? session, AIAgent? agent)
    {
        MemoryIdentity identity;
        try
        {
            identity = session.GetMemoryIdentity(_agentOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract identity from state bag.");
            identity = default;
        }

        var sessionId = identity.SessionId ?? agent?.Id ?? Guid.NewGuid().ToString("N");
        var localConversationId = identity.ConversationId ?? sessionId;
        // Single-application hosts never configure an ApplicationId; "default" is a documented, harmless
        // fallback. UserId gets NO fabricated fallback -- see the caller-side check (#100's own stance).
        var applicationId = identity.ApplicationId ?? "default";

        return new NamsIdentity(applicationId, identity.UserId, sessionId, localConversationId);
    }
}

/// <summary>
/// Identity extracted from a MAF session for NAMS conversation resolution -- built from
/// <c>AgentMemory.AgentFramework.MemoryIdentity</c> by <see cref="NamsMemoryContextProvider.ExtractIds"/>.
/// Deliberately a distinct type (different field order -- Application/User/Session/LocalConversation here vs.
/// User/Session/Conversation/Application there -- and non-nullable <c>SessionId</c>/<c>LocalConversationId</c>
/// after fallback defaults are applied) rather than reusing <c>MemoryIdentity</c> directly, so the two are
/// never accidentally interchanged despite the similar name and shape.
/// </summary>
internal readonly record struct NamsIdentity(string ApplicationId, string? UserId, string SessionId, string LocalConversationId);
