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
/// A member may itself be a composite (PACK-nest), at most <see cref="MaximumDepth"/> composites deep.
/// </summary>
/// <remarks>
/// <b>A family is a hierarchy (CODING-hierarchy).</b> A member may say which other members it contains: decisions contain the
/// work done under them and the code they govern, work contains the code it changed. Containment is many-to-many (one work
/// item serves two decisions), never a cycle, and it is what lets packs link across: a member may depend on, and so link to
/// the types of, only the members it contains. The composite stays the unit a host installs; the hierarchy says what the
/// members mean to each other, and a walk that follows such a link moves down it or back up.
/// </remarks>
[Experimental("AMEXT001")]
public sealed record CompositeManifest
{
    /// <summary>The wire format.</summary>
    public const string FormatName = "agentmemory.composite/v1alpha1";
    /// <summary>Most members one composite names.</summary>
    public const int MaximumMembers = 16;
    /// <summary>
    /// Most composites on any path from the outermost one down to a module (PACK-nest): a family of families of families of
    /// families, and no deeper, so a host can always show and switch the whole tree.
    /// </summary>
    public const int MaximumDepth = 4;
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
        Object(root, "$", [.. PackRegistry.CompositeFields]);
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

    /// <summary>The members no other member contains, in member order: the top of the hierarchy.</summary>
    public IReadOnlyList<string> Roots => [.. Members.Where(m => Members.All(o => !o.Contains.Contains(m.Id, StringComparer.Ordinal))).Select(m => m.Id)];

    /// <summary>The members a member contains, as it lists them.</summary>
    public IReadOnlyList<string> Children(string memberId) => Members.FirstOrDefault(m => m.Id == memberId)?.Contains ?? [];

    /// <summary>The members that contain a member, in member order.</summary>
    public IReadOnlyList<string> Parents(string memberId) =>
        [.. Members.Where(m => m.Contains.Contains(memberId, StringComparer.Ordinal)).Select(m => m.Id)];

    /// <summary>How far a member sits below the top: 0 for a root, else one more than its deepest parent.</summary>
    public int Depth(string memberId)
    {
        var parents = Parents(memberId);
        return parents.Count == 0 ? 0 : 1 + parents.Max(Depth);
    }

