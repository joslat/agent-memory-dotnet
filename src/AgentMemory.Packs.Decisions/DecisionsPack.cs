using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Packs.Decisions;

/// <summary>
/// The decisions memory pack (coding packs CMP-3): what was decided in a project, why, its status, and the code it applies to.
/// Decisions come from the project's own files (ADR and MADR files, Squad's <c>.squad/decisions.md</c>) through a deterministic
/// reader and a snapshot import; no model writes them. Each decision names the files and types it applies to, found from the
/// paths and type names its text mentions, so traversal context brings it up when that code is in hand, and its search view
/// brings it up when a question names what it is about.
/// </summary>
[Experimental("AMEXT001")]
public sealed class DecisionsPack : IAgentMemoryModule
{
    /// <summary>The module identity.</summary>
    public const string Id = "io.agentmemory.packs.decisions";
    /// <summary>The one record type.</summary>
    public const string Type = "DecisionsDecision";
    /// <summary>The search view recall uses.</summary>
    public const string RelevantView = "relevant";
    /// <summary>A decision replaces another (CP-acceptance: read from "Supersedes" and "Superseded by" in ADRs).</summary>
    public const string Supersedes = "DECISIONS_SUPERSEDES";

    private static readonly Lazy<ModuleManifest> Metadata = new(() => ModuleManifest.Load(typeof(DecisionsPack).Assembly));

    /// <summary>The embedded manifest.</summary>
    public static ModuleManifest Manifest => Metadata.Value;

    /// <inheritdoc/>
    public void Configure(IModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddContextContributor(c => new ViewContributor(c.Resolution, c.Reader, RelevantView));
    }
}
