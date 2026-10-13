using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Data-only module declaration. Parsing does not authorize, resolve or activate a module.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleManifest
{
    /// <summary>Maximum UTF-8 input size accepted by the manifest reader, including whitespace.</summary>
    public const int MaximumUtf8Bytes = 128 * 1024;
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
}
