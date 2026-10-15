using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Diagnostics;

namespace AgentMemory.Extensibility.Context;

public sealed partial class ContextCompiler
{
    private Activity? StartCompileActivity(IReadOnlySet<string>? excluded)
    {
        if (Resolution is null || !AgentMemoryDiagnostics.Source.HasListeners()
            || !_contributors.Any(c => c is BoundContributor && excluded?.Contains(c.Descriptor.Id) != true)) return null;
        var activity = AgentMemoryDiagnostics.Source.StartActivity(ModuleTelemetry.Compile);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(ModuleTelemetry.Fingerprint, Resolution.ManifestFingerprint);
            activity.SetTag(ModuleTelemetry.FingerprintKind, "metadata");
            activity.SetTag(ModuleTelemetry.ModuleCount, _contributors.OfType<BoundContributor>()
                .Where(c => excluded?.Contains(c.Descriptor.Id) != true).Select(c => c.ModuleId).Distinct(StringComparer.Ordinal).Count());
        }
        return activity;
    }

    private static Activity? StartContributionActivity(IContextContributor contributor)
    {
        if (contributor is not BoundContributor bound) return null;
        var activity = AgentMemoryDiagnostics.Source.StartActivity(ModuleTelemetry.Contribute);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(ModuleTelemetry.ModuleId, bound.ModuleId);
            activity.SetTag(ModuleTelemetry.ModuleVersion, bound.ModuleVersion);
            activity.SetTag(ModuleTelemetry.ContributorId, bound.Descriptor.Id);
        }
        return activity;
    }

    private static void ObserveOmission(Activity? compile, Activity? contribution,
        IContextContributor contributor, ContextOmissionReason reason)
    {
        if (contributor is not BoundContributor bound) return;
        if (contribution is { IsAllDataRequested: true }) ObserveOutcome(contribution, reason.ToString(), 0);
        if (compile is { IsAllDataRequested: true })
            compile.AddEvent(new ActivityEvent(ModuleTelemetry.Omission, tags: new ActivityTagsCollection
            {
                [ModuleTelemetry.ContributorId] = bound.Descriptor.Id,
                [ModuleTelemetry.Outcome] = reason.ToString()
            }));
    }

    private static void ObserveOutcome(Activity? activity, string outcome, int? items = null)
    {
        if (activity is not { IsAllDataRequested: true }) return;
        activity.SetTag(ModuleTelemetry.Outcome, outcome);
        if (items is { } count) activity.SetTag(ModuleTelemetry.Items, count);
        activity.SetStatus(outcome is "Unavailable" or "Cancelled" ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
    }
}
