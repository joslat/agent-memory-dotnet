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

    private readonly SortedSet<string> _revokedDigests = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, DateTimeOffset?> _revokedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Every revocation this host applied, kept for good (fifth review): a pack digest or a key once revoked here stays revoked
    /// whatever list a later run is given, an older one, none, or one whose revoker has since expired or been revoked itself. A
    /// list can only add. Verifying with the ledger applies them first.
    /// </summary>
    internal IReadOnlyCollection<string> RevokedDigests => _revokedDigests;

    /// <summary>Keys this host revoked, with the earliest compromise time it learned (null: revoked outright).</summary>
    internal IReadOnlyDictionary<string, DateTimeOffset?> RevokedKeys => _revokedKeys;

    /// <summary>Keeps what a revocation document revoked.</summary>
    internal void Record(IEnumerable<string> digests, IEnumerable<RevokedKey> keys)
    {
        foreach (var digest in digests) _revokedDigests.Add(digest);
        foreach (var key in keys)
            _revokedKeys[key.KeyId] = _revokedKeys.TryGetValue(key.KeyId, out var known) && (known is null || key.CompromisedAt is { } later && later > known)
                ? known : key.CompromisedAt;
    }

    /// <summary>The policy with every revocation this host kept added to it.</summary>
    internal TrustPolicy Apply(TrustPolicy policy)
    {
        if (_revokedDigests.Count == 0 && _revokedKeys.Count == 0) return policy;
        var keys = policy.RevokedKeys.ToDictionary(k => k.KeyId, k => k.CompromisedAt, StringComparer.Ordinal);
        foreach (var (keyId, at) in _revokedKeys)
            keys[keyId] = keys.TryGetValue(keyId, out var known) && (known is null || at is { } later && later > known) ? known : at;
        return policy with
        {
            RevokedDigests = [.. policy.RevokedDigests.Union(_revokedDigests, StringComparer.Ordinal)],
            RevokedKeys = [.. keys.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new RevokedKey(k.Key, k.Value))],
        };
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
        if (document.RootElement.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => n is not ("format" or "packs" or "revoked")) is { } unknown)
            throw new InvalidDataException($"The ledger at {path} has a field this host does not know ('{unknown}'); it is not read rather than half read.");
        foreach (var pack in document.RootElement.GetProperty("packs").EnumerateObject())
        {
            var digests = pack.Value.GetProperty("versions").EnumerateObject().ToDictionary(v => v.Name, v => v.Value.GetString()!, StringComparer.Ordinal);
            ledger._packs[pack.Name] = (pack.Value.GetProperty("highest").GetString()!, digests);
        }
        if (document.RootElement.TryGetProperty("revoked", out var revoked))
        {
            if (revoked.ValueKind != JsonValueKind.Object || !revoked.TryGetProperty("digests", out var digests) || digests.ValueKind != JsonValueKind.Array
                || !revoked.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"The ledger at {path} has a malformed revoked section.");
            foreach (var digest in digests.EnumerateArray())
                ledger._revokedDigests.Add(digest.ValueKind == JsonValueKind.String ? digest.GetString()! : throw new InvalidDataException($"The ledger at {path} has a malformed revoked digest."));
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object || !key.TryGetProperty("keyId", out var id) || id.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"The ledger at {path} has a malformed revoked key.");
                DateTimeOffset? at = null;
                if (key.TryGetProperty("compromisedAt", out var compromised))
                    at = compromised.ValueKind == JsonValueKind.String && PackTime.TryParse(compromised.GetString(), out var time)
                        ? time : throw new InvalidDataException($"The ledger at {path} has a malformed compromise time.");
                ledger._revokedKeys[id.GetString()!] = at;
            }
        }
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
        if (_revokedDigests.Count > 0 || _revokedKeys.Count > 0)
            root["revoked"] = new JsonObject
            {
                ["digests"] = new JsonArray([.. _revokedDigests.Select(d => (JsonNode)d)]),
                ["keys"] = new JsonArray([.. _revokedKeys.Select(k =>
                {
                    var key = new JsonObject { ["keyId"] = k.Key };
                    if (k.Value is { } at) key["compromisedAt"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                    return (JsonNode)key;
                })]),
            };
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Jcs.Canonical(root));
        File.Move(temporary, path, overwrite: true);
    }
}
