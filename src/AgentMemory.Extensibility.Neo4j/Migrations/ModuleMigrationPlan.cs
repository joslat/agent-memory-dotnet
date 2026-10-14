using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

/// <summary>Read-only module migration assessment for one configured store.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleMigrationPlan
{
    /// <summary>Definitions not yet stamped applied, including matching interrupted intents.</summary>
    public IReadOnlyList<ModuleMigrationStep> Pending { get; init; } = [];
    /// <summary>Declared definitions whose hashes and database objects match the applied ledger.</summary>
    public IReadOnlyList<ModuleMigrationStep> Applied { get; init; } = [];
    /// <summary>Reasons application must be refused; no partial application is authorized.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    /// <summary>Namespace identities needing durable ownership, even when no new DDL is needed.</summary>
    public IReadOnlyList<string> PendingNamespaces { get; init; } = [];
    /// <summary>SDK bookkeeping constraints needing explicit application; never created by record writes.</summary>
    public IReadOnlyList<string> PendingBookkeeping { get; init; } = [];
    /// <summary>Previously applied module/key identities omitted by this declaration, retained without rollback.</summary>
    public IReadOnlyList<string> UndeclaredAppliedKeys { get; init; } = [];
}
