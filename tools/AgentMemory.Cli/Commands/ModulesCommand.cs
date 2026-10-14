#pragma warning disable AMEXT001 // Explicit operator tooling consumes the held module API.
using System.Text.Json;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Cli.Commands;

/// <summary>Explicit migration planning/application for one profile store; never activates modules.</summary>
internal sealed class ModulesCommand(INeo4jTransactionRunner runner, TextWriter output)
{
    public async Task<int> ExecuteAsync(ModuleCommandInput input, CancellationToken cancellationToken = default)
    {
        // Compilation and namespace checks happen in the constructor before any database call.
        var migrator = new ModuleMigrator(runner, input.Resolution, input.Scripts, null);
        var plan = await migrator.PlanAsync(cancellationToken).ConfigureAwait(false);
        if (input.Operation == "apply" && plan.Problems.Count == 0)
            plan = await migrator.ApplyAsync(input.Actor!, cancellationToken).ConfigureAwait(false);
        var report = new
        {
            format = "agentmemory.module-migrations/v1alpha1",
            operation = input.Operation, actor = input.Operation == "apply" ? input.Actor : null,
            store = input.Store, provider = "neo4j-direct", profileSha256 = input.ProfileSha256,
            compositionFingerprint = input.CompositionFingerprint,
            storeFingerprint = input.Resolution.ManifestFingerprint,
            targetHostFeatures = input.TargetHostFeatures,
            hostFeatureEvidence = "Explicit target declarations; no running host capability probe or activation.",
            sources = input.Sources,
            modules = input.Resolution.Modules.Select(m => new { m.Id, version = m.Version.Value }),
            status = plan.Problems.Count == 0 ? "complete" : "refused",
            plan
        };
        await output.WriteLineAsync(JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).ConfigureAwait(false);
        return plan.Problems.Count == 0 ? 0 : 2;
    }
}
