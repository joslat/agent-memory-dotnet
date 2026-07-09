namespace AgentMemory.Abstractions.Schema;

/// <summary>
/// Canonical Neo4j schema constants matching the Python agent-memory reference implementation.
/// All relationship types and property names MUST match Python exactly for cross-implementation compatibility.
/// </summary>
public static class SchemaConstants
{
    /// <summary>Neo4j relationship type names used throughout the memory graph.</summary>
    public static class RelationshipTypes
    {
        // Short-term memory

        /// <summary>Conversation to message relationship.</summary>
        public const string HasMessage = "HAS_MESSAGE";

        /// <summary>Conversation to its first message relationship.</summary>
        public const string FirstMessage = "FIRST_MESSAGE";

        /// <summary>Message to following message relationship.</summary>
        public const string NextMessage = "NEXT_MESSAGE";

        // Long-term memory

        /// <summary>Memory node to the message it was extracted from.</summary>
        public const string ExtractedFrom = "EXTRACTED_FROM";

        /// <summary>Message or fact to mentioned entity relationship.</summary>
        public const string Mentions = "MENTIONS";

        /// <summary>Entity equivalence (deduplication) relationship.</summary>
        public const string SameAs = "SAME_AS";

        /// <summary>
        /// Supersession edge (D7): <c>(loser)-[:SUPERSEDED_BY]-&gt;(winner)</c> for Fact→Fact and
        /// Preference→Preference. Written when a contradiction is resolved or a duplicate is collapsed;
        /// the loser is soft-invalidated (kept, not deleted) and points to the winner. Mirrors the
        /// upstream <c>supersede_preference</c> <c>:SUPERSEDED_BY</c> link (non-destructive, both kept).
        /// </summary>
        public const string SupersededBy = "SUPERSEDED_BY";

        /// <summary>Generic entity-to-entity relationship.</summary>
        public const string RelatedTo = "RELATED_TO";

        /// <summary>Fact or preference to the entity it concerns.</summary>
        public const string About = "ABOUT";

        // Reasoning

        /// <summary>Reasoning trace to reasoning step relationship.</summary>
        public const string HasStep = "HAS_STEP";

        /// <summary>Reasoning step to tool call relationship.</summary>
        public const string UsesTool = "USES_TOOL";

        /// <summary>Tool call to its tool definition relationship.</summary>
        public const string InstanceOf = "INSTANCE_OF";

        /// <summary>
        /// Reasoning step to an entity it read or acted upon (audit/provenance edge; carries a
        /// <c>recorded_at</c> timestamp set on create). Matches the Python reference
        /// <c>(:ReasoningStep)-[:TOUCHED]->(:Entity)</c>.
        /// </summary>
        public const string Touched = "TOUCHED";

        // Cross-memory

        /// <summary>Conversation to reasoning trace relationship.</summary>
        public const string HasTrace = "HAS_TRACE";

        /// <summary>Reasoning trace to the message that initiated it.</summary>
        public const string InitiatedBy = "INITIATED_BY";

        /// <summary>Node to the event or message that triggered it.</summary>
        public const string TriggeredBy = "TRIGGERED_BY";

        // Provenance

        /// <summary>Memory node to the extractor that produced it.</summary>
        public const string ExtractedBy = "EXTRACTED_BY";

        // .NET extensions (kept for added value)

        /// <summary>Conversation or entity to fact relationship (.NET extension).</summary>
        public const string HasFact = "HAS_FACT";

        /// <summary>Conversation or entity to preference relationship (.NET extension).</summary>
        public const string HasPreference = "HAS_PREFERENCE";

        /// <summary>Node to its containing session relationship (.NET extension).</summary>
        public const string InSession = "IN_SESSION";
    }

    /// <summary>Neo4j node label names used throughout the memory graph.</summary>
    public static class NodeLabels
    {
        /// <summary>Cypher label for Conversation nodes.</summary>
        public const string Conversation = "Conversation";

        /// <summary>Cypher label for Message nodes.</summary>
        public const string Message = "Message";

        /// <summary>Cypher label for Entity nodes.</summary>
        public const string Entity = "Entity";

        /// <summary>Cypher label for Fact nodes.</summary>
        public const string Fact = "Fact";

        /// <summary>Cypher label for Preference nodes.</summary>
        public const string Preference = "Preference";

        /// <summary>Cypher label for ReasoningTrace nodes.</summary>
        public const string ReasoningTrace = "ReasoningTrace";

