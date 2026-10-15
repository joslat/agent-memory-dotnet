using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Extensibility.Diagnostics;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

public sealed partial class ModuleMigrator
{
    private Activity? StartMigrationActivity(string operation, string? moduleId = null)
    {
        var activity = AgentMemoryDiagnostics.Source.StartActivity(ModuleTelemetry.Migrate);
        if (activity is not { IsAllDataRequested: true }) return activity;
        activity.SetTag(ModuleTelemetry.Operation, operation);
        activity.SetTag(ModuleTelemetry.Fingerprint, _resolution.ManifestFingerprint);
        activity.SetTag(ModuleTelemetry.FingerprintKind, "metadata");
        if (moduleId is not null)
        {
            var module = _resolution.Modules.Single(m => m.Id == moduleId);
            activity.SetTag(ModuleTelemetry.ModuleId, module.Id);
            activity.SetTag(ModuleTelemetry.ModuleVersion, module.Version.ToString());
        }
        return activity;
    }

    private async Task ApplyOwnedStepAsync(ModuleMigrationStep step, string actor, CancellationToken cancellationToken)
    {
        using var activity = StartMigrationActivity("step", step.ModuleId);
        try
        {
            await _executor.ApplyAsync(_compilation.Steps.Single(s => s.Definition.ModuleId == step.ModuleId && s.Definition.Key == step.Key),
                actor, cancellationToken).ConfigureAwait(false);
            ObserveMigrationOutcome(activity, "Completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveMigrationOutcome(activity, "Cancelled");
            throw;
        }
        catch
        {
            ObserveMigrationFailure(activity);
            throw;
        }
    }

    private static void ObservePlan(Activity? activity, ModuleMigrationPlan plan, string? outcome = null)
    {
        if (activity is not { IsAllDataRequested: true }) return;
        activity.SetTag(ModuleTelemetry.MigrationsPending, plan.Pending.Count);
        activity.SetTag(ModuleTelemetry.MigrationsApplied, plan.Applied.Count);
        activity.SetTag(ModuleTelemetry.MigrationProblems, plan.Problems.Count);
        ObserveMigrationOutcome(activity, outcome ?? (plan.Problems.Count > 0 ? "Refused"
            : plan.Pending.Count > 0 || plan.PendingNamespaces.Count > 0 || plan.PendingBookkeeping.Count > 0 ? "Pending" : "Completed"));
    }

    private static void ObserveMigrationFailure(Activity? activity)
    {
        if (activity is not { IsAllDataRequested: true }) return;
        // Preserve a known policy refusal rather than relabel it as a database failure.
        if (!Equals(activity.GetTagItem(ModuleTelemetry.Outcome), "Refused")) ObserveMigrationOutcome(activity, "Unavailable");
    }

    private static void ObserveMigrationOutcome(Activity? activity, string outcome)
    {
        if (activity is not { IsAllDataRequested: true }) return;
        activity.SetTag(ModuleTelemetry.Outcome, outcome);
        activity.SetStatus(outcome is "Unavailable" or "Cancelled" or "Refused" ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
    }
}
