using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Queries;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Shared;
using Neo4j.Driver;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

/// <summary>Explicit per-store application of derived module schema with durable ownership and intent.</summary>
/// <remarks>No work runs in the constructor or registration. Empty schema composition makes zero
/// database calls. This experimental entrypoint currently handles generated definitions; hand-script
/// loading and hostless CLI integration are separate unfinished work. The caller authorizes module
/// metadata and supplies the transaction runner for exactly one store.</remarks>
[Experimental("AMEXT001")]
public sealed class ModuleMigrator
{
    private readonly INeo4jTransactionRunner _runner;
    private readonly ModuleResolution _resolution;
    private readonly IReadOnlyList<ModuleMigrationStep> _steps;
    private readonly IReadOnlyList<ModuleNamespaceClaim> _claims;
    private readonly TimeProvider _clock;
    private readonly object _readParameters;

    /// <summary>Prepares immutable definitions without database calls or module code execution.</summary>
    public ModuleMigrator(INeo4jTransactionRunner runner, ModuleResolution resolution, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(resolution);
        _runner = runner;
        _resolution = resolution;
        _steps = ModuleMigrationDefinitions.Derive(resolution);
        if (resolution.Modules.Any(m => m.Id == ModuleBookkeepingSchema.NamespaceLedgerId))
            throw new ArgumentException("The SDK bookkeeping identity cannot be used by an application module.", nameof(resolution));
        _claims = ModuleMigrationAssessment.Claims(resolution);
        _clock = timeProvider ?? TimeProvider.System;
        _readParameters = new { moduleIds = resolution.Modules.Select(m => m.Id).ToArray(), sdkId = ModuleBookkeepingSchema.NamespaceLedgerId };
    }

    /// <summary>Reads schema and ledger without creating constraints, claims or intent rows.</summary>
    public async Task<ModuleMigrationPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_claims.Count == 0) return new();
        return await _runner.ReadAsync(async r => Assess(await ModuleDatabaseSnapshot.ReadAsync(r, _readParameters, cancellationToken)
            .ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reserves matching ownership/hashes, applies pending derived DDL, then stamps completion.</summary>
    /// <remarks>Actor is mandatory. DDL and ledger stamps are separate transactions; failures preserve
    /// applying intent and matching retries resume. Foreign namespaces and changed hashes are refused.
    /// Administrators must serialize external schema changes with this operation; this lock coordinates
    /// compliant module migrators, not arbitrary database administrators.</remarks>
    public async Task<ModuleMigrationPlan> ApplyAsync(string actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An explicit migration actor is required.", nameof(actor));
        cancellationToken.ThrowIfCancellationRequested();
        if (_claims.Count == 0) return new();
        var initial = await PlanAsync(cancellationToken).ConfigureAwait(false);
        RefuseProblems(initial);
        // A fully applied set has no DDL or ledger writes on subsequent application.
        if (initial.Pending.Count == 0 && initial.PendingNamespaces.Count == 0) return initial;
        await ExecuteAsync(ModuleLedgerQueries.CreateLedger, null, cancellationToken).ConfigureAwait(false);
        var reserved = await _runner.WriteAsync(async r =>
        {
            await ExecuteInAsync(r, ModuleLedgerQueries.Lock, new { sdkId = ModuleBookkeepingSchema.NamespaceLedgerId,
                actor, now = _clock.GetUtcNow().ToString("O") }, cancellationToken).ConfigureAwait(false);
            var current = Assess(await ModuleDatabaseSnapshot.ReadAsync(r, _readParameters, cancellationToken).ConfigureAwait(false));
            RefuseProblems(current);
            foreach (var claim in _claims)
                await ExecuteInAsync(r, ModuleLedgerQueries.Claim, new { sdkId = ModuleBookkeepingSchema.NamespaceLedgerId,
                    key = claim.Key, owner = claim.Owner, actor, now = _clock.GetUtcNow().ToString("O") }, cancellationToken).ConfigureAwait(false);
            foreach (var step in current.Pending)
                await ExecuteInAsync(r, ModuleLedgerQueries.Intent, Parameters(step, actor), cancellationToken).ConfigureAwait(false);
            return current;
        }, cancellationToken).ConfigureAwait(false);
        foreach (var step in reserved.Pending)
        {
            await ExecuteAsync(step.Statement, null, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(ModuleLedgerQueries.Applied, Parameters(step, actor), cancellationToken).ConfigureAwait(false);
        }
        var final = await PlanAsync(cancellationToken).ConfigureAwait(false);
        RefuseProblems(final);
        if (final.Pending.Count != 0 || final.PendingNamespaces.Count != 0) throw new InvalidOperationException("Module migration application did not complete all reserved intents.");
        return final;
    }

    private object Parameters(ModuleMigrationStep step, string actor) => new
    { moduleId = step.ModuleId, key = step.Key, hash = step.Sha256, actor, now = _clock.GetUtcNow().ToString("O") };

    private ModuleMigrationPlan Assess(ModuleDatabaseSnapshot snapshot) =>
        ModuleMigrationAssessment.Assess(_resolution, _steps, _claims, snapshot);

    private static void RefuseProblems(ModuleMigrationPlan plan)
    {
        if (plan.Problems.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Problems));
    }

    private Task ExecuteAsync(string statement, object? parameters, CancellationToken cancellationToken) =>
        _runner.WriteAsync(r => ExecuteInAsync(r, statement, parameters, cancellationToken), cancellationToken);

    private static async Task ExecuteInAsync(IAsyncQueryRunner runner, string statement, object? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = parameters is null ? await runner.RunAsync(statement).ConfigureAwait(false)
            : await runner.RunAsync(statement, parameters).ConfigureAwait(false);
        await cursor.ConsumeAsync().ConfigureAwait(false);
    }
}
