using System.Collections.Frozen;

namespace AgentMemory.Shared;

internal static class ModuleBookkeepingSchema
{
    internal const string ExtensionId = "module-bookkeeping";
    internal const string Label = "ModuleMigration";
    internal const string ConstraintName = "module_migration_key";
    internal const string NamespaceLedgerId = "io.agentmemory.sdk";
    internal static IReadOnlySet<string> Properties { get; } = new[]
    {
        "module_id", "key", "sha256", "state", "actor", "applied_actor",
        "started_at", "applied_at", "owner_module_id", "completed_statements"
    }.ToFrozenSet(StringComparer.Ordinal);
}
