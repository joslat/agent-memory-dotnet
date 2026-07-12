using System.Reflection;
using System.Text.Json;

namespace AgentMemory.Neo4j.Schema.Parity;

/// <summary>
/// Registry of captured upstream <c>neo4j-agent-memory</c> schema snapshots, shipped as embedded
/// resources (<c>…Schema.Parity.Snapshots.{version}.json</c>). Each snapshot is the frozen reference for
/// one upstream release; adding a new version is a drop-in (embed the new <c>schema.json</c> + register a
/// <see cref="SchemaParityPolicy"/>). Parsing the rich capture down to a <see cref="SchemaDescriptor"/>
/// (labels / relationship types / property names) lives here so the verifier stays format-agnostic.
/// </summary>
internal sealed class UpstreamSchemaRegistry
{
    private const string ResourcePrefix = "AgentMemory.Neo4j.Schema.Parity.Snapshots.";
    private const string ResourceSuffix = ".json";

    private readonly Assembly _assembly;

    public UpstreamSchemaRegistry() : this(typeof(UpstreamSchemaRegistry).Assembly) { }

    internal UpstreamSchemaRegistry(Assembly assembly) => _assembly = assembly;

    /// <summary>
    /// The upstream versions for which a snapshot is embedded (e.g. "0.5.0"), ordered oldest → newest by
    /// <b>semantic</b> version (so the CLI default-to-newest <c>[^1]</c> picks the true latest — ordinal
    /// string sort would misorder "0.10.0" before "0.5.0").
    /// </summary>
    public IReadOnlyList<string> AvailableVersions =>
        OrderByVersion(
            _assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                .Select(n => n[ResourcePrefix.Length..^ResourceSuffix.Length]));

    /// <summary>Orders version strings oldest → newest by parsed semantic version (ties / unparseable fall back to ordinal).</summary>
    public static IReadOnlyList<string> OrderByVersion(IEnumerable<string> versions) =>
        versions
            .OrderBy(ParseVersion)
            .ThenBy(v => v, StringComparer.Ordinal)
            .ToList();

    private static Version ParseVersion(string v) =>
        Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0, 0);

    /// <summary>Loads and parses the snapshot for <paramref name="version"/>, or throws if it is not embedded.</summary>
    public SchemaDescriptor Load(string version)
    {
        var resource = ResourcePrefix + version + ResourceSuffix;
        using var stream = _assembly.GetManifestResourceStream(resource)
            ?? throw new KeyNotFoundException(
                $"No embedded upstream schema snapshot for version '{version}'. Available: {string.Join(", ", AvailableVersions)}.");
        return Parse(version, stream);
    }

    /// <summary>Parses an upstream <c>schema.json</c> capture into a comparable descriptor.</summary>
    public static SchemaDescriptor Parse(string version, Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var labels = root.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("label").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var relTypes = root.GetProperty("relationships").EnumerateArray()
            .Select(r => r.GetProperty("type").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in new[] { "nodes", "relationships" })
            foreach (var element in root.GetProperty(section).EnumerateArray())
                if (element.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
                    foreach (var name in props.EnumerateArray())
                        properties.Add(name.GetString()!);

        return new SchemaDescriptor(version, labels, relTypes, properties);
    }
}
