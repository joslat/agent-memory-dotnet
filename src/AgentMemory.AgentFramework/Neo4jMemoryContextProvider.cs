using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.AgentFramework.Recall;
using AgentMemory.AgentFramework.Security;
using AgentMemory.AgentFramework.Tools;

namespace AgentMemory.AgentFramework;

/// <summary>
/// MAF context provider that injects relevant memory into the agent's context before each run.
/// </summary>
public sealed class Neo4jMemoryContextProvider : AIContextProvider
{
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly RecallOptions _recallOptions;
    private readonly ContextFormatOptions _formatOptions;
    private readonly AgentFrameworkOptions _agentOptions;
    private readonly IMemoryStoreContext? _storeContext;
    private readonly IWritableMemoryOwnerContext? _ownerContext;
    private readonly MemoryToolFactory? _toolFactory;
    private readonly IAutomaticRecallPolicy _recallPolicy;
    private readonly IMemoryContextAdmissionPolicy _admissionPolicy;
    private readonly ILogger<Neo4jMemoryContextProvider> _logger;

    public Neo4jMemoryContextProvider(
        IMemoryService memoryService,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IClock clock,
        IIdGenerator idGenerator,
        IOptions<MemoryOptions> memoryOptions,
        IOptions<ContextFormatOptions> formatOptions,
        IOptions<AgentFrameworkOptions> agentOptions,
        ILogger<Neo4jMemoryContextProvider> logger,
        IMemoryStoreContext? storeContext = null,
        IWritableMemoryOwnerContext? ownerContext = null,
        MemoryToolFactory? toolFactory = null,
        IAutomaticRecallPolicy? recallPolicy = null,
        IMemoryContextAdmissionPolicy? admissionPolicy = null)
        // AIContextProvider(IServiceProvider? sp, ILogger? logger, string? stateKey)
        // All three are passed as null: we supply our own ILogger via constructor injection,
        // we don't need the base-class IServiceProvider, and StateKey is exposed as our own property.
        : base(null, null, null)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _embeddingOrchestrator = embeddingOrchestrator ?? throw new ArgumentNullException(nameof(embeddingOrchestrator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _recallOptions = memoryOptions?.Value.Recall ?? RecallOptions.Default;
        _formatOptions = formatOptions?.Value ?? new ContextFormatOptions();
        _agentOptions = agentOptions?.Value ?? new AgentFrameworkOptions();
        _storeContext = storeContext;
        _ownerContext = ownerContext;
        _toolFactory = toolFactory;
        // Must stay the same type AddAgentMemoryFramework registers. A constructor default that differs
        // from the DI default gives one component two behaviours depending on how it was built, and the
        // direct-construction path (tools, tests, hosts wiring by hand) is exactly where that divergence
        // goes unnoticed -- it did here, until a perf run showed the counters had not moved.
        _recallPolicy = recallPolicy ?? new TrivialTurnRecallPolicy();
        _admissionPolicy = admissionPolicy ?? new DefaultMemoryContextAdmissionPolicy();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Identifies this provider in the MAF pipeline for introspection.</summary>
    public string StateKey => "Neo4jMemory";

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var messages = context.AIContext?.Messages ?? Enumerable.Empty<ChatMessage>();
        var ids = ExtractIds(context.Session, context.Agent);
        using var storeScope = ApplyStoreContext(ids.applicationId);
        return await BuildContextAsync(
                messages, ids.sessionId, ids.conversationId, cancellationToken, ids.userId,
                ReadDeltaCheckpoint(context.Session), context.Session)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the session's delta checkpoint, or <see langword="null"/> when the feature is off.
    /// </summary>
    /// <remarks>
    /// Gated on the flag <b>here</b>, not at the use site, so that with the feature off this provider
    /// never touches the state bag for a key it does not use — the off state is not merely
    /// byte-identical in output, it performs no extra work at all.
    /// </remarks>
    private DateTimeOffset? ReadDeltaCheckpoint(AgentSession? session)
    {
        if (!_agentOptions.InjectDeltaOnSessionResume) return null;

        try
        {
            return session.GetDeltaCheckpoint(_agentOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the delta checkpoint from the state bag.");
            return null;
        }
    }

    /// <summary>
    /// The state-bag key holding the checkpoint a delta was <i>read</i> at, awaiting acknowledgement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second key, because the value has to survive from <c>ProvideAIContextAsync</c> to
    /// <c>StoreAIContextAsync</c> and there is nowhere else it can live: an instance field would be
    /// shared across every concurrent session on this provider, and an <c>AsyncLocal</c> set in the
    /// provide hook never reaches the store hook — execution context flows into nested calls, not back
    /// out of them.
    /// </para>
    /// <para>
    /// Derived from the configured key so a host that renames one renames both.
    /// </para>
    /// </remarks>
    private string PendingDeltaCheckpointKey => _agentOptions.DefaultDeltaCheckpointKey + ":pending";

    /// <summary>Records the instant a delta was read at, without acknowledging it.</summary>
    private void StagePendingCheckpoint(AgentSession? session, DateTimeOffset takenAt)
    {
        if (session is null) return;

        try
        {
            session.StateBag.SetValue(
                PendingDeltaCheckpointKey,
                takenAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                JsonSerializerOptions.Default);
        }
        catch (Exception ex)
        {
            // The checkpoint simply does not advance to the delta's instant; the fallback below stamps
            // the turn's end. Losing a staging write is a cost question, never a correctness one.
            _logger.LogDebug(ex, "Could not stage the delta checkpoint on the state bag.");
        }
    }

    /// <summary>
    /// Advances the delta checkpoint after a turn the agent actually completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Advancing is an acknowledgement, not a read receipt</b> — the distinction that disqualified
    /// deriving the checkpoint from the read-audit trail. A turn that threw never advances, so its delta
    /// is replayed next time. Replaying a change set is harmless; marking one acknowledged that the
    /// agent never saw loses it permanently.
    /// </para>
    /// <para>
    /// It advances to the delta's own <c>TakenAtUtc</c>, not to now: the window between the delta being
    /// read and the turn finishing was never reported to the agent, and stamping now would mark it
    /// acknowledged. That window is short but it spans a model call, which is exactly long enough for a
    /// concurrent writer to land in it.
    /// </para>
    /// <para>
    /// Every turn advances, delta or not. The checkpoint marks the last moment the agent was present,
    /// so mid-session turns must move it — otherwise the next resume re-reports everything the agent sat
    /// through live.
    /// </para>
    /// </remarks>
    private void AdvanceDeltaCheckpoint(AgentSession? session)
    {
        if (!_agentOptions.InjectDeltaOnSessionResume || session is null) return;

        try
        {
            var current = session.GetDeltaCheckpoint(_agentOptions);
            var staged = ReadPendingCheckpoint(session);

            // Stale-staging guard: a staged value that is not newer than the checkpoint has already been
            // acknowledged on an earlier turn. Promoting it again would move the checkpoint BACKWARDS and
            // replay that window forever. This is also what clears the staging slot without needing to
            // remove a key -- once promoted, it is no longer newer.
            var advanceTo = staged is not null && (current is null || staged > current)
                ? staged.Value
                : _clock.UtcNow;

            session.SetDeltaCheckpoint(advanceTo, _agentOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not advance the delta checkpoint on the state bag.");
        }
    }

    private DateTimeOffset? ReadPendingCheckpoint(AgentSession? session)
    {
        var bag = session?.StateBag;
        if (bag is null) return null;

        try
        {
            bag.TryGetValue(PendingDeltaCheckpointKey, out string? raw, JsonSerializerOptions.Default);
            return DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Core context-building logic, exposed internally for unit testing.
    /// </summary>
    internal async Task<AIContext> BuildContextAsync(
        IEnumerable<ChatMessage> messages,
        string sessionId,
        string conversationId,
        CancellationToken cancellationToken,
        string? userId = null,
        DateTimeOffset? deltaCheckpoint = null,
        AgentSession? session = null)
    {
        // Set the ambient owner BEFORE recall so the LLM-invokable facade tools the agent calls mid-turn
        // (search_memory / remember_* etc.) scope to this owner instead of running unscoped. Scoped (not a
        // bare assignment) so the value is restored once this hook returns rather than leaking into
        // whatever runs next on this ambient context. NOTE: this hook still can't, on its own, guarantee
        // the value survives into the tool-calling loop that runs AFTER it returns -- see
        // MemoryOwnerScopingAgent (#90), which wraps the complete invocation for that guarantee.
        using var ownerScope = _ownerContext?.BeginOwnerScope(userId);
        // Declared outside the try so every early return -- no user messages, policy said don't recall,
        // recall threw -- still carries it. A resume delta that only survives the happy path is a resume
        // delta that vanishes on exactly the turns where knowing what changed matters most.
        ChatMessage? deltaMessage = null;
        try
        {
            deltaMessage = await TryBuildDeltaMessageAsync(
                deltaCheckpoint, sessionId, userId, session, cancellationToken).ConfigureAwait(false);

            // Materialised once: the thread is enumerated for the query below AND handed to the
            // mapper for dedup, and `messages` is an IEnumerable that a caller may well have built
            // lazily. Enumerating it twice would be a silent correctness bug for a generator source.
            var liveThread = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

            var userMessages = liveThread
                .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
                .ToList();

            if (userMessages.Count == 0)
                return BuildResult(null, deltaMessage);

            var queryText = string.Join("\n", userMessages.Select(m => m.Text));

            // Task-aware automatic recall (#88): let the policy decide, before anything is queried,
            // whether this turn needs recall at all and which categories/intent are relevant. The default
            // ConfiguredAutomaticRecallPolicy always returns Categories=All + Intent=null, which
            // ResolveEffectiveOptions below turns into a complete no-op -- so the pre-#88 behavior is
            // preserved exactly unless a host opts into a different policy.
            var decision = await _recallPolicy
                .DecideAsync(
                    new AutomaticRecallContext
                    {
                        Messages = userMessages,
                        SessionId = sessionId,
                        ConversationId = conversationId,
                        UserId = userId
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Automatic recall decision for session {SessionId}: policy={Policy} recall={ShouldRecall} " +
                "categories={Categories} intent={Intent}",
                sessionId, _recallPolicy.GetType().Name, decision.ShouldRecall, decision.Categories, decision.Intent);

            if (!decision.ShouldRecall)
                return BuildResult(null, deltaMessage);

            var effectiveOptions = ResolveEffectiveOptions(decision);

            // Only embed if a retrieval that consumes the vector survived the policy's narrowing.
            // A policy that excludes every vector category (e.g. "recent messages only" on a trivial
            // turn) would otherwise still pay the largest single stage of a remote-shaped recall for a
            // vector nothing reads. The assembler applies the same gate for its other callers; this one
            // is needed because the provider embeds BEFORE handing the request over, so narrowing alone
            // would relocate the call rather than remove it.
            //
            // Null is already the established "no embedding" value here — the catch below hands the
            // assembler a null on failure and it degrades to recent messages — so this adds no new state.
            float[]? queryEmbedding = null;
            if (RecallNeedsQueryEmbedding(effectiveOptions))
            {
                try
                {
                    using var embeddingSpan = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.embedding");
                    queryEmbedding = await _embeddingOrchestrator
                        .EmbedQueryAsync(queryText, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate query embedding; proceeding without semantic search.");
                }
            }

            var recallRequest = new RecallRequest
            {
                SessionId = sessionId,
                UserId = userId,
                Query = queryText,
                QueryEmbedding = queryEmbedding,
                Options = effectiveOptions
            };

            RecallResult recallResult;
            try
            {
                recallResult = await _memoryService
                    .RecallAsync(recallRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory recall failed for session {SessionId}; returning empty context.", sessionId);
                return BuildResult(null, deltaMessage);
            }

            // 2.5. The host is already sending the live thread; recall returns the same recent turns
            // from storage, so without this the model sees them twice and pays for both. Passed here
            // rather than filtered afterwards because the mapper drops duplicates BEFORE applying
            // MaxChatHistoryMessages -- so the same budget carries that many genuinely new messages.
            var contextMessages = MafTypeMapper.ToContextMessages(
                recallResult.Context, _formatOptions, _admissionPolicy, _logger,
                _agentOptions.DeduplicateRecalledHistory ? liveThread : null);

            if (contextMessages.Count == 0)
                return BuildResult(null, deltaMessage);

            return BuildResult(contextMessages, deltaMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error in Neo4jMemoryContextProvider for session {SessionId}.", sessionId);
            return BuildResult(null, deltaMessage);
        }
    }

    /// <summary>
    /// Builds the <see cref="AIContext"/> returned by every branch of <see cref="BuildContextAsync"/>, so
    /// tool exposure is consistent regardless of whether this turn had recall hits, no user messages, or
    /// a recall failure -- a turn with nothing to recall must not silently lose tool availability.
    /// </summary>
    /// <param name="messages">The recall block, if this turn produced one.</param>
    /// <param name="deltaMessage">
    /// The resume delta, prepended ahead of recall. When null — which is every turn with
    /// <see cref="AgentFrameworkOptions.InjectDeltaOnSessionResume"/> off — <c>Messages</c> is the exact
    /// same reference it was before 30.5, so the off state is byte-identical rather than merely
    /// equivalent.
    /// </param>
    private AIContext BuildResult(
        IReadOnlyList<ChatMessage>? messages = null, ChatMessage? deltaMessage = null)
    {
        IReadOnlyList<ChatMessage>? combined = messages;
        if (deltaMessage is not null)
        {
            // Delta first: it frames what follows. Recall answers the current question; the delta says
            // what moved underneath while nobody was asking.
            var list = new List<ChatMessage>((messages?.Count ?? 0) + 1) { deltaMessage };
            if (messages is not null) list.AddRange(messages);
            combined = list;
        }

        return new AIContext
        {
            Messages = combined,
            Tools = _agentOptions.ExposeMemoryToolsFromContextProvider
                ? _toolFactory?.CreateAIFunctions()
                : null,
        };
    }

    /// <summary>
    /// Fetches and renders the resume delta, or returns <see langword="null"/> when this turn does not
    /// get one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The decision table, in three lines.</b> No checkpoint ⇒ brand-new session, full recall only.
    /// Checkpoint younger than <see cref="AgentFrameworkOptions.MinimumDeltaGap"/> ⇒ a mid-session turn,
    /// nothing to catch up on. Older ⇒ a resume: the delta is injected <i>in addition to</i> normal
    /// recall, never instead of it.
    /// </para>
    /// <para>
    /// The gap heuristic is deliberate. There is no session lifecycle in this system — a session is a
    /// string — so "resume" cannot be read off a close event that does not exist. An age threshold is
    /// deterministic, stateless beyond the token, and wrong only in the benign direction.
    /// </para>
    /// <para>
    /// Every failure degrades to normal recall, matching the recall path's own catch. A delta is an
    /// enrichment; taking down a turn because the enrichment failed inverts its value.
    /// </para>
    /// </remarks>
    private async Task<ChatMessage?> TryBuildDeltaMessageAsync(
        DateTimeOffset? checkpoint,
        string sessionId,
        string? userId,
        AgentSession? session,
        CancellationToken cancellationToken)
    {
        if (!_agentOptions.InjectDeltaOnSessionResume || checkpoint is null) return null;

        var age = _clock.UtcNow - checkpoint.Value;
        if (age < _agentOptions.MinimumDeltaGap) return null;

        try
        {
            var delta = await _memoryService.RecallChangedSinceAsync(
                new MemoryDeltaRequest
                {
                    Since = checkpoint.Value,
                    UserId = userId,
                    MaxItemsPerSection = _agentOptions.MaxDeltaItemsPerSection,
                },
                cancellationToken).ConfigureAwait(false);

            // Stamped even when the delta is empty. "Nothing changed" is still an answer the agent was
            // given, and re-reporting an empty window next turn would be pure waste.
            StagePendingCheckpoint(session, delta.TakenAtUtc);

            // The host's own admission policy, not Core's built-in one: a custom policy applied to every
            // category except this one would be a hole shaped exactly like a new feature.
            var rendered = AgentMemory.Core.Services.MemoryDeltaFormatter.Format(
                delta, options: null, _logger,
                admit: (content, trust) => MafTypeMapper.AdmitItem(
                    "delta", content, trust, _formatOptions, _admissionPolicy, _logger));
            if (string.IsNullOrEmpty(rendered)) return null;

            return new ChatMessage(
                MafTypeMapper.RecalledBlockChatRole(MemoryTrustLevel.Untrusted, _formatOptions),
                rendered);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Delta recall failed for session {SessionId}; continuing with normal recall.", sessionId);
            return null;
        }
    }

    /// <summary>
    /// Resolves the effective <see cref="RecallOptions"/> for this turn from the policy's decision (#88).
    /// An explicit <see cref="AutomaticRecallDecision.RecallOptions"/> is used verbatim; otherwise the
    /// configured base <see cref="RecallOptions"/> is used, with the per-category limit zeroed for every
    /// <see cref="AutomaticRecallCategories"/> flag the decision excludes (so <c>MemoryContextAssembler</c>
    /// skips that category's retrieval entirely instead of querying and discarding it) and
    /// <see cref="AutomaticRecallDecision.Intent"/> applied when set. Either way, <c>Scope</c> is always
    /// cleared afterwards: scope must always be resolved from this invocation's authenticated userId (via
    /// #100's isolation policy), never from a statically configured or policy-supplied value.
    /// </summary>
    /// <summary>
    /// Whether any retrieval selected by <paramref name="options"/> consumes a query vector.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>MemoryContextAssembler</c>'s own gate on exactly the five categories whose searches
    /// require an embedding. Recent messages are session-scoped and time-ordered; GraphRAG builds its
    /// own retriever. Kept as a separate copy rather than shared because the two live in different
    /// packages, and <c>MemoryContextAssembler</c>'s is <c>private</c> — the shared thing is the rule,
    /// which is stated in both places and covered by tests on both sides.
    /// </remarks>
    private static bool RecallNeedsQueryEmbedding(RecallOptions options) =>
        options.MaxRelevantMessages > 0
        || options.MaxEntities > 0
        || options.MaxPreferences > 0
        || options.MaxFacts > 0
        || options.MaxTraces > 0;

    private RecallOptions ResolveEffectiveOptions(AutomaticRecallDecision decision)
    {
        var effective = decision.RecallOptions ?? _recallOptions with
        {
            MaxRecentMessages = decision.Categories.HasFlag(AutomaticRecallCategories.RecentMessages) ? _recallOptions.MaxRecentMessages : 0,
            MaxRelevantMessages = decision.Categories.HasFlag(AutomaticRecallCategories.RelevantMessages) ? _recallOptions.MaxRelevantMessages : 0,
            MaxEntities = decision.Categories.HasFlag(AutomaticRecallCategories.Entities) ? _recallOptions.MaxEntities : 0,
            MaxFacts = decision.Categories.HasFlag(AutomaticRecallCategories.Facts) ? _recallOptions.MaxFacts : 0,
            MaxPreferences = decision.Categories.HasFlag(AutomaticRecallCategories.Preferences) ? _recallOptions.MaxPreferences : 0,
            // J4.1: the aggregation route. Top-K cannot answer "how many", so a routed decision
            // turns on relation completeness for that turn only - it roughly triples the retrieved
            // context, which is why it is routed rather than defaulted on.
            ExpandFactsByPredicate =
                _recallOptions.ExpandFactsByPredicate || decision.RequiresRelationCompleteness,
            ResolveQueryRelations =
                _recallOptions.ResolveQueryRelations || decision.RequiresRelationCompleteness,
            MaxTraces = decision.Categories.HasFlag(AutomaticRecallCategories.ReasoningTraces) ? _recallOptions.MaxTraces : 0,
            MaxGraphRagItems = decision.Categories.HasFlag(AutomaticRecallCategories.GraphRag) ? _recallOptions.MaxGraphRagItems : 0,
            Intent = decision.Intent ?? _recallOptions.Intent
        };

        return effective with { Scope = null };
    }

    /// <summary>
    /// Post-run hook: persists response messages and optionally triggers extraction.
    /// Skipped if the invocation raised an exception. Failures are logged but never re-thrown.
    /// </summary>
    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
        {
            _logger.LogDebug("Skipping memory persistence: invocation failed with exception.");
            return;
        }

        var requestMessages = context.RequestMessages ?? Enumerable.Empty<ChatMessage>();
        var responseMessages = context.ResponseMessages ?? Enumerable.Empty<ChatMessage>();
        var ids = ExtractIds(context.Session, context.Agent);
        using var storeScope = ApplyStoreContext(ids.applicationId);

        await PerformStoreAsync(requestMessages, responseMessages, ids.sessionId, ids.conversationId, cancellationToken, ids.userId)
            .ConfigureAwait(false);

        // After persistence, and only on a turn that did not throw (the guard above): the checkpoint
        // records what the agent has acknowledged, and a turn that failed acknowledged nothing.
        AdvanceDeltaCheckpoint(context.Session);
    }

    /// <summary>Internal helper exposed for unit testing.</summary>
    internal async Task PerformStoreAsync(
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage> responseMessages,
        string sessionId,
        string conversationId,
        CancellationToken cancellationToken,
        string? userId = null)
    {
        using var ownerScope = _ownerContext?.BeginOwnerScope(userId);
        try
        {
            // Response messages are persisted as new :Message nodes, as before. Request messages are
            // deliberately NOT persisted here (#89): ChatMessage.MessageId (used below for response-side
            // dedup) is essentially never populated on caller-constructed request messages, so it can't
            // help there -- request-message persistence ownership intentionally stays solely with
            // Neo4jChatHistoryProvider. Building transient (never-persisted) Message objects for extraction
            // only captures what the user said without risking a duplicate node. Filtered to ChatRole.User
            // -- the same filter recall already applies (BuildContextAsync above) -- so a system prompt or
            // other non-user content accumulated in RequestMessages doesn't get minted into spurious
            // entities/facts/preferences every turn.
            var transientRequestMessages = requestMessages
                .Where(msg => msg.Role == ChatRole.User && !string.IsNullOrWhiteSpace(msg.Text))
                .Select(msg => MafTypeMapper.ToInternalMessage(msg, sessionId, conversationId, _clock, _idGenerator))
                .ToList();

            var storedMessages = await StoreResponseMessagesAsync(
                responseMessages, sessionId, conversationId, cancellationToken).ConfigureAwait(false);

            // Extraction sees the complete turn (#89): a fact or preference the user states in their
            // request is now captured even if the assistant never repeats it back. Because
            // transientRequestMessages were never persisted as :Message nodes above, provenance for
            // anything extracted from them is incomplete: the EXTRACTED_FROM edge silently fails to
            // attach (the MATCH in PersistenceStage's linking Cypher finds no such node), AND the
            // extracted node's own source_message_ids property will still list the transient (never
            // persisted) message id, i.e. a dangling reference, not just a missing edge. The extracted
            // fact/preference itself is still created and recallable correctly; only its link back to the
            // literal source message is best-effort, not guaranteed, unless another component also
            // persisted that exact message.
            var turnMessages = transientRequestMessages.Concat(storedMessages).ToList();
            if (_agentOptions.AutoExtractOnPersist && turnMessages.Count > 0)
            {
                using var extractSpan = AgentMemoryDiagnostics.Source.StartActivity("memory.store.extract");
                extractSpan?.SetTag("memory.extract.source_messages", turnMessages.Count);
                try
                {
                    await _memoryService.ExtractAndPersistAsync(
                        new ExtractionRequest
                        {
                            Messages = turnMessages,
                            SessionId = sessionId,
                            UserId = userId
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Extraction failed for session {SessionId}; messages were persisted.", sessionId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist messages after run for session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// Persists the run's response messages, one node each, inside a <c>memory.store.messages</c> span.
    /// Extracted from <see cref="PerformStoreAsync"/> so the per-message loop is attributable in a trace
    /// without nesting a <c>using</c> block around the surrounding logic; behaviour is unchanged.
    /// </summary>
    private async Task<List<Message>> StoreResponseMessagesAsync(
        IEnumerable<ChatMessage> responseMessages,
        string sessionId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        using var span = AgentMemoryDiagnostics.Source.StartActivity("memory.store.messages");
        var storedMessages = new List<Message>();

        foreach (var msg in responseMessages)
        {
            if (string.IsNullOrWhiteSpace(msg.Text)) continue;

            // When the underlying IChatClient stamps a provider-native MessageId on this response
            // message, persist it under a deterministic id (#89): if another persisting component
            // (e.g. Neo4jChatHistoryProvider, Neo4jChatMessageStore) configured on the same agent
            // observes the same response, it converges on this same :Message node instead of creating
            // a duplicate. Falls back to today's fresh-id behavior when absent (no regression).
            var providerId = MafTypeMapper.TryGetProviderMessageId(msg);
            var stored = providerId is not null
                ? await _memoryService
                    .AddMessageWithIdAsync(
                        sessionId, conversationId,
                        MafTypeMapper.ToInternalRole(msg.Role),
                        msg.Text, providerId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false)
                : await _memoryService
                    .AddMessageAsync(
                        sessionId, conversationId,
                        MafTypeMapper.ToInternalRole(msg.Role),
                        msg.Text,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            storedMessages.Add(stored);
        }

        span?.SetTag("memory.store.message_count", storedMessages.Count);
        return storedMessages;
    }

    private (string sessionId, string conversationId, string? userId, string? applicationId) ExtractIds(
        AgentSession? session,
        AIAgent? agent)
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
        // P2-4: Fall back to sessionId (not a new GUID) to preserve cross-turn correlation.
        var conversationId = identity.ConversationId ?? sessionId;

        return (sessionId, conversationId, identity.UserId, identity.ApplicationId);
    }

    // Routes the memory store for this scope when an application_id is supplied and a writable store
    // context is registered (R1b). No-op otherwise. Scoped (not a bare assignment) so the value is
    // restored once this hook returns. Mutating a singleton context is only safe for one application per
    // host; register a scoped IMemoryStoreContext to route per request, or use MemoryOwnerScopingAgent
    // (#90) to guarantee the scope spans the complete invocation including the tool-calling loop.
    private IDisposable? ApplyStoreContext(string? applicationId) =>
        applicationId is not null && _storeContext is IWritableMemoryStoreContext writable
            ? writable.BeginStoreScope(applicationId)
            : null;
}
