// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Packs.Intent;

/// <summary>
/// The product intent memory pack (coding packs CMP-6): what the product is meant to do, at the top of the coding family's
/// hierarchy. Requirements in EARS form, the Gherkin scenarios that pin them down (a scenario tagged with a requirement's id
/// verifies it), OpenSLO service level objectives, and ideas not yet requirements; read from the repository's own files
/// through a deterministic reader; no model writes them. A requirement's status is what its document says (proposed by
/// default); how many scenarios are tagged with it is counted, never mistaken for a run that passed.
/// </summary>
[Experimental("AMEXT001")]
public sealed class IntentPack : IAgentMemoryModule
{
    /// <summary>The module identity.</summary>
    public const string Id = "io.agentmemory.packs.intent";
    /// <summary>A requirement.</summary>
    public const string RequirementType = "IntentRequirement";
    /// <summary>A Gherkin feature.</summary>
    public const string FeatureType = "IntentFeature";
    /// <summary>A Gherkin scenario.</summary>
    public const string ScenarioType = "IntentScenario";
    /// <summary>A service level objective.</summary>
    public const string ObjectiveType = "IntentObjective";
    /// <summary>An idea.</summary>
    public const string IdeaType = "IntentIdea";
    /// <summary>Scenario belongs to a feature.</summary>
    public const string ScenarioOf = "INTENT_SCENARIO_OF";
    /// <summary>Scenario is tagged with a requirement's id.</summary>
    public const string Verifies = "INTENT_VERIFIES";

    private static readonly Lazy<ModuleManifest> Metadata = new(() => ModuleManifest.Load(typeof(IntentPack).Assembly));

    /// <summary>The embedded manifest.</summary>
    public static ModuleManifest Manifest => Metadata.Value;

    /// <inheritdoc/>
    public void Configure(IModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var view in new[] { "requirements", "scenarios", "objectives", "ideas" })
            builder.AddContextContributor(c => new ViewContributor(c.Resolution, c.Reader, view));
    }
}
