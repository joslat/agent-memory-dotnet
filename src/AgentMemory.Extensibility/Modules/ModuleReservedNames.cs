using System.Collections.Frozen;

namespace AgentMemory.Extensibility.Modules;

// Pinned against core constants and built-in declarations by the SDK tests; no storage dependency.
internal static class ModuleReservedNames
{
    internal static readonly FrozenSet<string> Labels = new[]
    {
        "Conversation", "Message", "Entity", "Fact", "Preference", "ReasoningTrace", "ReasoningStep",
        "ToolCall", "Tool", "Extractor", "ConsolidationRun", "MemoryReadAudit", "Schema", "User",
        "Migration", "ModuleMigration", "ModuleKey"
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> Relationships = new[]
    {
        "HAS_MESSAGE", "FIRST_MESSAGE", "NEXT_MESSAGE", "EXTRACTED_FROM", "MENTIONS", "SAME_AS",
        "SUPERSEDED_BY", "RELATED_TO", "ABOUT", "HAS_STEP", "USES_TOOL", "INSTANCE_OF", "TOUCHED",
        "HAS_TRACE", "INITIATED_BY", "TRIGGERED_BY", "EXTRACTED_BY", "HAS_FACT", "HAS_PREFERENCE",
        "IN_SESSION", "DERIVED_FROM"
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> Properties = new[]
    {
        "id", "key", "key_id", "owner_id", "created_at", "invalidated_at", "superseded_by",
        "source_session_id", "source_message_ids", "source_excerpt"
    }.ToFrozenSet(StringComparer.Ordinal);

    // Base/bootstrap, all built-in migration objects, upstream snapshot and module bookkeeping.
    internal static readonly FrozenSet<string> SchemaObjects = new[]
    {
        "consolidation_run_id",
        "consolidation_run_kind_idx",
        "conversation_archived_idx",
        "conversation_id",
        "conversation_session_idx",
        "entity_canonical_idx",
        "entity_created_at_idx",
        "entity_embedding_idx",
        "entity_embedding_owner_idx",
        "entity_id",
        "entity_location_idx",
        "entity_name",
        "entity_name_idx",
        "entity_owner_idx",
        "entity_owner_key_idx",
        "entity_type_idx",
        "extractor_name",
        "fact_category",
        "fact_content",
        "fact_created_at_idx",
        "fact_derivation_key_idx",
        "fact_embedding_idx",
        "fact_embedding_owner_idx",
        "fact_id",
        "fact_invalidated_at_idx",
        "fact_kind_idx",
        "fact_merge_key_idx",
        "fact_owner_idx",
        "fact_owner_key_idx",
        "fact_predicate_key_idx",
        "fact_valid_from_idx",
        "fact_valid_until_idx",
        "memory_read_audit_id",
        "memory_read_audit_kind_idx",
        "memory_read_audit_memory_id_idx",
        "message_content",
        "message_embedding_idx",
        "message_extraction_deferred_idx",
        "message_id",
        "message_role_idx",
        "message_session_idx",
        "message_session_timestamp_idx",
        "message_timestamp_idx",
        "migration_version",
        "module_key_id",
        "module_migration_key",
        "preference_category_idx",
        "preference_created_at_idx",
        "preference_embedding_idx",
        "preference_id",
        "preference_invalidated_at_idx",
        "preference_owner_idx",
        "reasoning_step_embedding_idx",
        "reasoning_step_id",
        "reasoning_step_timestamp",
        "reasoning_trace_id",
        "rel_owner_idx",
        "schema_id_idx",
        "schema_name_idx",
        "schema_version_idx",
        "step_embedding_idx",
        "task_embedding_idx",
        "tool_call_id",
        "tool_call_status_idx",
        "tool_name",
        "trace_error_kind_idx",
        "trace_kind_idx",
        "trace_owner_idx",
        "trace_session_idx",
        "trace_success_idx",
        "user_identifier",
    }.ToFrozenSet(StringComparer.Ordinal);

}
