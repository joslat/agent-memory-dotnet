using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
#pragma warning disable AMEXT001 // Import helpers used only inside the experimental module API.
using static AgentMemory.Extensibility.Modules.ModuleJson;
#pragma warning restore AMEXT001

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// A composite pack (KP-composite): a pack made of packs, such as "Coding" made of the code map, decisions and repository
/// experience. It names its members with version ranges, the priority each takes and whether it starts on; it holds no records,
/// views or code of its own. Members stay whole modules: separately versioned, signed where they are bundles, and switchable.
/// One level only: a composite's members are modules, never composites.
/// </summary>
[Experimental("AMEXT001")]
public sealed record CompositeManifest
{
    /// <summary>The wire format.</summary>
    public const string FormatName = "agentmemory.composite/v1alpha1";
    /// <summary>Most members one composite names.</summary>
    public const int MaximumMembers = 16;
    /// <summary>The embedded resource suffix a composite assembly carries its manifest under.</summary>
    public const string ResourceSuffix = ".agentmemory-composite.json";

    /// <summary>The wire format identifier.</summary>
    public required string Format { get; init; }
    /// <summary>The dotted composite identity.</summary>
    public required string Id { get; init; }
    /// <summary>The composite's own version.</summary>
    public required ModuleVersion Version { get; init; }
    /// <summary>How a host shows it: title, description, icon and colour.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModuleDisplay? Display { get; init; }
    /// <summary>The member modules, in the order a host lists them.</summary>
    public IReadOnlyList<CompositeMember> Members { get; init; } = [];

    /// <summary>Reads strict bounded JSON.</summary>
    public static CompositeManifest Parse(string json)
    {
        using var document = Document(json, ModuleManifest.MaximumUtf8Bytes, "composite manifest");
        var root = document.RootElement;
        Object(root, "$", "format", "id", "version", "display", "members");
        var format = Text(root, "format", "$");
        if (format != FormatName) throw Error("$.format", "unsupported composite format");
        return new CompositeManifest
        {
            Format = format, Id = Text(root, "id", "$"), Version = Version(Text(root, "version", "$"), "$.version"),
            Display = root.TryGetProperty("display", out var display) ? DisplayOf(display, "$.display") : null,
            Members = Array(root, "members", "$", Member),
        };
    }

    /// <summary>The composite embedded in an assembly (resource <c>&lt;AssemblyName&gt;.agentmemory-composite.json</c>).</summary>
    public static CompositeManifest Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var name = assembly.GetName().Name + ResourceSuffix;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Assembly '{assembly.GetName().Name}' carries no composite manifest ({name}).");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Every problem, in order: identity, version, members (identity, range, priority, duplicates, count).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!ModuleIdentity.IsValidModuleId(Id)) problems.Add("$.id: invalid dotted composite identity.");
        if (Members.Count == 0) problems.Add("$.members: a composite names at least one member.");
        if (Members.Count > MaximumMembers) problems.Add($"$.members: at most {MaximumMembers} members.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in Members)
        {
            var at = "$.members." + member.Id;
            if (!ModuleIdentity.IsValidModuleId(member.Id)) problems.Add(at + ": invalid dotted module identity.");
            if (member.Id == Id) problems.Add(at + ": a composite cannot name itself.");
            if (!ids.Add(member.Id)) problems.Add(at + ": named twice.");
            if (member.Priority is < 0 or > 10_000) problems.Add(at + ".priority: expected 0 to 10,000.");
        }
        return problems;
    }

    private static CompositeMember Member(JsonElement value, string path)
    {
        Object(value, path, "id", "version", "priority", "enabled");
        return new CompositeMember
        {
            Id = Text(value, "id", path), Version = Range(Text(value, "version", path), path + ".version"),
            Priority = value.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number && priority.TryGetInt32(out var p)
                ? p : value.TryGetProperty("priority", out _) ? throw Error(path + ".priority", "expected an integer") : 100,
            Enabled = !value.TryGetProperty("enabled", out _) || Boolean(value, "enabled", path),
        };
    }

    private static ModuleDisplay DisplayOf(JsonElement value, string path)
    {
        Object(value, path, "title", "description", "icon", "color");
        return new ModuleDisplay
        {
            Title = OptionalText(value, "title", path), Description = OptionalText(value, "description", path),
            Icon = OptionalText(value, "icon", path), Color = OptionalText(value, "color", path),
        };
    }
}

