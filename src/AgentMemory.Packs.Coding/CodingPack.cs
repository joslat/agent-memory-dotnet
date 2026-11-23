// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Architecture;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Intent;
using AgentMemory.Packs.Repository;
using AgentMemory.Packs.Work;

namespace AgentMemory.Packs.Coding;

/// <summary>
/// The Coding composite pack (KP-composite): product intent, decisions, repository experience, the code map and work in one
/// call, with their priorities (decisions first). Which member may link to which follows the evidence (CO-2, the owner's choice
/// of 22 Nov 2026): a link is stored by the pack whose source says it, so decisions, work and the code map contain (may link
/// into) intent, work contains the code it changed, repository experience is about the code. What the members mean to each
/// other is the family's layer order, from why to what (<see cref="Layers"/>): a walk labels each hop between layers by it.
/// Architecture is a member that starts off until it has links of its own (slice 2). Compose it with
/// <c>modules.AddComposite&lt;CodingPack&gt;()</c>; switch a member off or move it with the options delegate (work needs the
/// code map and intent; decisions and the code map need intent). The repository pack's writer needs the host to grant a chat
/// model.
/// </summary>
[Experimental("AMEXT001")]
public sealed class CodingPack : IAgentMemoryComposite
{
    /// <summary>The composite identity.</summary>
    public const string Id = "io.agentmemory.packs.coding";

    /// <summary>
    /// The family's layers, for meaning only, from why to what (CO-2 design, layers 1 to 5): intent, decisions, architecture and
    /// APIs, work, code. Repository experience has no layer: a hop into or out of it is neither why nor what.
    /// </summary>
    public static IReadOnlyList<string> Layers { get; } = [IntentPack.Id, DecisionsPack.Id, ArchitecturePack.Id, WorkPack.Id, CodeMapPack.Id];

    /// <summary>The embedded composite manifest.</summary>
    public static CompositeManifest Manifest => CompositeManifest.Load(typeof(CodingPack).Assembly);

    /// <inheritdoc/>
    public void AddMembers(CompositeMembers members)
    {
        ArgumentNullException.ThrowIfNull(members);
        members.Add<DecisionsPack>().Add<RepositoryPack>().Add<CodeMapPack>().Add<WorkPack>().Add<IntentPack>().Add<ArchitecturePack>()
            .Layers([.. Layers]);
    }
}
