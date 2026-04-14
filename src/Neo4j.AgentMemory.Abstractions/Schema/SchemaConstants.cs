#pragma warning disable CS1591 // XML comment warnings suppressed for schema constants
namespace Neo4j.AgentMemory.Abstractions.Schema;

/// <summary>
/// Canonical Neo4j schema constants matching the Python agent-memory reference implementation.
/// All relationship types and property names MUST match Python exactly for cross-implementation compatibility.
/// </summary>
public static class SchemaConstants
{
    public static class RelationshipTypes
    {
        // Short-term memory
        public const string HasMessage = "HAS_MESSAGE";
        public const string FirstMessage = "FIRST_MESSAGE";
        public const string NextMessage = "NEXT_MESSAGE";

        // Long-term memory
        public const string ExtractedFrom = "EXTRACTED_FROM";
        public const string Mentions = "MENTIONS";
        public const string SameAs = "SAME_AS";
        public const string RelatedTo = "RELATED_TO";
        public const string About = "ABOUT";

        // Reasoning
        public const string HasStep = "HAS_STEP";
        public const string UsesTool = "USES_TOOL";
        public const string InstanceOf = "INSTANCE_OF";

        // Cross-memory
        public const string HasTrace = "HAS_TRACE";
        public const string InitiatedBy = "INITIATED_BY";
        public const string TriggeredBy = "TRIGGERED_BY";

        // Provenance
        public const string ExtractedBy = "EXTRACTED_BY";

        // .NET extensions (kept for added value)
        public const string HasFact = "HAS_FACT";
        public const string HasPreference = "HAS_PREFERENCE";
        public const string InSession = "IN_SESSION";
    }

    public static class NodeLabels
    {
        public const string Conversation = "Conversation";
        public const string Message = "Message";
        public const string Entity = "Entity";
        public const string Fact = "Fact";
        public const string Preference = "Preference";
        public const string ReasoningTrace = "ReasoningTrace";
        public const string ReasoningStep = "ReasoningStep";
        public const string ToolCall = "ToolCall";
        public const string Tool = "Tool";
        public const string Extractor = "Extractor";
        public const string Schema = "Schema";
    }

    /// <summary>
    /// Neo4j property names in snake_case to match Python reference.
    /// C# domain models use PascalCase; these are for Cypher queries only.
    /// </summary>
    public static class Properties
    {
        // Common
        public const string Id = "id";
        public const string Name = "name";
        public const string Type = "type";
        public const string Description = "description";
        public const string Embedding = "embedding";
        public const string Confidence = "confidence";
        public const string Metadata = "metadata";

        // Timestamps — use snake_case, stored as datetime()
        public const string CreatedAt = "created_at";
        public const string UpdatedAt = "updated_at";
        public const string Timestamp = "timestamp";

        // Conversation
        public const string SessionId = "session_id";
        public const string Title = "title";

        // Message
        public const string Role = "role";
        public const string Content = "content";
        public const string ConversationId = "conversation_id";
        public const string ToolCallIds = "tool_call_ids";

        // Entity
        public const string Subtype = "subtype";
        public const string CanonicalName = "canonical_name";
        public const string Aliases = "aliases";
        public const string Location = "location";
        public const string MergedInto = "merged_into";
        public const string MergedAt = "merged_at";
        public const string SourceMessageIds = "source_message_ids";

        // Fact
        public const string Subject = "subject";
        public const string Predicate = "predicate";
        public const string Object = "object";
        public const string ValidFrom = "valid_from";
        public const string ValidUntil = "valid_until";
        public const string Category = "category";

        // Preference
        public const string Preference = "preference";

        // ReasoningTrace
        public const string Task = "task";
        public const string TaskEmbedding = "task_embedding";
        public const string Outcome = "outcome";
        public const string Result = "result";
        public const string Success = "success";
        public const string StartedAt = "started_at";
        public const string CompletedAt = "completed_at";

        // ReasoningStep
        public const string StepNumber = "step_number";
        public const string Action = "action";
        public const string Observation = "observation";
        public const string Thought = "thought";
        public const string TraceId = "trace_id";

        // ToolCall
        public const string ToolName = "tool_name";
        public const string Arguments = "arguments";
        public const string Status = "status";
        public const string Error = "error";
        public const string DurationMs = "duration_ms";
        public const string StepId = "step_id";

        // Tool
        public const string TotalCalls = "total_calls";
        public const string SuccessfulCalls = "successful_calls";
        public const string FailedCalls = "failed_calls";
        public const string TotalDurationMs = "total_duration_ms";
        public const string LastUsedAt = "last_used_at";

        // Relationship properties
        public const string RelationType = "relation_type";
        public const string SourceEntityId = "source_entity_id";
        public const string TargetEntityId = "target_entity_id";
        public const string MatchType = "match_type";
        public const string Order = "order";
        public const string StartPos = "start_pos";
        public const string EndPos = "end_pos";
        public const string Context = "context";
    }

    public static class ToolCallStatusValues
    {
        public const string Pending = "pending";
        public const string Success = "success";
        public const string Failure = "failure";
        public const string Error = "error";
        public const string Timeout = "timeout";
        public const string Cancelled = "cancelled";
    }
}
