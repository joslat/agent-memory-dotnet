using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Packs.CodeMap;

/// <summary>
/// The code map memory pack (coding packs CMP-2a): a repository's projects, files, types and tests at a commit, keyed by SCIP
/// symbols. It runs no model and writes nothing per turn: a reader (Roslyn for C#, SCIP for other languages) builds a
/// <see cref="Extensibility.Packs.ModuleSnapshot"/> and the host imports it, which writes only what changed. Its manifest
/// declares traversal, so the file or symbol in hand leads to the tests that exercise it, and other packs (decisions,
/// lessons) match the same anchors.
/// </summary>
[Experimental("AMEXT001")]
public sealed class CodeMapPack : IAgentMemoryModule
{
    /// <summary>The module identity.</summary>
    public const string Id = "io.agentmemory.packs.codemap";
    /// <summary>A project: one .csproj, or one SCIP package.</summary>
    public const string ProjectType = "CodemapProject";
    /// <summary>A source file.</summary>
    public const string FileType = "CodemapFile";
    /// <summary>A type.</summary>
    public const string SymbolType = "CodemapSymbol";
    /// <summary>A test method.</summary>
    public const string TestType = "CodemapTest";

    private static readonly Lazy<ModuleManifest> Metadata = new(() => ModuleManifest.Load(typeof(CodeMapPack).Assembly));

    /// <summary>The embedded manifest.</summary>
    public static ModuleManifest Manifest => Metadata.Value;

    /// <inheritdoc/>
    /// <remarks>Nothing to add: the traversal contributor comes from the manifest, and records come from imports.</remarks>
    public void Configure(IModuleBuilder builder) => ArgumentNullException.ThrowIfNull(builder);
}