        /// <summary>Cypher label for ReasoningStep nodes.</summary>
        public const string ReasoningStep = "ReasoningStep";

        /// <summary>Cypher label for ToolCall nodes.</summary>
        public const string ToolCall = "ToolCall";

        /// <summary>Cypher label for Tool nodes.</summary>
        public const string Tool = "Tool";

        /// <summary>Cypher label for Extractor nodes.</summary>
        public const string Extractor = "Extractor";

        /// <summary>Cypher label for ConsolidationRun audit nodes (memory hygiene, PR #113).</summary>
        public const string ConsolidationRun = "ConsolidationRun";

        /// <summary>Cypher label for read-audit nodes recording long-term memory recall hits.</summary>
        public const string MemoryReadAudit = "MemoryReadAudit";

        /// <summary>Cypher label for Schema nodes.</summary>
        public const string Schema = "Schema";
    }

    /// <summary>
    /// Neo4j property names in snake_case to match Python reference.
    /// C# domain models use PascalCase; these are for Cypher queries only.
    /// </summary>
    public static class Properties
    {
        // Common

        /// <summary>Unique identifier property.</summary>
        public const string Id = "id";

        /// <summary>Display name property.</summary>
        public const string Name = "name";

        /// <summary>Type discriminator property.</summary>
        public const string Type = "type";

        /// <summary>Free-text description property.</summary>
        public const string Description = "description";

        /// <summary>Vector embedding property.</summary>
        public const string Embedding = "embedding";

        /// <summary>Confidence score property.</summary>
        public const string Confidence = "confidence";

        /// <summary>Arbitrary metadata property.</summary>
        public const string Metadata = "metadata";

        // Timestamps — use snake_case, stored as datetime()

        /// <summary>Creation timestamp property.</summary>
        public const string CreatedAt = "created_at";

        /// <summary>Last-update timestamp property.</summary>
        public const string UpdatedAt = "updated_at";

        /// <summary>Generic timestamp property.</summary>
        public const string Timestamp = "timestamp";

        /// <summary>Last access timestamp used by read audit and recency/frequency ranking.</summary>
        public const string LastAccessedAt = "last_accessed_at";

        /// <summary>Recall/access count used by read audit and recency/frequency ranking.</summary>
        public const string AccessCount = "access_count";

        /// <summary>Generic kind/type discriminator used by audit nodes.</summary>
        public const string Kind = "kind";

        /// <summary>Identifier of the memory node a read-audit record refers to.</summary>
        public const string MemoryId = "memory_id";

        /// <summary>Timestamp when a memory read was audited.</summary>
        public const string ReadAt = "read_at";

        // Conversation

        /// <summary>Session identifier property.</summary>
        public const string SessionId = "session_id";

        /// <summary>Conversation title property.</summary>
        public const string Title = "title";

        // Message

        /// <summary>Message role property.</summary>
        public const string Role = "role";

        /// <summary>Message content property.</summary>
        public const string Content = "content";

        /// <summary>Owning conversation identifier property.</summary>
        public const string ConversationId = "conversation_id";

        /// <summary>Associated tool call identifiers property.</summary>
        public const string ToolCallIds = "tool_call_ids";

        // Entity

        /// <summary>Entity subtype property.</summary>
        public const string Subtype = "subtype";

        /// <summary>Canonical (normalized) name property.</summary>
        public const string CanonicalName = "canonical_name";

        /// <summary>Entity aliases property.</summary>
        public const string Aliases = "aliases";

        /// <summary>Entity location property.</summary>
        public const string Location = "location";

        /// <summary>Identifier of the entity this one was merged into.</summary>
        public const string MergedInto = "merged_into";

        /// <summary>Timestamp of the merge operation.</summary>
        public const string MergedAt = "merged_at";

        /// <summary>Identifiers of source messages property.</summary>
        public const string SourceMessageIds = "source_message_ids";

        // Fact

        /// <summary>Fact subject property.</summary>
        public const string Subject = "subject";

        /// <summary>Fact predicate property.</summary>
        public const string Predicate = "predicate";

        /// <summary>Fact object property.</summary>
        public const string Object = "object";

        /// <summary>Validity start timestamp property.</summary>
        public const string ValidFrom = "valid_from";

        /// <summary>Validity end timestamp property (valid-time axis — when a fact stopped being true).</summary>
        public const string ValidUntil = "valid_until";

