using System.Text.Json;

namespace AgentMemory.RouterArena.Data;

/// <summary>The recall section an item came from.</summary>
public enum RecallSection { Facts, Entities, Relationships, Preferences, RelevantMessages, RecentMessages, Traces, Due, Expiring }

/// <summary>
/// The matrix world's memories by id (<c>world-index.json</c>), and the map from what recall returns (a fact's triple, a
/// name, a relationship line, a preference or message text) back to those ids. Two people can hold the same key ("Lyon"):
/// the recorded owner decides whose it is, and another person's memory is an isolation breach (ids starting with X).
/// The store resolves a fact's subject to the person ("Marta" becomes "Marta Ruiz"), so both sides are compared with every
/// alias replaced by the full name.
/// </summary>
public sealed class WorldIndex
{
    private readonly Dictionary<(string Kind, string Key), Dictionary<bool, string>> _byKey = [];
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _refs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Door> _doors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);

    public WorldIndex(IEnumerable<(string Id, string Kind, string Ref, string Note)> memories, IReadOnlyDictionary<string, IReadOnlyList<string>> aliases, string owner)
    {
        Owner = owner;
        foreach (var (name, names) in aliases)
            foreach (var alias in names) _aliases[Norm(alias)] = Norm(name);
        foreach (var (id, kind, reference, note) in memories)
        {
            _refs[id] = reference;
            _notes[id] = note;
            _doors[id] = kind switch
            {
                "entity" or "relationship" => Door.EntityGraph,
                "preference" => Door.Preference,
                "message" => Door.Episodic,
                "trace" => note.StartsWith("procedure", StringComparison.Ordinal) ? Door.Procedural : Door.Reasoning,
                _ => note.StartsWith("derived", StringComparison.Ordinal) ? Door.Derived : Door.Semantic,
            };
            var key = (kind, Canonical(kind, reference));
            if (!_byKey.TryGetValue(key, out var owners)) _byKey[key] = owners = [];
            owners[id.StartsWith('X')] = id;
        }
    }

    /// <summary>The matrix's person (store owner ids end with <c>-{Owner}</c>).</summary>
    public string Owner { get; }

    /// <summary>The memory's text in the world (a triple, a name, a relationship line).</summary>
    public string RefOf(string id) => _refs.TryGetValue(id, out var r) ? r : id;

    /// <summary>What the world says about a memory beside its text: its dates ("validFrom …, validUntil …"), a trace's outcome.</summary>
    public string NoteOf(string id) => _notes.GetValueOrDefault(id, "");

    /// <summary>
    /// The door that holds a memory by what it is: a fact is semantic (derived when the accountant computed it), a trace
    /// procedural or reasoning. The reading doors (temporal, bi-temporal, prospective, forgetting) hold nothing of their
    /// own: they read facts another way. Unknown ids are semantic.
    /// </summary>
    public Door DoorOf(string id) => _doors.GetValueOrDefault(id, Door.Semantic);

    /// <summary>The memory id a recalled item is, or an id starting with <c>?</c> when the world does not hold it.</summary>
    public string IdOf(RecallSection section, Hit hit)
    {
        var kind = section switch
        {
            RecallSection.Facts or RecallSection.Due or RecallSection.Expiring => "fact",
            RecallSection.Entities => "entity", RecallSection.Relationships => "relationship",
            RecallSection.Preferences => "preference", RecallSection.Traces => "trace", _ => "message",
        };
        _byKey.TryGetValue((kind, Canonical(kind, hit.Key)), out var owners);
        var foreign = hit.Owner is not null && !hit.Owner.EndsWith($"-{Owner}", StringComparison.Ordinal);
        string? id = null;
        if (owners is not null)
            id = foreign ? owners.GetValueOrDefault(true) : owners.GetValueOrDefault(false) ?? owners.GetValueOrDefault(true);
        return id ?? (foreign ? $"X?{section}:{hit.Key}" : $"?{section}:{hit.Key}");
    }

    private string Canonical(string kind, string text)
    {
        var key = Norm(text);
        if (kind != "fact") return key;
        var parts = key.Split(" | ");
        if (parts.Length != 3) return key;
        parts[0] = _aliases.GetValueOrDefault(parts[0], parts[0]);
        parts[2] = _aliases.GetValueOrDefault(parts[2], parts[2]);
        return string.Join(" | ", parts);
    }

    internal static string Norm(string? text) => string.Join(' ', (text ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Reads the index and the aliases from the world pack beside it (<c>world.pack.json</c>, else the one <c>*.pack.json</c>
    /// there: 40.92, a second world).</summary>
    public static WorldIndex Read(string indexPath, string owner = "marta")
    {
        using var index = JsonDocument.Parse(File.ReadAllBytes(indexPath));
        var memories = index.RootElement.EnumerateArray()
            .Select(m => (m.GetProperty("id").GetString()!, m.GetProperty("kind").GetString()!, m.GetProperty("ref").GetString()!,
                m.TryGetProperty("note", out var note) ? note.GetString() ?? "" : ""))
            .ToList();
        var aliases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var folder = Path.GetDirectoryName(Path.GetFullPath(indexPath))!;
        var packPath = Path.Combine(folder, "world.pack.json");
        if (!File.Exists(packPath) && Directory.GetFiles(folder, "*.pack.json") is [var only])
            packPath = only;
        if (File.Exists(packPath))
        {
            using var pack = JsonDocument.Parse(File.ReadAllBytes(packPath));
            foreach (var session in pack.RootElement.GetProperty("sessions").EnumerateArray())
                foreach (var message in session.GetProperty("messages").EnumerateArray())
                    if (message.TryGetProperty("entities", out var entities))
                        foreach (var entity in entities.EnumerateArray())
                            if (entity.TryGetProperty("aliases", out var names))
                                aliases[entity.GetProperty("name").GetString()!] = [.. names.EnumerateArray().Select(n => n.GetString()!)];
        }
        return new WorldIndex(memories, aliases, owner);
    }
}
