using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
#pragma warning disable AMEXT001 // Import helpers used only inside the experimental module API.
using static AgentMemory.Extensibility.Modules.ModuleJson;
#pragma warning restore AMEXT001

namespace AgentMemory.Extensibility.Modules;

/// <summary>Immutable parsed host/CLI composition input. Reading it performs no loading or activation.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleCompositionProfile
{
    /// <summary>Maximum UTF-8 profile size, including whitespace (128 KiB).</summary>
    public const int MaximumUtf8Bytes = 128 * 1024;
    private ModuleCompositionProfile(IReadOnlyDictionary<string, ModuleProfileStore> stores,
        IReadOnlyList<ModuleProfileEntry> modules, string sha256)
    { Stores = stores; Modules = modules; Sha256 = sha256; }
    /// <summary>Declared store aliases and provider identities; not instantiated services.</summary>
    public IReadOnlyDictionary<string, ModuleProfileStore> Stores { get; }
    /// <summary>Explicit module composition, in declaration order.</summary>
    public IReadOnlyList<ModuleProfileEntry> Modules { get; }
    /// <summary>SHA-256 of exactly the UTF-8 input string, including whitespace.</summary>
    public string Sha256 { get; }

    /// <summary>Parses strict bounded profile JSON without opening declared paths or binding options.</summary>
    public static ModuleCompositionProfile Parse(string json)
    {
        using var document = Document(json, MaximumUtf8Bytes, "profile");
        var root = document.RootElement;
        Object(root, "$", "format", "stores", "modules");
        if (Text(root, "format", "$") != "agentmemory.modules/v1alpha1") throw Error("$.format", "unsupported profile format");
        if (!root.TryGetProperty("stores", out var stores)) throw Error("$.stores", "required field missing");
        Kind(stores, JsonValueKind.Object, "$.stores");
        var bindings = new Dictionary<string, ModuleProfileStore>(StringComparer.Ordinal);
        foreach (var store in stores.EnumerateObject())
        {
            var path = "$.stores." + store.Name;
            if (!ModuleIdentity.IsValidLocalId(store.Name)) throw Error(path, "invalid store alias");
            Object(store.Value, path, "provider");
            var provider = Text(store.Value, "provider", path);
            if (!ModuleIdentity.IsValidLocalId(provider)) throw Error(path + ".provider", "invalid provider identity");
            bindings.Add(store.Name, new ModuleProfileStore { Provider = provider });
        }
        if (!root.TryGetProperty("modules", out _)) throw Error("$.modules", "required field missing");
        var entries = Array(root, "modules", "$", Entry);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (!ids.Add(entry.Id)) throw Error("$.modules", $"duplicate module '{entry.Id}'");
        return new ModuleCompositionProfile(bindings.AsReadOnly(), entries,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
    }

    private static ModuleProfileEntry Entry(JsonElement value, string path)
    {
        Object(value, path, "id", "version", "store", "assembly", "manifest", "configuration", "sha256");
        var id = Text(value, "id", path);
        if (!ModuleIdentity.IsValidModuleId(id)) throw Error(path + ".id", "invalid module identity");
        var store = Text(value, "store", path);
        if (!ModuleIdentity.IsValidLocalId(store)) throw Error(path + ".store", "invalid store alias");
        var assembly = OptionalText(value, "assembly", path);
        var manifest = OptionalText(value, "manifest", path);
        if ((assembly is null) == (manifest is null)) throw Error(path, "exactly one assembly or manifest source is required");
        if ((assembly ?? manifest)!.Any(char.IsControl)) throw Error(path, "source path contains control characters");
        var configuration = OptionalText(value, "configuration", path);
        if (configuration is not null && (configuration.Any(char.IsControl) || configuration.Split(':').Any(string.IsNullOrWhiteSpace)))
            throw Error(path + ".configuration", "invalid configuration section path");
        var pin = OptionalText(value, "sha256", path);
        if (pin is not null && (pin.Length != 64 || pin.Any(c => !char.IsAsciiHexDigit(c))))
            throw Error(path + ".sha256", "expected 64 hexadecimal characters");
        return new ModuleProfileEntry
        {
            Id = id, Version = Range(Text(value, "version", path), path + ".version"), Store = store,
            Assembly = assembly, Manifest = manifest, Configuration = configuration, Sha256 = pin?.ToLowerInvariant()
        };
    }
}

/// <summary>Requested store provider identity; resolution must compare it to the host binding.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleProfileStore
{
    /// <summary>Host-defined provider identity.</summary>
    public required string Provider { get; init; }
}

/// <summary>Explicit module version, store and discovery/configuration sources.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleProfileEntry
{
    /// <summary>Module identity.</summary>
    public required string Id { get; init; }
    /// <summary>Accepted module versions.</summary>
    public required ModuleVersionRange Version { get; init; }
    /// <summary>Declared store alias.</summary>
    public required string Store { get; init; }
    /// <summary>Assembly discovery path; mutually exclusive with Manifest. Not loaded by parsing.</summary>
    public string? Assembly { get; init; }
    /// <summary>Data-only manifest discovery path; mutually exclusive with Assembly.</summary>
    public string? Manifest { get; init; }
    /// <summary>Explicit options section, or null for AgentMemory:Modules:&lt;moduleId&gt;.</summary>
    public string? Configuration { get; init; }
    /// <summary>Optional expected bundle digest. Merely parsing this pin does not verify any bytes.</summary>
    public string? Sha256 { get; init; }
}
