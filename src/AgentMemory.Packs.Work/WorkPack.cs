using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Packs.Work;

/// <summary>
/// The work memory pack (CODING-hierarchy): a project's work items from its task ledger, each linked to the source files its
/// commits changed. It sits between decisions and the code map in the Coding family: work contains the code it changed, so a
/// walk from the file in hand climbs to the work that last shaped it, with the item's status and why it was asked for. Like the
/// code map it runs no model and writes nothing per turn: <see cref="WorkLedgerReader"/> builds a snapshot the host imports.
/// </summary>
[Experimental("AMEXT001")]
public sealed class WorkPack : IAgentMemoryModule
{
    /// <summary>The module identity.</summary>
    public const string Id = "io.agentmemory.packs.work";
    /// <summary>A work item.</summary>
    public const string ItemType = "WorkItem";
    /// <summary>A work item to a source file its commits changed (a code map file).</summary>
    public const string Changed = "WORK_CHANGED";

    private static readonly Lazy<ModuleManifest> Metadata = new(() => ModuleManifest.Load(typeof(WorkPack).Assembly));

    /// <summary>The embedded manifest.</summary>
    public static ModuleManifest Manifest => Metadata.Value;

    /// <inheritdoc/>
    /// <remarks>Nothing to add: the traversal contributor comes from the manifest, and records come from imports.</remarks>
    public void Configure(IModuleBuilder builder) => ArgumentNullException.ThrowIfNull(builder);
}
