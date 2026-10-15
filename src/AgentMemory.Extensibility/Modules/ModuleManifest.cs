using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Data-only module declaration. Parsing does not authorize, resolve or activate a module.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleManifest
{
    /// <summary>Maximum UTF-8 input size accepted by the manifest reader, including whitespace.</summary>
    public const int MaximumUtf8Bytes = 128 * 1024;
    /// <summary>Maximum assembly-image size accepted for metadata discovery (64 MiB).</summary>
    public const int MaximumAssemblyBytes = 64 * 1024 * 1024;
    /// <summary>The supported wire-format identifier.</summary>
    public required string Format { get; init; }
    /// <summary>The dotted module identity.</summary>
    public required string Id { get; init; }
    /// <summary>The declared module version.</summary>
    public required ModuleVersion Version { get; init; }
    /// <summary>The supported SDK version interval.</summary>
    public required ModuleVersionRange Sdk { get; init; }
    /// <summary>Declared dependency module identities and version intervals.</summary>
    public IReadOnlyDictionary<string, ModuleVersionRange> Modules { get; init; } =
        new Dictionary<string, ModuleVersionRange>().AsReadOnly();
    /// <summary>Explicit code requirements; schema requirements are derived during resolution.</summary>
    public IReadOnlyList<string> Host { get; init; } = [];
    /// <summary>Owned schema declarations, or null for a code-only module.</summary>
    public ModuleOntology? Ontology { get; init; }
    /// <summary>Declarative reads, whose local IDs belong to this module.</summary>
    public IReadOnlyList<ModuleView> Views { get; init; } = [];

    /// <summary>Reads strict bounded JSON without executing code or accessing a database.</summary>
    /// <remarks>Validates wire shape and scalar syntax. Whole-ontology/set validation is a separate
    /// resolution requirement; parsing alone is not evidence that declarations can be activated.</remarks>
    public static ModuleManifest Parse(string json) => ModuleManifestReader.Parse(json);

    /// <summary>Returns all local semantic problems in ordinal order, without executing code or DDL.</summary>
    /// <remarks>Also checks caller-constructed records. Dependency target existence, composed schema
    /// collisions, SDK/host compatibility and publisher authorization require whole-set resolution.</remarks>
    public IReadOnlyList<string> Validate() => new ModuleManifestValidator().Validate(this);

    /// <summary>Reads metadata and the embedded manifest without loading the implementation assembly.</summary>
    /// <remarks>Validates informational-version agreement, ignoring build metadata. This is discovery,
    /// not signature verification or permission to activate. No implementation dependencies are loaded.</remarks>
    public static ModuleManifest LoadFromAssemblyFile(string path) => ModuleAssemblyReader.ReadFile(path);

    /// <summary>Inspects a bounded managed PE image without loading it or executing module code.</summary>
    /// <remarks>The input is copied for parsing. A later loader must separately verify and load its
    /// authorized bytes; discovery does not authenticate an image or a mutable filesystem path.</remarks>
    public static ModuleManifest LoadFromAssemblyImage(ReadOnlyMemory<byte> image) => ModuleAssemblyReader.ReadImage(image);

    /// <summary>Reads the manifest and attribute metadata of an assembly the caller already loaded.</summary>
    /// <remarks>Does not instantiate attributes. Use file/image discovery before loading untrusted code;
    /// this overload cannot undo assembly initialization performed by the caller.</remarks>
    public static ModuleManifest Load(Assembly assembly) => ModuleAssemblyReader.ReadLoaded(assembly);
}
