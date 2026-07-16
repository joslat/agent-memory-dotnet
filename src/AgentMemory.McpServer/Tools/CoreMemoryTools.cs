using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// Core memory tools: search, context, store, add entity/preference/fact.
/// </summary>
[McpServerToolType]
internal sealed class CoreMemoryTools
{
    [McpServerTool(Name = "memory_search"), Description("Search agent memory for relevant context matching a query. Returns assembled memory context with recent messages, entities, facts, and preferences.")]
    public static async Task<string> MemorySearch(
        IMemoryService memoryService,
        IOptions<AgentMemoryMcpOptions> options,
        [Description("The search query text")] string query,
        [Description("Session identifier (optional, uses default if omitted)")] string? sessionId = null,
        [Description("User identifier (optional)")] string? userId = null,
        [Description("Maximum number of results per memory section")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        var request = new RecallRequest
        {
            SessionId = sessionId ?? options.Value.DefaultSessionId,
            UserId = userId,
            Query = query
        };

        var result = await memoryService.RecallAsync(request, cancellationToken).ConfigureAwait(false);
        return ToolJsonContext.Serialize(new
        {
            result.TotalItemsRetrieved,
            result.Truncated,
            result.EstimatedTokenCount,
            context = new
            {
                result.Context.SessionId,
                result.Context.AssembledAtUtc,
                recentMessages = result.Context.RecentMessages.Items,
                relevantMessages = result.Context.RelevantMessages.Items,
                relevantEntities = result.Context.RelevantEntities.Items,
                relevantPreferences = result.Context.RelevantPreferences.Items,
                relevantFacts = result.Context.RelevantFacts.Items,
                similarTraces = result.Context.SimilarTraces.Items,
                result.Context.GraphRagContext
            }
        });
    }

    [McpServerTool(Name = "memory_get_context"), Description("Get assembled memory context for the current conversation. Similar to memory_search but returns the full structured context object.")]
    public static async Task<string> MemoryGetContext(
        IMemoryService memoryService,
        IOptions<AgentMemoryMcpOptions> options,
        [Description("The current query or topic to recall context for")] string query,
        [Description("Session identifier (optional, uses default if omitted)")] string? sessionId = null,
        [Description("User identifier (optional)")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var request = new RecallRequest
        {
            SessionId = sessionId ?? options.Value.DefaultSessionId,
            UserId = userId,
            Query = query
        };

        var result = await memoryService.RecallAsync(request, cancellationToken).ConfigureAwait(false);
        return ToolJsonContext.Serialize(result);
    }

    [McpServerTool(Name = "memory_store_message"), Description("Store a message in short-term conversation memory.")]
    public static async Task<string> MemoryStoreMessage(
        IMemoryService memoryService,
        IOptions<AgentMemoryMcpOptions> options,
        [Description("The role of the message sender (e.g., 'user', 'assistant', 'system')")] string role,
        [Description("The message content")] string content,
        [Description("Session identifier (optional, uses default if omitted)")] string? sessionId = null,
        [Description("Conversation identifier (optional, defaults to session ID)")] string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        var sid = sessionId ?? options.Value.DefaultSessionId;
        var cid = conversationId ?? sid;

        // #92 Phase 7: this tool accepts an unvalidated, caller-supplied role -- including "system"/"tool",
        // which most IChatClient implementations give elevated or special handling. Stamping ToolDerived
        // (the same level memory_add_fact stamps, #92 Phase 3) doesn't restrict what role can be set, but
        // ensures a privileged role can never resurface with full authority on recall unless a host
        // explicitly configures MinimumTrustForSystemRole low enough to admit it (see
        // RecalledMessageRoleGate, consulted by MafTypeMapper/MemoryContextFormatter/Neo4jTextSearch).
        var metadata = MemoryTrustMetadataExtensions.CreateWithTrustLevel(MemoryTrustLevel.ToolDerived);
        var message = await memoryService.AddMessageAsync(sid, cid, role, content, metadata, cancellationToken).ConfigureAwait(false);
        return ToolJsonContext.Serialize(new
        {
            message.MessageId,
            message.ConversationId,
            message.SessionId,
            message.Role,
            message.Content,
            message.TimestampUtc
        });
    }

    [McpServerTool(Name = "memory_add_entity"), Description("Add an entity (person, organization, location, event, or object) to long-term memory.")]
    public static async Task<string> MemoryAddEntity(
        ILongTermMemoryService longTermMemory,
        IIdGenerator idGenerator,
        IClock clock,
        IOptions<AgentMemoryMcpOptions> options,
        IOptions<LongTermMemoryOptions> longTermOptions,
        [Description("Name of the entity")] string name,
        [Description("Type of entity: Person, Organization, Location, Event, or Object")] string type,
        [Description("Description of the entity (optional)")] string? description = null,
        [Description("Confidence score from 0.0 to 1.0 (optional, defaults to configured value)")] double? confidence = null,
        [Description("Owner/user identifier (optional). Null = shared/global knowledge visible to everyone.")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var confidenceValue = confidence ?? options.Value.DefaultConfidence;
        var entity = new Entity
        {
            EntityId = idGenerator.GenerateId(),
            Name = name,
            Type = type,
            Description = description,
            Confidence = confidenceValue,
            OwnerId = userId,
            CreatedAtUtc = clock.UtcNow
        };

        var result = await longTermMemory.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
        var (persisted, reason) = PersistenceOutcome(confidenceValue, longTermOptions.Value.MinConfidenceThreshold);
        return ToolJsonContext.Serialize(new
        {
            result.EntityId,
            result.Name,
            result.Type,
            result.Description,
            result.Confidence,
            result.CreatedAtUtc,
            persisted,
            reason
        });
    }

    [McpServerTool(Name = "memory_add_preference"), Description("Store a user preference in long-term memory.")]
    public static async Task<string> MemoryAddPreference(
        ILongTermMemoryService longTermMemory,
        IIdGenerator idGenerator,
        IClock clock,
        IOptions<AgentMemoryMcpOptions> options,
        IOptions<LongTermMemoryOptions> longTermOptions,
        [Description("Category of the preference (e.g., 'communication', 'style', 'tooling')")] string category,
        [Description("The preference text describing what the user prefers")] string preferenceText,
        [Description("Context in which the preference applies (optional)")] string? context = null,
        [Description("Confidence score from 0.0 to 1.0 (optional)")] double? confidence = null,
        [Description("Owner/user identifier (optional). Null = shared/global knowledge visible to everyone.")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var confidenceValue = confidence ?? options.Value.DefaultConfidence;
        var preference = new Preference
        {
            PreferenceId = idGenerator.GenerateId(),
            Category = category,
            PreferenceText = preferenceText,
            Context = context,
            Confidence = confidenceValue,
            OwnerId = userId,
            CreatedAtUtc = clock.UtcNow
        };

        var result = await longTermMemory.AddPreferenceAsync(preference, cancellationToken).ConfigureAwait(false);
        var (persisted, reason) = PersistenceOutcome(confidenceValue, longTermOptions.Value.MinConfidenceThreshold);
        return ToolJsonContext.Serialize(new
        {
            result.PreferenceId,
            result.Category,
            result.PreferenceText,
            result.Context,
            result.Confidence,
            result.CreatedAtUtc,
            persisted,
            reason
        });
    }

    [McpServerTool(Name = "memory_add_fact"), Description("Store a factual statement (subject-predicate-object triple) in long-term memory.")]
    public static async Task<string> MemoryAddFact(
        ILongTermMemoryService longTermMemory,
        IIdGenerator idGenerator,
        IClock clock,
        IOptions<AgentMemoryMcpOptions> options,
        IOptions<LongTermMemoryOptions> longTermOptions,
        [Description("Subject of the fact (e.g., a person or concept name)")] string subject,
        [Description("Predicate or relationship (e.g., 'works_at', 'lives_in', 'knows')")] string predicate,
        [Description("Object or value of the fact (e.g., 'Microsoft', 'Seattle')")] string factObject,
        [Description("Confidence score from 0.0 to 1.0 (optional)")] double? confidence = null,
        [Description("Owner/user identifier (optional). Null = shared/global knowledge visible to everyone.")] string? userId = null,
        [Description("Category for grouping the fact (e.g., 'personal', 'professional') (optional)")] string? category = null,
        [Description("Additional metadata as a JSON object string, e.g. {\"source\":\"crm\"} (optional)")] string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        var confidenceValue = confidence ?? options.Value.DefaultConfidence;
        // #92 Phase 3: trust_level is a framework-reserved metadata key -- strip any caller-supplied value
        // (a caller must never be able to self-assign ApplicationTrusted and bypass the admission policy's
        // instruction-like-content detection) and stamp ToolDerived, since this fact arrived via a direct
        // tool call rather than the extraction pipeline.
        var callerMetadata = (ParseMetadata(metadataJson) ?? new Dictionary<string, object>())
            .WithoutCallerSuppliedTrustLevel();
        var fact = new Fact
        {
            FactId = idGenerator.GenerateId(),
            Subject = subject,
            Predicate = predicate,
            Object = factObject,
            Category = category,
            Confidence = confidenceValue,
            OwnerId = userId,
            CreatedAtUtc = clock.UtcNow,
            Metadata = callerMetadata.WithTrustLevel(MemoryTrustLevel.ToolDerived)
        };

        var result = await longTermMemory.AddFactAsync(fact, cancellationToken).ConfigureAwait(false);
        var (persisted, reason) = PersistenceOutcome(confidenceValue, longTermOptions.Value.MinConfidenceThreshold);
        return ToolJsonContext.Serialize(new
        {
            result.FactId,
            result.Subject,
            result.Predicate,
            result.Object,
            result.Category,
            result.Confidence,
            result.CreatedAtUtc,
            result.Metadata,
            persisted,
            reason
        });
    }

    // The Add* gate (LongTermMemoryOptions.MinConfidenceThreshold) skips persistence for a sub-threshold item
    // and returns it un-stored. Surface that to the MCP caller so a low-confidence add is not reported as a
    // false success. Uses the effective confidence the tool stamped (not result.Confidence, which the dedup-
    // reinforce path can raise); mirrors the service's strict-&lt; skip, so persisted == (confidence >= threshold).
    private static (bool Persisted, string? Reason) PersistenceOutcome(double confidence, double threshold) =>
        confidence >= threshold
            ? (true, (string?)null)
            // Format the doubles with InvariantCulture: this string lands in a JSON tool response, so a
            // comma-decimal locale must not change "0.3" to "0,3" (R6 cleanup).
            : (false, string.Create(CultureInfo.InvariantCulture,
                      $"Not stored: confidence {confidence:0.###} is below the configured minimum {threshold:0.###} " +
                      $"(LongTermMemoryOptions.MinConfidenceThreshold). Increase confidence or lower the threshold."));

    // Parses an optional JSON-object string into a metadata dictionary. Returns null when blank;
    // throws an actionable ArgumentException (surfaced to the MCP caller) when the JSON is invalid.
    private static IReadOnlyDictionary<string, object>? ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"metadataJson must be a valid JSON object: {ex.Message}", nameof(metadataJson));
        }
    }
}
