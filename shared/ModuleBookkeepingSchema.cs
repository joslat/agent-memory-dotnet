using System.Collections.Frozen;

namespace AgentMemory.Shared;

internal static class ModuleBookkeepingSchema
{
    internal const string ExtensionId = "module-bookkeeping";
    internal const string Label = "ModuleMigration";
    internal const string ConstraintName = "module_migration_key";
    internal const string KeyLabel = "ModuleKey";
    internal const string KeyConstraintName = "module_key_id";
    internal static IReadOnlySet<string> KeyProperties { get; } = new[]
    { "id", "module_id", "type", "owner_id", "key", "version", "current_id", "lock_token" }.ToFrozenSet(StringComparer.Ordinal);
    internal const string NamespaceLedgerId = "io.agentmemory.sdk";
    internal static IReadOnlySet<string> Properties { get; } = new[]
    {
        "module_id", "key", "sha256", "state", "actor", "applied_actor",
        "started_at", "applied_at", "owner_module_id", "completed_statements"
    }.ToFrozenSet(StringComparer.Ordinal);
}
