using System.Diagnostics;

namespace AgentMemory.Abstractions.Diagnostics;

/// <summary>
/// One place for AgentMemory's span names and attribute keys, so spans, dashboards and docs derive them
/// from the same source instead of retyping strings that then drift apart.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trace a turn produces</b> (each level nests under the one above it):
/// <c>memory.hook.recall</c> (the PRE hook) → <c>memory.route</c> (the recall policy's decision) →
/// <c>memory.recall.total</c> → <c>memory.recall.{section}</c> (one per memory type, tagged
/// <see cref="MemoryType"/> and <see cref="ResultsCount"/>) → <c>memory.db.tx</c> → <c>memory.db.query</c>;
/// and <c>memory.hook.ingest</c> (the POST hook) → <c>memory.store.*</c> / <c>memory.extract.*</c> /
/// <c>memory.persist.total</c>. Embedding requests are <c>memory.embed</c>.
/// </para>
/// <para>
/// <b>Content is never an attribute.</b> Messages, memories, prompts and Cypher text do not appear in any
/// span; neither do owner or application ids (only whether the turn was scoped to one).
/// </para>
/// </remarks>
public static class MemoryTelemetry
{
    // ---- spans ----------------------------------------------------------------------------------

    /// <summary>The PRE hook of an agent turn: everything recall does before the model runs.</summary>
    public const string RecallHookSpan = "memory.hook.recall";

    /// <summary>The POST hook of an agent turn: storing the turn and learning from it.</summary>
    public const string IngestHookSpan = "memory.hook.ingest";

    /// <summary>The recall policy's decision: whether to recall, and which memory types.</summary>
    public const string RouteSpan = "memory.route";

    /// <summary>Composing recalled memory into the model's context: admission, trust, dedup, formatting.</summary>
    public const string ComposeSpan = "memory.compose";

    /// <summary>One embedding request (one or more inputs).</summary>
    public const string EmbedSpan = "memory.embed";

    // ---- identity -------------------------------------------------------------------------------

    /// <summary>Session id (also read from the W3C baggage item of the same name when a host sets it).</summary>
    public const string SessionId = "memory.session.id";

    /// <summary>Conversation id of the turn.</summary>
    public const string ConversationId = "memory.conversation.id";
    /// <summary>
    /// Whether the turn was scoped to an owner. A boolean on purpose, matching the Observability package's
    /// default: an owner or tenant id in a trace is tenant data, and an unsalted hash of a guessable id
    /// (an email, a number) is only a pseudonym.
    /// </summary>
    public const string OwnerScoped = "memory.owner_scoped";

    /// <summary>Whether the turn was routed to a named application store (the id itself is often a tenant id).</summary>
    public const string AppScoped = "memory.app_scoped";

    // ---- recall ---------------------------------------------------------------------------------

    /// <summary>The memory type a span works on: working, episodic, semantic, entity, preference, reasoning, …</summary>
    public const string MemoryType = "memory.type";

    /// <summary>How many items a recall leg returned.</summary>
    public const string ResultsCount = "memory.results.count";

    /// <summary>The recall policy type that decided.</summary>
    public const string RoutePolicy = "memory.route.policy";
    /// <summary>Whether the policy decided to recall at all.</summary>
    public const string RouteShouldRecall = "memory.route.should_recall";
    /// <summary>The memory categories the policy selected.</summary>
    public const string RouteCategories = "memory.route.categories";
    /// <summary>The recall intent the policy inferred, if any.</summary>
    public const string RouteIntent = "memory.route.intent";
    /// <summary>The point in time a temporal query ("what did I think last March?") resolved to.</summary>
    public const string RouteTemporalAsOf = "memory.route.temporal.as_of";

    /// <summary>Whether the fan-out planner split the query into per-memory-type legs.</summary>
    public const string RouteFanOutFired = "memory.route.fanout.fired";

    /// <summary>Which fan-out rules fired (comma-separated).</summary>
    public const string RouteFanOutRules = "memory.route.fanout.rules";

    /// <summary>How many sub-query legs the fan-out planner produced.</summary>
    public const string RouteFanOutLegs = "memory.route.fanout.legs";

    /// <summary>Recalled items flagged as instruction-like but still included (permissive mode).</summary>
    public const string ComposeFlagged = "memory.compose.flagged";

    /// <summary>Recalled items the admission policy excluded from the context.</summary>
    public const string ComposeExcluded = "memory.compose.excluded";

    /// <summary>Recalled messages dropped because the live thread already carries them.</summary>
    public const string ComposeDeduplicated = "memory.compose.deduplicated";

    /// <summary>How many context messages recall handed to the model.</summary>
    public const string ContextItems = "memory.context.items";
    /// <summary>How many messages the POST hook stored or extracted from.</summary>
    public const string IngestMessages = "memory.ingest.messages";

    // ---- embeddings -----------------------------------------------------------------------------

    /// <summary>Inputs in one embedding request (before the cache).</summary>
    public const string EmbedInputs = "memory.embed.inputs";
    /// <summary>Inputs served from the embedding cache.</summary>
    public const string EmbedCacheHits = "memory.embed.cache_hits";
    /// <summary>Inputs actually sent to the embedding provider.</summary>
    public const string EmbedSent = "memory.embed.sent";

    // ---- errors ---------------------------------------------------------------------------------

    /// <summary>OpenTelemetry error type (the exception type name).</summary>
    public const string ErrorType = "error.type";

    /// <summary>
    /// Marks <paramref name="activity"/> failed with <paramref name="exception"/>: the OpenTelemetry
    /// exception event, <c>error.type</c>, and an error status whose description is the exception type
    /// (never its message, which can carry content).
    /// </summary>
    internal static void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null) return;
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = type,
        }));
        activity.SetTag(ErrorType, type);
        activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
    }

    /// <summary>The memory type a recall section span (<c>memory.recall.{section}</c>) works on, or null.</summary>
    internal static string? MemoryTypeOfSpan(string spanName)
    {
        const string prefix = "memory.recall.";
        if (!spanName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var section = spanName[prefix.Length..];
        var asOf = section.EndsWith("_as_of", StringComparison.Ordinal);
        if (asOf) section = section[..^"_as_of".Length];
        if (section.EndsWith("_vector", StringComparison.Ordinal)) section = section[..^"_vector".Length];
        if (asOf) return "bitemporal";
        return section switch
        {
            "recent" => "working",
            "messages" or "message" => "episodic",
            "entities" or "entity" or "entity_similar" => "entity",
            "graphrag" => "entity",
            "facts" or "fact" => "semantic",
            "preferences" or "preference" => "preference",
            "traces" or "trace" => "reasoning",
            "due" => "prospective",
            _ => null,
        };
    }

    /// <summary>
    /// Starts a recall section span tagged with the memory type its name implies, so every recall span
    /// (the assembler's legs and the repositories' vector and point-in-time searches) carries it.
    /// </summary>
    internal static Activity? StartRecallSpan(string spanName)
    {
        var activity = AgentMemoryDiagnostics.Source.StartActivity(spanName);
        if (activity is not null && MemoryTypeOfSpan(spanName) is { } memoryType)
            activity.SetTag(MemoryType, memoryType);
        return activity;
    }

    /// <summary>The session id from the current W3C baggage, when a host propagates one.</summary>
    internal static string? BaggageSessionId() => Activity.Current?.GetBaggageItem(SessionId);
}
