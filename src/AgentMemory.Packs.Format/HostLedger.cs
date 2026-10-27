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
    private (DateTimeOffset IssuedAt, DateTimeOffset Expires)? _freshness;
    private DateTimeOffset? _retired;
    private int _version;

    /// <summary>
    /// The newest revocation list this host applied while it was valid: when it was issued and when it expires. Verifying with
    /// the ledger is stale after that expiry unless a newer list is applied, so leaving lists out, or giving older ones, cannot
    /// keep the host fresh (sixth review). <c>ampack ledger --retire-revocations</c> clears it when a host stops using lists.
    /// </summary>
    internal (DateTimeOffset IssuedAt, DateTimeOffset Expires)? Freshness => _freshness;

    /// <summary>Changes each time something is recorded.</summary>
    internal int Version => _version;

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
        _version++;
        foreach (var digest in digests) _revokedDigests.Add(digest);
        foreach (var key in keys)
            _revokedKeys[key.KeyId] = _revokedKeys.TryGetValue(key.KeyId, out var known) && (known is null || key.CompromisedAt is { } later && later > known)
                ? known : key.CompromisedAt;
    }

    /// <summary>Keeps a list's freshness when it is the newest this host applied; of two issued in one second, the earlier expiry.</summary>
    internal void RecordFreshness(DateTimeOffset issuedAt, DateTimeOffset expires)
    {
        // A list issued no later than the mark that was retired does not bring freshness back (seventh review).
        if (_retired is { } floor && issuedAt <= floor) return;
        if (_freshness is { } known && (issuedAt < known.IssuedAt || issuedAt == known.IssuedAt && expires >= known.Expires)) return;
        _freshness = (issuedAt, expires);
        _version++;
    }

    /// <summary>Forgets the freshness mark, for a host that stops using revocation lists; what was revoked stays revoked.</summary>
    internal void RetireRevocationLists()
    {
        // A tombstone, not a blank: saving merges with the file, and a run that still holds the old mark must not restore it.
        if (_freshness is { } retired && (_retired is null || retired.IssuedAt > _retired)) _retired = retired.IssuedAt;
        _freshness = null;
        _version++;
    }

    /// <summary>The policy with every revocation this host kept added to it, and the newest list's freshness.</summary>
    internal TrustPolicy Apply(TrustPolicy policy)
    {
        if (ReferenceEquals(policy.KeptFrom, this) && policy.KeptVersion == _version) return policy;
        var keys = policy.RevokedKeys.ToDictionary(k => k.KeyId, k => k.CompromisedAt, StringComparer.Ordinal);
        foreach (var (keyId, at) in _revokedKeys)
            keys[keyId] = keys.TryGetValue(keyId, out var known) && (known is null || at is { } later && later > known) ? known : at;
        var applied = policy with
        {
            RevokedDigests = _revokedDigests.Count == 0 ? policy.RevokedDigests : [.. policy.RevokedDigests.Union(_revokedDigests, StringComparer.Ordinal)],
            RevokedKeys = [.. keys.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new RevokedKey(k.Key, k.Value))],
            KeptFrom = this, KeptVersion = _version,
        };
        return _freshness is { } fresh ? Revocations.Fresher(applied, fresh.IssuedAt, fresh.Expires) with { KeptFrom = this, KeptVersion = _version } : applied;
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
        _version++;
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
        var bytes = Retry(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
        // The profile refuses a repeated key, so no field is read twice with the last one winning (sixth review).
        if (PackJson.Check(bytes) is { } malformed) throw new InvalidDataException($"The ledger at {path} {malformed}.");
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("format", out var format) || format.GetString() != Format)
            throw new InvalidDataException($"The ledger at {path} is not a '{Format}' file.");
        // A field this reader does not know (an older mark's spelling, say) refuses the ledger: dropping it would forget history.
        if (document.RootElement.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => n is not ("format" or "packs" or "revoked" or "freshness" or "retired")) is { } unknown)
            throw new InvalidDataException(unknown == "revocations"
                ? $"The ledger at {path} holds revocation marks from an earlier preview build; delete its 'revocations' field and apply the current lists again."
                : $"The ledger at {path} has a field this host does not know ('{unknown}'); it is not read rather than half read.");
        foreach (var pack in document.RootElement.GetProperty("packs").EnumerateObject())
        {
            var digests = pack.Value.GetProperty("versions").EnumerateObject().ToDictionary(v => v.Name, v => v.Value.GetString()!, StringComparer.Ordinal);
            var highest = pack.Value.GetProperty("highest").GetString()!;
            // A stored version this host cannot read would stop it refusing downgrades for that id (sixth review): refused.
            if (!SemVer.TryParse(highest, out _) || digests.Keys.Any(v => !SemVer.TryParse(v, out _)))
                throw new InvalidDataException($"The ledger at {path} holds a version of {pack.Name} that is not SemVer; it is not read rather than half read.");
            ledger._packs[pack.Name] = (highest, digests);
        }
        if (document.RootElement.TryGetProperty("freshness", out var freshness))
            ledger._freshness = freshness.ValueKind == JsonValueKind.Object
                && freshness.TryGetProperty("issuedAt", out var issued) && issued.ValueKind == JsonValueKind.String && PackTime.TryParse(issued.GetString(), out var issuedAt)
                && freshness.TryGetProperty("expires", out var expires) && expires.ValueKind == JsonValueKind.String && PackTime.TryParse(expires.GetString(), out var expiresAt)
                ? (issuedAt, expiresAt) : throw new InvalidDataException($"The ledger at {path} has a malformed freshness mark.");
        if (document.RootElement.TryGetProperty("retired", out var retiredText))
            ledger._retired = retiredText.ValueKind == JsonValueKind.String && PackTime.TryParse(retiredText.GetString(), out var retiredAt)
                ? retiredAt : throw new InvalidDataException($"The ledger at {path} has a malformed retired mark.");
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

    /// <summary>
    /// Writes the ledger, merged with what another run saved meanwhile, under a lock: two runs at once never lose each other's
    /// revocations or installs (sixth review). Of two digests for one version, the one saved first stays.
    /// </summary>
    internal void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        using var guard = Lock(path + ".lock");
        if (File.Exists(path)) Merge(LoadChecked(path));
        Write(path);
    }

    private static FileStream Lock(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 200) throw new LedgerBusyException($"The ledger is locked by another run ({path}); try again.", e);
                Thread.Sleep(50);
            }
        }
    }

    // A file another process is reading or replacing at this moment, tried again a few times.
    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && e is not FileNotFoundException && attempt < 40) { Thread.Sleep(50); }
        }
    }

    private void Merge(HostLedger saved)
    {
        foreach (var (id, (highest, digests)) in saved._packs)
        {
            if (!_packs.TryGetValue(id, out var mine)) { _packs[id] = (highest, new Dictionary<string, string>(digests, StringComparer.Ordinal)); continue; }
            foreach (var (version, digest) in digests) mine.Digests[version] = digest;
            if (SemVer.TryParse(highest, out var theirs) && SemVer.TryParse(mine.Highest, out var ours) && theirs.CompareTo(ours) > 0)
                _packs[id] = (highest, mine.Digests);
        }
        Record(saved._revokedDigests, saved._revokedKeys.Select(k => new RevokedKey(k.Key, k.Value)));
        if (saved._retired is { } floor && (_retired is null || floor > _retired)) _retired = floor;
        if (_retired is { } retiredFloor && _freshness is { } held && held.IssuedAt <= retiredFloor) _freshness = null;
        if (saved._freshness is { } fresh) RecordFreshness(fresh.IssuedAt, fresh.Expires);
    }

    private void Write(string path)
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
        if (_freshness is { } fresh)
            root["freshness"] = new JsonObject
            {
                ["issuedAt"] = fresh.IssuedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["expires"] = fresh.Expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            };
        if (_retired is { } retiredMark)
            root["retired"] = retiredMark.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Jcs.Canonical(root));
        Retry(() => { File.Move(temporary, path, overwrite: true); return 0; });
    }
}
