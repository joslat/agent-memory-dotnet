using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

[Experimental("AMEXT001")]
internal static class ModuleMigrationProgress
{
    internal static int Completed(ModuleCompiledMigration migration, ModuleLedgerEntry entry)
    {
        var definition = migration.Definition;
        var identity = definition.ModuleId + "/" + definition.Key;
        if (entry.ModuleId != definition.ModuleId || entry.Key != definition.Key || entry.Hash != definition.Sha256)
            throw new InvalidOperationException("Migration hash/identity differs from reserved/applied intent: " + identity);
        if (entry.State is not ("applying" or "applied"))
            throw new InvalidOperationException("Invalid migration state: " + identity);
        // Before statement progress existed, generated definitions had one replay-safe DDL operation.
        // Handwritten migrations have never shipped without progress; absence must not replay a backfill.
        var legacy = entry.CompletedStatements is null && !entry.InvalidProgress
            && definition.Key.StartsWith("derived/", StringComparison.Ordinal) && migration.Operations.Count == 1;
        var completed = legacy ? entry.State == "applied" ? 1 : 0 : entry.CompletedStatements;
        if (entry.InvalidProgress || completed is null || completed < 0 || completed > migration.Operations.Count
            || (entry.State == "applied" && completed != migration.Operations.Count))
            throw new InvalidOperationException("Invalid or missing migration statement progress: " + identity);
        return (int)completed.Value;
    }
}
