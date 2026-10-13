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
}
