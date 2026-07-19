using AgentMemory.Nams.Domain;

namespace AgentMemory.Nams.Client;

/// <summary>
/// The low-level NAMS REST boundary. Application and (future) MAF provider code must never depend on an external
/// NAMS SDK type directly -- everything crossing that boundary is contained inside <c>AgentMemory.Nams</c> behind
/// this interface (engineering plan §7 Phase 2, "Rule").
///
/// Started (Phase 2) with only the 4 operations confirmed against the pinned OpenAPI snapshot
/// (<c>docs/reviews/nams-openapi-snapshot-2026-07-17.json</c>); a 5th method the plan's own example included --
/// <c>SearchMessagesAsync</c> -- was deliberately dropped at the time as unconfirmed. Both that method and
/// <see cref="ListEntitiesAsync"/> (Phase 9) have since been confirmed against the live NAMS SaaS and added --
/// see each method's own doc comment for when/why.
/// </summary>
internal interface INamsClient
{
    Task<NamsConversation> CreateConversationAsync(
        string? userId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task<NamsContext> GetContextAsync(
        string conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
        string conversationId,
        IReadOnlyList<NamsMessageInput> messages,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
        string query,
        string? type,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists entities in the workspace with no query -- confirmed against the live NAMS REST API
    /// (<c>GET /v1/entities</c>) as part of Phase 9. Unlike <see cref="SearchEntitiesAsync"/> (which requires
    /// a non-empty query -- NAMS returns 400 for an empty one), this needs no precondition and no existing
    /// conversation, making it the one safe, side-effect-free read this client can use for a connectivity
    /// health probe.
    /// </summary>
    Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Searches a conversation's own message history (<c>POST /v1/conversations/{id}/search</c>) --
    /// confirmed live as part of the Phase 10 scenario-matrix expansion. Phase 2's original design dropped
    /// this (as <c>SearchMessagesAsync</c>) for lack of confirmation at the time; it's added now, standalone,
    /// deliberately NOT wired into <c>INamsRecallService.RecallAsync</c>'s already-shipped, tested Phase 4-6
    /// behavior -- that would be a real behavior change to the automatic recall pipeline, a separate decision
    /// from adding the client capability itself.
    /// </summary>
    Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
        string conversationId, string query, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a conversation (<c>DELETE /v1/conversations/{id}</c>) -- confirmed live as part of the Phase
    /// 10 data-lifecycle scenario. Idempotent: confirmed live that deleting an already-deleted conversation
    /// still returns success rather than a not-found error. After deletion, <see cref="GetContextAsync"/>
    /// keeps returning 200 with empty tiers (not 404) -- only fetching the conversation record itself, or
    /// adding messages to it (single or bulk), 404s. Deliberately not wired into
    /// <c>INamsPersistenceService</c>/<c>INamsConversationResolver</c> or any MCP tool -- data-lifecycle
    /// operations (deletion, export) are explicitly called out in the plan's own Phase 9 text as needing
    /// live testing and/or an organizational decision before being exposed as a routine capability; this is
    /// the low-level client operation only, added so a host that has already made that decision doesn't have
    /// to reach around this package to call the REST endpoint directly.
    /// </summary>
    Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists conversations in the workspace, newest first (<c>GET /v1/conversations</c>) -- confirmed live as
    /// part of the Phase 10e TCK Platinum probe. Returns <see cref="NamsConversationSummary"/>, a distinct shape
    /// from <see cref="CreateConversationAsync"/>'s response (no <c>workspaceId</c>; adds title/snippet/message
    /// count). Deliberately not wired into any higher-level service or MCP tool yet -- low-level client
    /// capability only, same tier as <see cref="SearchMessagesAsync"/>/<see cref="DeleteConversationAsync"/>.
    /// </summary>
    Task<IReadOnlyList<NamsConversationSummary>> ListConversationsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Lists compressed observations for a conversation (<c>GET /v1/conversations/{id}/observations</c>) --
    /// confirmed live as part of the Phase 10e TCK Platinum probe. Reuses <see cref="NamsObservation"/>, the same
    /// shape already returned inside <see cref="GetContextAsync"/>'s <c>Observations</c> tier. Observations are
    /// generated asynchronously by a server-side worker after sufficient messages accumulate -- one live probe
    /// saw a ~20-message window produce observations within ~100s, but a second attempt at the same margin
    /// (see the Phase 10f planning doc) did NOT reproduce within 150s, so this timing is NOT a reliable
    /// guarantee. Callers must not assume a call shortly after adding messages will see fresh observations, and
    /// should not build a bounded wait on any specific timing figure from this comment. Deliberately not wired
    /// into any higher-level service or MCP tool yet -- low-level client capability only.
    /// </summary>
    Task<IReadOnlyList<NamsObservation>> GetObservationsAsync(
        string conversationId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Scores/confirms an entity (<c>PUT /v1/entities/{id}/feedback</c>) -- confirmed live as part of the
    /// Phase 10e TCK Platinum probe. Both <paramref name="userScore"/> (0-1 confidence) and
    /// <paramref name="confirmed"/> (human-verified flag) are optional; pass <see langword="null"/> to leave
    /// either unset. Wired into the <c>nams_entity_feedback</c> MCP tool (PR #154).
    /// </summary>
    Task<NamsEntityFeedbackResult> SetEntityFeedbackAsync(
        string entityId, double? userScore, bool? confirmed, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the full workspace entity graph (<c>GET /v1/entities/graph</c>, no parameters) -- confirmed live as
    /// part of the Phase 10e TCK Platinum probe. Nodes reuse <see cref="NamsEntity"/> (confirmed identical
    /// shape); see <see cref="ExpandGraphAsync"/> for the genuinely different node shape that endpoint returns.
    /// Wired into the <c>nams_entity_graph</c> MCP tool (PR #154).
    /// </summary>
    Task<NamsEntityGraph> GetEntityGraphAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Expands a graph node's 1-hop neighborhood (<c>POST /v1/graph/expand</c>) -- confirmed live as part of
    /// the Phase 10e TCK Platinum probe. A POST verb but read-only (no server-side side effects per its own
    /// description), so treated as idempotent-for-retry like <see cref="SearchEntitiesAsync"/>. Wired into the
    /// <c>nams_expand_graph</c> MCP tool (PR #154) -- see that tool's own SECURITY comment for the Message-node
    /// content-elision mitigation it applies, since this method itself performs no gating.
    /// </summary>
    Task<NamsGraphExpansion> ExpandGraphAsync(
        string nodeId, IReadOnlyList<string> loadedIds, CancellationToken cancellationToken);

    /// <summary>
    /// Records a reasoning step (<c>POST /v1/reasoning/steps</c>) -- confirmed live as part of the Phase 10e TCK
    /// Platinum probe. Unlike every other addition in Phase 10e-10g, this is the first method touching an
    /// entirely new domain -- reasoning/provenance -- this client has never exposed before. A genuine write
    /// (resending would create a duplicate step), not idempotent-for-retry. Wired into the
    /// <c>nams_record_reasoning_step</c> MCP tool (PR #154).
    /// </summary>
    Task<NamsReasoningStep> RecordReasoningStepAsync(
        string conversationId, string reasoning, string actionTaken, string? result, CancellationToken cancellationToken);

    /// <summary>
    /// Lists reasoning steps for a conversation (<c>GET /v1/reasoning/steps?conversation_id=</c>) -- confirmed
    /// live as part of the Phase 10e TCK Platinum probe. Unlike Phase 10f's observations, recording and
    /// immediately listing a step is NOT subject to any async worker delay -- confirmed live to be immediately
    /// visible. Wired into the <c>nams_list_reasoning_steps</c> MCP tool (PR #154) -- that tool substitutes the
    /// caller's own known conversation id for each step's <see cref="NamsReasoningStep.ConversationId"/> rather
    /// than echoing this method's response verbatim, since NAMS's response omits it per-step.
    /// </summary>
    Task<IReadOnlyList<NamsReasoningStep>> ListReasoningStepsAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a tool call, optionally linked to a step (<c>POST /v1/reasoning/tool-calls</c>) -- confirmed
    /// live as part of the Phase 10e TCK Platinum probe. <paramref name="input"/>/<paramref name="output"/>
    /// must be pre-serialized JSON strings, not objects -- NAMS stores them as scalar string properties. A
    /// genuine write, not idempotent-for-retry. Wired into the <c>nams_record_tool_call</c> MCP tool (PR #154).
    /// </summary>
    Task<NamsToolCall> RecordToolCallAsync(
        string? stepId, string toolName, string input, string? output, string? status, int? durationMs,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the full reasoning trace for a conversation -- all steps and tool calls, in order
    /// (<c>GET /v1/reasoning/trace/{conversationId}</c>) -- confirmed live as part of the Phase 10e TCK
    /// Platinum probe. Flat shape (steps and toolCalls as parallel arrays), not steps-with-nested-toolCalls
    /// despite the endpoint's own prose description implying nesting. Wired into the <c>nams_reasoning_trace</c>
    /// MCP tool (PR #154) -- that tool substitutes the caller's own known conversation id for each step's
    /// <see cref="NamsReasoningStep.ConversationId"/> rather than echoing this method's response verbatim, for
    /// the same per-step-omission reason as <see cref="ListReasoningStepsAsync"/>.
    /// </summary>
    Task<NamsReasoningTrace> GetReasoningTraceAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the reasoning chain that influenced an entity's creation (<c>GET /v1/reasoning/provenance/{entityId}</c>)
    /// -- confirmed live as part of the Phase 10e TCK Platinum probe. Like Phase 10f's observations, this links
    /// to async entity-extraction machinery -- callers should not assume a call shortly after recording
    /// reasoning steps will see a populated provenance chain for any particular entity. Wired into the
    /// <c>nams_entity_provenance</c> MCP tool (PR #154).
    /// </summary>
    Task<NamsEntityProvenance> GetEntityProvenanceAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a caller-supplied Cypher query against the tenant's graph (<c>POST /v1/query</c>) --
    /// confirmed live as part of the Phase 10e TCK Platinum probe. NAMS enforces read-only server-side: a real
    /// write attempt (<c>CREATE</c>) was confirmed live to be rejected with HTTP 400, not merely documented as
    /// read-only. A POST verb, but read-only by that server-enforced contract -- treated as idempotent-for-retry
    /// like <see cref="ExpandGraphAsync"/>. This is the last TCK Platinum capability added deliberately behind
    /// explicit user approval (like Phase 12) rather than the general autonomous-execution authorization
    /// covering Phase 10e-10h, given it's a raw-Cypher-passthrough security/design decision. Deliberately not
    /// wired into any higher-level service or MCP tool -- exposing this to an agent/end-user is a separate,
    /// later decision from adding the client capability itself. <paramref name="parameters"/> values must be
    /// JSON-primitive-compatible (string/number/bool/null/nested list-or-map) -- <see cref="System.Text.Json"/>
    /// serializes anything else (e.g. a raw <see cref="DateTime"/>) using its own default conversion, which may
    /// not match the Cypher literal the query expects to bind against (e.g. a temporal property comparison);
    /// pass pre-formatted strings and cast explicitly in the query (<c>datetime($x)</c>) for those cases.
    /// </summary>
    Task<NamsQueryResult> ExecuteCypherQueryAsync(
        string cypher, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Manually creates an entity (<c>POST /v1/entities</c>) -- confirmed live (Phase 10j). Entities are
    /// normally created by the async extraction pipeline; this is the explicit-write counterpart, needed by
    /// the upstream TCK's own <c>test_set_entity_feedback_returns_updated</c> Platinum scenario (it creates an
    /// entity via <c>add_entity</c> before scoring it). Returns <see cref="NamsCreateEntityResult"/>, NOT
    /// <see cref="NamsEntity"/> -- confirmed live that NAMS's own fuzzy entity-resolution can return a
    /// genuinely different, minimal response shape (no name/type/description at all) when it auto-merges the
    /// submission into an existing entity rather than creating a new one; see that type's own doc comment for
    /// the full three-shape breakdown. A genuine write, not idempotent-for-retry. Wired into the
    /// <c>nams_create_entity</c> MCP tool (PR #154).
    /// </summary>
    Task<NamsCreateEntityResult> CreateEntityAsync(
        string name, string type, string? description, CancellationToken cancellationToken);
}