        /// <summary>
        /// Transaction-time axis (D5): the moment the system stopped believing a record — set by
        /// soft-invalidation / non-destructive decay / supersession. <c>null</c> = currently believed.
        /// Live recall excludes nodes with a non-null value; as-of recall includes them when
        /// <c>asOf &lt; invalidated_at</c> (i.e. they were still believed at that past time).
        /// </summary>
        public const string InvalidatedAt = "invalidated_at";

        /// <summary>Category classification property.</summary>
        public const string Category = "category";

        /// <summary>Owner/user scope property (null = shared/global). See MemoryScope.</summary>
        public const string OwnerId = "owner_id";

        /// <summary>Non-null owner merge key (coalesce(owner_id, '*')) used to keep shared vs owned
        /// records distinct in MERGE patterns where a null property would otherwise collapse them.</summary>
        public const string OwnerKey = "owner_key";

        // Preference

        /// <summary>Preference value property.</summary>
        public const string Preference = "preference";

        // ReasoningTrace

        /// <summary>Task description property.</summary>
        public const string Task = "task";

        /// <summary>Task embedding property.</summary>
        public const string TaskEmbedding = "task_embedding";

        /// <summary>Reasoning outcome property.</summary>
        public const string Outcome = "outcome";

        /// <summary>Reasoning result property.</summary>
        public const string Result = "result";

        /// <summary>Success flag property.</summary>
        public const string Success = "success";

        /// <summary>Trace start timestamp property.</summary>
        public const string StartedAt = "started_at";

        /// <summary>Trace completion timestamp property.</summary>
        public const string CompletedAt = "completed_at";

        // ReasoningStep

        /// <summary>Step ordering number property.</summary>
        public const string StepNumber = "step_number";

        /// <summary>Step action property.</summary>
        public const string Action = "action";

        /// <summary>Step observation property.</summary>
        public const string Observation = "observation";

        /// <summary>Step thought property.</summary>
        public const string Thought = "thought";

        /// <summary>Owning trace identifier property.</summary>
        public const string TraceId = "trace_id";

        // ToolCall

        /// <summary>Invoked tool name property.</summary>
        public const string ToolName = "tool_name";

        /// <summary>Tool call arguments property.</summary>
        public const string Arguments = "arguments";

        /// <summary>Tool call status property.</summary>
        public const string Status = "status";

        /// <summary>Tool call error property.</summary>
        public const string Error = "error";

        /// <summary>Tool call duration in milliseconds property.</summary>
        public const string DurationMs = "duration_ms";

        /// <summary>Owning reasoning step identifier property.</summary>
        public const string StepId = "step_id";

        // Tool

        /// <summary>Total invocation count property.</summary>
        public const string TotalCalls = "total_calls";

        /// <summary>Successful invocation count property.</summary>
        public const string SuccessfulCalls = "successful_calls";

        /// <summary>Failed invocation count property.</summary>
        public const string FailedCalls = "failed_calls";

        /// <summary>Aggregate invocation duration in milliseconds property.</summary>
        public const string TotalDurationMs = "total_duration_ms";

        /// <summary>Last-used timestamp property.</summary>
        public const string LastUsedAt = "last_used_at";

        // Relationship properties

        /// <summary>Relationship type discriminator property.</summary>
        public const string RelationType = "relation_type";

        /// <summary>Source entity identifier property.</summary>
        public const string SourceEntityId = "source_entity_id";

        /// <summary>Target entity identifier property.</summary>
        public const string TargetEntityId = "target_entity_id";

        /// <summary>Match type property.</summary>
        public const string MatchType = "match_type";

        /// <summary>Ordering index property.</summary>
        public const string Order = "order";

        /// <summary>Mention start position property.</summary>
        public const string StartPos = "start_pos";

        /// <summary>Mention end position property.</summary>
        public const string EndPos = "end_pos";

        /// <summary>Surrounding context property.</summary>
        public const string Context = "context";
    }

    /// <summary>Allowed status values for tool call nodes.</summary>
    public static class ToolCallStatusValues
    {
        /// <summary>Tool call is queued but not yet executed.</summary>
        public const string Pending = "pending";

        /// <summary>Tool call completed successfully.</summary>
        public const string Success = "success";

        /// <summary>Tool call failed.</summary>
        public const string Failure = "failure";

        /// <summary>Tool call raised an error.</summary>
        public const string Error = "error";

        /// <summary>Tool call exceeded its time limit.</summary>
        public const string Timeout = "timeout";

        /// <summary>Tool call was cancelled.</summary>
        public const string Cancelled = "cancelled";
    }
}