    /// <summary>Every problem, in order: identity, version, members (identity, range, priority, duplicates, count, hierarchy).</summary>
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
        foreach (var member in Members)
            foreach (var child in member.Contains)
            {
                if (child == member.Id) problems.Add($"$.members.{member.Id}.contains: a member cannot contain itself.");
                else if (Members.All(m => m.Id != child)) problems.Add($"$.members.{member.Id}.contains: '{child}' is not a member.");
            }
        if (Cycle() is { } cycle) problems.Add("$.members: the hierarchy has a cycle: " + string.Join(" > ", cycle) + ".");
        return problems;
    }

    // The first containment cycle found, members in order and the first repeated at the end; null when there is none.
    private IReadOnlyList<string>? Cycle()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        IReadOnlyList<string>? Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 2) return null;
            if (state.GetValueOrDefault(id) == 1) return [.. path.Skip(path.IndexOf(id)), id];
            state[id] = 1; path.Add(id);
            foreach (var child in Children(id).Where(c => c != id && Members.Any(m => m.Id == c)))
                if (Visit(child) is { } found) return found;
            path.RemoveAt(path.Count - 1); state[id] = 2;
            return null;
        }
        foreach (var id in Members.Select(m => m.Id).Distinct(StringComparer.Ordinal))
            if (Visit(id) is { } cycle) return cycle;
        return null;
    }

    private static CompositeMember Member(JsonElement value, string path)
    {
        Object(value, path, [.. PackRegistry.MemberFields]);
        return new CompositeMember
        {
            Id = Text(value, "id", path), Version = Range(Text(value, "version", path), path + ".version"),
            Priority = value.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number && priority.TryGetInt32(out var p)
                ? p : value.TryGetProperty("priority", out _) ? throw Error(path + ".priority", "expected an integer") : 100,
            Enabled = !value.TryGetProperty("enabled", out _) || Boolean(value, "enabled", path),
            Contains = value.TryGetProperty("contains", out var contains) ? Strings(contains, path + ".contains") : [],
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
    /// <summary>
    /// The members this one contains in the family's hierarchy (CODING-hierarchy): it may depend on them and link to their
    /// types; no other member of the composite. Empty for a member at the bottom.
    /// </summary>
    public IReadOnlyList<string> Contains { get; init; } = [];
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
/// switched off is skipped; every member added takes its priority. A member may be a composite itself (PACK-nest): its
/// members take its slot's priority plus their own (at most 10,000), so they never come before the slot and keep their own
/// order within it; switching the slot off switches off everything under it; a module that two composites of the family
/// both name is composed once, from the first that adds it. At most <see cref="CompositeManifest.MaximumDepth"/> deep.
/// </summary>
[Experimental("AMEXT001")]
public sealed class CompositeMembers
{
    private readonly ModuleSetBuilder _builder;
    private readonly CompositeManifest _composite;
    private readonly CompositeOptions _options;
    private readonly IReadOnlyList<string> _chain;
    private readonly int _offset;
    private readonly bool _on;
    private readonly HashSet<string> _composed;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ModuleManifest Manifest, bool Enabled)> _added = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CompositeMembers Members, bool Enabled)> _nested = new(StringComparer.Ordinal);

    internal CompositeMembers(ModuleSetBuilder builder, CompositeManifest composite, CompositeOptions options)
        : this(builder, composite, options, [composite.Id], 0, true, new HashSet<string>(StringComparer.Ordinal))
    {
    }

    // A composite inside the family: the chain of composites above it and itself, the slot priority it adds to its members',
    // whether every slot above is on, and the modules the family composed so far.
    private CompositeMembers(ModuleSetBuilder builder, CompositeManifest composite, CompositeOptions options, IReadOnlyList<string> chain, int offset, bool on,
        HashSet<string> composed)
    {
        _builder = builder; _composite = composite; _options = options; _chain = chain; _offset = offset; _on = on; _composed = composed;
    }

    /// <summary>
    /// Adds a member that is itself a composite (PACK-nest), with a delegate that adds its members. The nested composite is
    /// validated like any; it must be named here within the version range, and the family may not nest deeper than
    /// <see cref="CompositeManifest.MaximumDepth"/> or contain a composite inside itself. A host's switches and priorities
    /// for this composite apply to the slot; the nested composite's own members keep their declared choices.
    /// </summary>
    /// <exception cref="InvalidOperationException">The nested composite is invalid, not named, out of range, too deep or a cycle.</exception>
    public CompositeMembers AddComposite(CompositeManifest composite, Action<CompositeMembers> addMembers)
    {
        ArgumentNullException.ThrowIfNull(composite);
        ArgumentNullException.ThrowIfNull(addMembers);
        if (composite.Validate() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        var member = _composite.Members.SingleOrDefault(m => m.Id == composite.Id)
            ?? throw new InvalidOperationException($"Composite '{_composite.Id}' adds '{composite.Id}', which its manifest does not name.");
        if (!member.Version.Contains(composite.Version))
            throw new InvalidOperationException($"Composite '{_composite.Id}' accepts {member.Id} {member.Version}, not {composite.Version}.");
        if (_chain.Contains(composite.Id, StringComparer.Ordinal))
            throw new InvalidOperationException($"Composite '{composite.Id}' contains itself: {string.Join(" > ", _chain)} > {composite.Id}.");
        if (_chain.Count + 1 > CompositeManifest.MaximumDepth)
            throw new InvalidOperationException($"{string.Join(" > ", _chain)} > {composite.Id} nests deeper than {CompositeManifest.MaximumDepth} composites.");
        _seen.Add(member.Id);
        var enabled = _on && (_options.Switches.TryGetValue(member.Id, out var on) ? on : member.Enabled);
        var slot = Math.Min(10_000, _offset + (_options.Priorities.TryGetValue(member.Id, out var p) ? p : member.Priority));
        var nested = new CompositeMembers(_builder, composite, new CompositeOptions(), [.. _chain, composite.Id], slot, enabled, _composed);
        addMembers(nested);
        nested.Complete();
        _nested[member.Id] = (nested, enabled);
        return this;
    }

    /// <summary>A code composite member from its assembly (PACK-nest): as <see cref="AddComposite(CompositeManifest, Action{CompositeMembers})"/>.</summary>
    public CompositeMembers AddComposite<TComposite>() where TComposite : IAgentMemoryComposite, new() =>
        AddComposite(CompositeManifest.Load(typeof(TComposite).Assembly), members => new TComposite().AddMembers(members));

    // Whether this composite, at any depth, holds the module.
    private bool Holds(string moduleId) => _added.ContainsKey(moduleId) || _nested.Values.Any(n => n.Members.Holds(moduleId));

    /// <summary>Adds a code module member from its assembly.</summary>
    public CompositeMembers Add<TModule>() where TModule : IAgentMemoryModule, new()
    {
        var manifest = ModuleManifest.Load(typeof(TModule).Assembly);
        if (Admit(manifest) is { } priority) _builder.Add<TModule>().Prioritize(manifest.Id, priority);
        return this;
    }

    /// <summary>Adds a member from its manifest and a configuration delegate, such as a definition pack, which runs no code.</summary>
    public CompositeMembers Add(ModuleManifest manifest, Action<IModuleBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(configure);
        if (Admit(manifest) is { } priority) _builder.Add(manifest, configure).Prioritize(manifest.Id, priority);
        return this;
    }

    /// <summary>Adds a verified definition pack member: declarations only, named in the fingerprint by its digest and signers.</summary>
    internal CompositeMembers AddDefinitionPack(ModuleManifest manifest, string digest, string signers, DateTimeOffset? validUntil = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Admit(manifest) is { } priority) _builder.AddDefinitionPack(manifest, digest, signers, validUntil: validUntil).Prioritize(manifest.Id, priority);
        return this;
    }

    /// <summary>Adds a verified knowledge pack member valid until <paramref name="validUntil"/>.</summary>
    internal CompositeMembers AddKnowledgePack(Packs.KnowledgePack pack, DateTimeOffset? validUntil)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (Admit(pack.Manifest) is { } priority) _builder.AddKnowledgePack(pack, priority, validUntil).Prioritize(pack.Id, priority);
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
        var enabled = _on && (_options.Switches.TryGetValue(member.Id, out var on) ? on : member.Enabled);
        _added[member.Id] = (manifest, enabled);
        // Off, or composed already through another composite of the family: nothing more to add.
        if (!enabled || !_composed.Add(member.Id)) return null;
        return Math.Min(10_000, _offset + (_options.Priorities.TryGetValue(member.Id, out var p) ? p : member.Priority));
    }

    internal void Complete()
    {
        var missing = _composite.Members.Select(m => m.Id).Where(id => !_seen.Contains(id)).ToList();
        if (missing.Count != 0) throw new InvalidOperationException($"Composite '{_composite.Id}' did not add {string.Join(", ", missing)}.");
        var unknown = _options.Switches.Keys.Concat(_options.Priorities.Keys).Where(id => _composite.Members.All(m => m.Id != id)).Distinct().ToList();
        if (unknown.Count != 0) throw new InvalidOperationException($"Composite '{_composite.Id}' has no member {string.Join(", ", unknown)}.");
        // A member depends on (and links to) only what it contains, and what it needs is composed with it. A module inside a
        // nested composite is contained through that composite.
        foreach (var (id, (manifest, enabled)) in _added.Where(a => a.Value.Enabled))
            foreach (var dependency in manifest.Modules.Keys)
            {
                if (_added.TryGetValue(dependency, out var direct))
                {
                    if (!_composite.Children(id).Contains(dependency, StringComparer.Ordinal))
                        throw new InvalidOperationException($"Composite '{_composite.Id}': {id} depends on {dependency}, which it does not contain.");
                    if (!direct.Enabled)
                        throw new InvalidOperationException($"Composite '{_composite.Id}': {id} needs {dependency}, which is switched off.");
                }
                else if (_nested.FirstOrDefault(n => n.Value.Members.Holds(dependency)) is { Key: { } slot, Value: var nested })
                {
                    if (!_composite.Children(id).Contains(slot, StringComparer.Ordinal))
                        throw new InvalidOperationException($"Composite '{_composite.Id}': {id} depends on {dependency} in {slot}, which it does not contain.");
                    if (!nested.Enabled)
                        throw new InvalidOperationException($"Composite '{_composite.Id}': {id} needs {dependency} in {slot}, which is switched off.");
                }
            }
    }
}