/// <summary>One member of a composite.</summary>
[Experimental("AMEXT001")]
public sealed record CompositeMember
{
    /// <summary>The member module's identity.</summary>
    public required string Id { get; init; }
    /// <summary>The member versions the composite accepts.</summary>
    public required ModuleVersionRange Version { get; init; }
    /// <summary>The member's priority: lower comes first in the candidate budget and wins ties. 0 to 10,000; 100 by default.</summary>
    public int Priority { get; init; } = 100;
    /// <summary>Whether the member starts on; a host may switch any member either way.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>A composite's entry point: it adds its member modules (KP-composite). Lives in the composite's assembly with its manifest.</summary>
[Experimental("AMEXT001")]
public interface IAgentMemoryComposite
{
    /// <summary>Adds every member the composite names; the host's choices decide which are composed.</summary>
    void AddMembers(CompositeMembers members);
}

/// <summary>What a host may change in a composite: switch a member, or move it in the stack.</summary>
[Experimental("AMEXT001")]
public sealed class CompositeOptions
{
    internal Dictionary<string, bool> Switches { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> Priorities { get; } = new(StringComparer.Ordinal);

    /// <summary>Composes a member the composite starts off.</summary>
    public CompositeOptions Enable(string memberId) { Switches[memberId] = true; return this; }
    /// <summary>Leaves a member out.</summary>
    public CompositeOptions Disable(string memberId) { Switches[memberId] = false; return this; }
    /// <summary>Overrides a member's priority, 0 to 10,000.</summary>
    public CompositeOptions Prioritize(string memberId, int priority)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 10_000);
        Priorities[memberId] = priority;
        return this;
    }
}

/// <summary>
/// How a composite adds its members (KP-composite): each must be named in its manifest, within the version range; a member
/// switched off is skipped; every member added takes its priority. There is no way to add a composite here, by design.
/// </summary>
[Experimental("AMEXT001")]
public sealed class CompositeMembers
{
    private readonly ModuleSetBuilder _builder;
    private readonly CompositeManifest _composite;
    private readonly CompositeOptions _options;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    internal CompositeMembers(ModuleSetBuilder builder, CompositeManifest composite, CompositeOptions options)
    {
        _builder = builder; _composite = composite; _options = options;
    }

    /// <summary>Adds a code module member from its assembly.</summary>
    public CompositeMembers Add<TModule>() where TModule : IAgentMemoryModule, new()
    {
        var manifest = ModuleManifest.Load(typeof(TModule).Assembly);
        if (Admit(manifest) is { } priority) _builder.Add<TModule>().Prioritize(manifest.Id, priority);
        return this;
    }

    /// <summary>Adds a verified knowledge pack member.</summary>
    public CompositeMembers AddKnowledgePack(Packs.KnowledgePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (Admit(pack.Manifest) is { } priority) _builder.AddKnowledgePack(pack, priority).Prioritize(pack.Id, priority);
        return this;
    }

    // The member's priority when it is composed; null when it is switched off. Refuses what the manifest does not name.
    private int? Admit(ModuleManifest manifest)
    {
        var member = _composite.Members.SingleOrDefault(m => m.Id == manifest.Id)
            ?? throw new InvalidOperationException($"Composite '{_composite.Id}' adds '{manifest.Id}', which its manifest does not name.");
        if (!member.Version.Contains(manifest.Version))
            throw new InvalidOperationException($"Composite '{_composite.Id}' accepts {member.Id} {member.Version}, not {manifest.Version}.");
        _seen.Add(member.Id);
        var enabled = _options.Switches.TryGetValue(member.Id, out var on) ? on : member.Enabled;
        return enabled ? (_options.Priorities.TryGetValue(member.Id, out var p) ? p : member.Priority) : null;
    }

    internal void Complete()
    {
        var missing = _composite.Members.Select(m => m.Id).Where(id => !_seen.Contains(id)).ToList();
        if (missing.Count != 0) throw new InvalidOperationException($"Composite '{_composite.Id}' did not add {string.Join(", ", missing)}.");
        var unknown = _options.Switches.Keys.Concat(_options.Priorities.Keys).Where(id => _composite.Members.All(m => m.Id != id)).Distinct().ToList();
        if (unknown.Count != 0) throw new InvalidOperationException($"Composite '{_composite.Id}' has no member {string.Join(", ", unknown)}.");
    }
}
