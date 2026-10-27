using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The host's memory of every pack it ever installed (v1.1 H4), kept apart from the store so that removing a pack does not
/// forget it: per id the highest version, and per version the one digest it had. It refuses a downgrade and a known version
/// arriving with other content, unless the operator explicitly allows a downgrade.
/// </summary>
internal sealed class HostLedger
{
    internal const string Format = "agentmemory.pack.ledger/1.1";
    private readonly Dictionary<string, (string Highest, Dictionary<string, string> Digests)> _packs = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (DateTimeOffset At, string Digest)> _revocations = new(StringComparer.Ordinal);

    /// <summary>
    /// Per revoker key, when the newest revocation document it signed that this host applied was issued (red team 8, third
    /// review): an older one from that key is refused, and while the key is trusted as revoker, verifying needs that list or
    /// a newer one applied, so leaving the list out cannot roll the host back either.
    /// </summary>
    internal IReadOnlyDictionary<string, (DateTimeOffset At, string Digest)> RevocationMarks => _revocations;

    /// <summary>
    /// Records an applied revocation document for a key that signed it: its issue time and its digest, so another list issued in
    /// the same second (lists are written to the second) counts as older (fourth review). The ledger keeps the latest.
    /// </summary>
    internal void RecordRevocations(string revokerKeyId, DateTimeOffset issuedAt, string digest)
    {
        if (!_revocations.TryGetValue(revokerKeyId, out var seen) || issuedAt > seen.At) _revocations[revokerKeyId] = (issuedAt, digest);
    }

    /// <summary>Why installing this pack must be refused, or null.</summary>
    internal string? Refusal(string id, string version, string digest, bool allowDowngrade = false)
    {
        if (!_packs.TryGetValue(id, out var known)) return null;
        if (!SemVer.TryParse(version, out var incoming)) return $"'{version}' is not a SemVer version.";
        foreach (var (seen, seenDigest) in known.Digests)
            if (SemVer.TryParse(seen, out var previous) && previous.CompareTo(incoming) == 0 && seenDigest != digest)
                return $"{id} {version} was installed before with other content ({Dsse.Short(seenDigest)}…); a version names one content for ever.";
        if (!allowDowngrade && SemVer.TryParse(known.Highest, out var highest) && incoming.CompareTo(highest) < 0)
            return $"{id} {version} is older than {known.Highest}, which this host installed before (a downgrade).";
        return null;
    }

    internal void Record(string id, string version, string digest)
    {
        if (!_packs.TryGetValue(id, out var known)) _packs[id] = known = (version, new Dictionary<string, string>(StringComparer.Ordinal));
        known.Digests[version] = digest;
        if (SemVer.TryParse(version, out var incoming) && SemVer.TryParse(known.Highest, out var highest) && incoming.CompareTo(highest) > 0)
            _packs[id] = (version, known.Digests);
    }

    internal static HostLedger Load(string path)
    {
        if (!File.Exists(path)) return new HostLedger();
        try { return LoadChecked(path); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            throw new InvalidDataException($"The ledger at {path} is malformed: {e.Message}", e);
        }
    }

    private static HostLedger LoadChecked(string path)
    {
        var ledger = new HostLedger();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.TryGetProperty("format", out var format) || format.GetString() != Format)
            throw new InvalidDataException($"The ledger at {path} is not a '{Format}' file.");
        // A field this reader does not know (an older mark's spelling, say) refuses the ledger: dropping it would forget history.
        if (document.RootElement.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => n is not ("format" or "packs" or "revocations")) is { } unknown)
            throw new InvalidDataException($"The ledger at {path} has a field this host does not know ('{unknown}'); it is not read rather than half read.");
        foreach (var pack in document.RootElement.GetProperty("packs").EnumerateObject())
        {
            var digests = pack.Value.GetProperty("versions").EnumerateObject().ToDictionary(v => v.Name, v => v.Value.GetString()!, StringComparer.Ordinal);
            ledger._packs[pack.Name] = (pack.Value.GetProperty("highest").GetString()!, digests);
        }
        if (document.RootElement.TryGetProperty("revocations", out var marks))
            foreach (var mark in marks.EnumerateObject())
                ledger._revocations[mark.Name] = mark.Value.ValueKind == JsonValueKind.Object
                    && mark.Value.TryGetProperty("issuedAt", out var issued) && issued.ValueKind == JsonValueKind.String && PackTime.TryParse(issued.GetString(), out var at)
                    && mark.Value.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String
                    ? (at, digest.GetString()!) : throw new InvalidDataException($"The ledger at {path} has a malformed revocation mark.");
        return ledger;
    }

    internal void Save(string path)
    {
        var packs = new JsonObject();
        foreach (var (id, (highest, digests)) in _packs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var versions = new JsonObject();
            foreach (var (version, digest) in digests.OrderBy(d => d.Key, StringComparer.Ordinal)) versions[version] = digest;
            packs[id] = new JsonObject { ["highest"] = highest, ["versions"] = versions };
        }
        var root = new JsonObject { ["format"] = Format, ["packs"] = packs };
        if (_revocations.Count > 0)
        {
            var marks = new JsonObject();
            // Full precision: a mark rounded down would let an older document issued in the same second through.
            foreach (var (keyId, (at, digest)) in _revocations.OrderBy(m => m.Key, StringComparer.Ordinal))
                marks[keyId] = new JsonObject
                {
                    ["issuedAt"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture), ["digest"] = digest,
                };
            root["revocations"] = marks;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Jcs.Canonical(root));
        File.Move(temporary, path, overwrite: true);
    }
}
