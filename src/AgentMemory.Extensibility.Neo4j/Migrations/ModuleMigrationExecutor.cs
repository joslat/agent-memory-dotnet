using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Neo4j.Queries;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Shared;
using Neo4j.Driver;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

// DDL must be committed independently; backfills and their durable progress share one transaction.
// The existing SDK lock serializes compliant writers before checking or advancing a data statement.
[Experimental("AMEXT001")]
internal sealed class ModuleMigrationExecutor(INeo4jTransactionRunner runner, TimeProvider clock)
{
    /// <summary>Neo4j's refusal of the slower of two commits that both passed IF NOT EXISTS.</summary>
    internal const string EquivalentSchemaRule = "Neo.ClientError.Schema.EquivalentSchemaRuleAlreadyExists";

    internal async Task ApplyAsync(ModuleCompiledMigration migration, string actor, CancellationToken cancellationToken)
    {
        for (var index = 0; index < migration.Operations.Count; index++)
        {
            var operation = migration.Operations[index];
            if (operation.IsSchema)
            {
                var entry = await runner.ReadAsync(r => ReadEntryAsync(r, migration, actor, cancellationToken), cancellationToken).ConfigureAwait(false);
                var completed = ModuleMigrationProgress.Completed(migration, entry);
                if (completed > index) continue;
                RequirePrevious(completed, index);
                try
                {
                    await runner.WriteAsync(r => ExecuteAsync(r, operation.Text, null, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                catch (ClientException e) when (e.Code == EquivalentSchemaRule)
                {
                    if (!await CreatedConcurrentlyAsync(migration, operation.Shape!, cancellationToken).ConfigureAwait(false)) throw;
                }
            }
            await runner.WriteAsync(async r =>
            {
                await LockAsync(r, actor, cancellationToken).ConfigureAwait(false);
                var entry = await ReadEntryAsync(r, migration, actor, cancellationToken).ConfigureAwait(false);
                var completed = ModuleMigrationProgress.Completed(migration, entry);
                if (completed > index) return;
                RequirePrevious(completed, index);
                if (!operation.IsSchema)
                    await ExecuteAsync(r, operation.Text, null, cancellationToken).ConfigureAwait(false);
                // A managed-transaction retry re-reads this counter: a committed update is never replayed.
                await ChangeAsync(r, ModuleLedgerQueries.Progress, Parameters(migration, actor, index, index + 1), cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        await runner.WriteAsync(async r =>
        {
            await LockAsync(r, actor, cancellationToken).ConfigureAwait(false);
            var entry = await ReadEntryAsync(r, migration, actor, cancellationToken).ConfigureAwait(false);
            var completed = ModuleMigrationProgress.Completed(migration, entry);
            RequirePrevious(completed, migration.Operations.Count);
            if (entry.State == "applied") return;
            await ChangeAsync(r, ModuleLedgerQueries.Applied, Parameters(migration, actor), cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    // Another compliant migrator committed this statement first. Accept only the exact declared object with this
    // migration's reserved intent unchanged; the locked progress step below then advances the counter once.
    private Task<bool> CreatedConcurrentlyAsync(ModuleCompiledMigration migration, ModuleMigrationSchemaObject shape,
        CancellationToken cancellationToken) => runner.ReadAsync(async r =>
        {
            var definition = migration.Definition;
            var snapshot = await ModuleDatabaseSnapshot.ReadAsync(r, new { moduleIds = new[] { definition.ModuleId },
                sdkId = ModuleBookkeepingSchema.NamespaceLedgerId }, cancellationToken).ConfigureAwait(false);
            var entries = snapshot.Entries.Where(e => e.ModuleId == definition.ModuleId && e.Key == definition.Key).ToArray();
            if (entries.Length != 1) return false;
            try { ModuleMigrationProgress.Completed(migration, entries[0]); }
            catch (InvalidOperationException) { return false; }
            return ModuleMigrationAssessment.IsDeclaredObjectPresent(snapshot, shape);
        }, cancellationToken);

    private Task LockAsync(IAsyncQueryRunner transaction, string actor, CancellationToken cancellationToken) =>
        ExecuteAsync(transaction, ModuleLedgerQueries.Lock,
            new { sdkId = ModuleBookkeepingSchema.NamespaceLedgerId, actor, now = clock.GetUtcNow().ToString("O") }, cancellationToken);

    private async Task<ModuleLedgerEntry> ReadEntryAsync(IAsyncQueryRunner transaction, ModuleCompiledMigration migration,
        string actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await transaction.RunAsync(ModuleLedgerQueries.Step, Parameters(migration, actor)).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
        if (rows.Count != 1) throw new InvalidOperationException("Reserved migration intent is missing or duplicated.");
        return ModuleLedgerEntry.FromRow(rows[0]["row"].As<Dictionary<string, object>>());
    }

    private object Parameters(ModuleCompiledMigration migration, string actor, int before = 0, int after = 0) => new
    {
        moduleId = migration.Definition.ModuleId, key = migration.Definition.Key, hash = migration.Definition.Sha256,
        actor, now = clock.GetUtcNow().ToString("O"), total = migration.Operations.Count, before, after
    };

    private static void RequirePrevious(int completed, int expected)
    {
        if (completed != expected) throw new InvalidOperationException("Migration statement progress does not match execution order.");
    }

    private static async Task ChangeAsync(IAsyncQueryRunner transaction, string query, object parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await transaction.RunAsync(query, parameters).ConfigureAwait(false);
        if ((await cursor.ToListAsync().ConfigureAwait(false)).Count != 1)
            throw new InvalidOperationException("Migration progress changed unexpectedly; transaction refused.");
    }

    private static async Task ExecuteAsync(IAsyncQueryRunner transaction, string query, object? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = parameters is null ? await transaction.RunAsync(query).ConfigureAwait(false)
            : await transaction.RunAsync(query, parameters).ConfigureAwait(false);
        await cursor.ConsumeAsync().ConfigureAwait(false);
    }
}
