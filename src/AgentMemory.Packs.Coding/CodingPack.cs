using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Repository;
using AgentMemory.Packs.Work;

namespace AgentMemory.Packs.Coding;

/// <summary>
/// The Coding composite pack (KP-composite): decisions, repository experience, the code map and work in one call, with their
/// priorities (decisions first) and their hierarchy (CODING-hierarchy): decisions contain the work done under them and the code
/// they govern, work contains the code it changed, repository experience is about the code. Compose it with
/// <c>modules.AddComposite&lt;CodingPack&gt;()</c>; switch a member off or move it with the options delegate (work needs the
/// code map). The repository pack's writer needs the host to grant a chat model.
/// </summary>
[Experimental("AMEXT001")]
public sealed class CodingPack : IAgentMemoryComposite
{
    /// <summary>The composite identity.</summary>
    public const string Id = "io.agentmemory.packs.coding";

    /// <summary>The embedded composite manifest.</summary>
    public static CompositeManifest Manifest => CompositeManifest.Load(typeof(CodingPack).Assembly);

    /// <inheritdoc/>
    public void AddMembers(CompositeMembers members)
    {
        ArgumentNullException.ThrowIfNull(members);
        members.Add<DecisionsPack>().Add<RepositoryPack>().Add<CodeMapPack>().Add<WorkPack>();
    }
}
