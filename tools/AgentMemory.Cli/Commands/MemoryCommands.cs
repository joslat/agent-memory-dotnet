using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Thin, unit-testable command handlers over the shipped maintenance services. Each writes
/// human-readable output to the supplied <see cref="TextWriter"/> and returns a process exit code
/// (0 = success, non-zero = usage/runtime error).
/// </summary>
public sealed class MigrateCommand(IMigrationRunner runner, TextWriter output)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        output.WriteLine("Running migrations...");
        await runner.RunMigrationsAsync(cancellationToken);
        output.WriteLine("Migrations complete.");
        return 0;
    }
}

/// <summary>Bootstraps the schema (constraints, indexes, vector indexes).</summary>
public sealed class BootstrapCommand(ISchemaBootstrapper bootstrapper, TextWriter output)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        output.WriteLine("Bootstrapping schema (constraints + indexes)...");
        await bootstrapper.BootstrapAsync(cancellationToken);
        output.WriteLine("Schema bootstrap complete.");
        return 0;
    }
}

/// <summary>Runs the consolidation / hygiene pass (dry-run unless <c>apply</c> is set).</summary>
public sealed class ConsolidateCommand(IConsolidationService service, TextWriter output)
{
    public async Task<int> ExecuteAsync(bool apply, CancellationToken cancellationToken = default)
    {
        var report = await service.ConsolidateAsync(new ConsolidationOptions { DryRun = !apply }, cancellationToken);

        output.WriteLine($"Consolidation {(report.DryRun ? "DRY-RUN (no changes written)" : "APPLIED")} — run {report.RunId}");
        output.WriteLine($"  Conversations archived:        {report.ConversationsArchived}");
        output.WriteLine($"  Duplicate preferences removed: {report.DuplicatePreferencesRemoved}");
        output.WriteLine($"  Duplicate entities detected:   {report.DuplicateEntitiesDetected}");
        output.WriteLine($"  Long-trace candidates:         {report.LongTraceCandidates}");
        if (report.DryRun)
            output.WriteLine("  Re-run with --apply to perform the mutating operations.");
        return 0;
    }
}

/// <summary>Detects fact contradictions (detect-only) and prints the report.</summary>
public sealed class ConflictsCommand(IConflictDetectionService service, TextWriter output)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var report = await service.DetectConflictsAsync(cancellationToken: cancellationToken);

        output.WriteLine($"Conflict detection — {report.FactConflictCount} fact contradiction group(s).");
        foreach (var conflict in report.FactConflicts)
        {
            var owner = conflict.OwnerId is null ? "shared" : $"owner={conflict.OwnerId}";
            output.WriteLine($"  [{owner}] {conflict.Subject} / {conflict.Predicate}:");
            foreach (var value in conflict.Values)
                output.WriteLine($"      = {value.Object}  (fact {value.FactId}, conf {value.Confidence:0.00})");
        }
        if (report.FactConflictCount == 0)
            output.WriteLine("  No contradictions found.");
        return 0;
    }
}

/// <summary>Prunes decayed memories for a session (decay is session-scoped).</summary>
public sealed class DecayCommand(IMemoryDecayService service, TextWriter output)
{
    public async Task<int> ExecuteAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            output.WriteLine("error: 'decay' requires --session <id>.");
            return 2;
        }

        var pruned = await service.PruneExpiredMemoriesAsync(sessionId, cancellationToken);
        output.WriteLine($"Pruned {pruned} expired memory node(s) for session '{sessionId}'.");
        return 0;
    }
}
