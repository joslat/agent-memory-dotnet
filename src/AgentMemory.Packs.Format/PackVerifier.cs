using System.Text;

namespace AgentMemory.Packs.Format;

/// <summary>A pack that passed every check, with what the host needs next.</summary>
internal sealed record VerifiedPack(string Id, string Version, string Digest, PackManifest Manifest, IReadOnlyList<PackSignature> Signatures,
    IReadOnlyDictionary<string, byte[]> Files)
{
    /// <summary>What travels beside the pack in <c>META/attestations/</c>, shaped and bounded but not trusted (§16).</summary>
    internal IReadOnlyDictionary<string, byte[]> Attestations { get; init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
}

/// <summary>One step of a verification, as <c>ampack verify --explain</c> shows it: what it checked, whether it held, and the details.</summary>
internal sealed record PackStep(string Name, bool Passed, IReadOnlyList<string> Lines);

/// <summary>The verification result: the pack, or every reason it was refused.</summary>
internal sealed record PackVerification(VerifiedPack? Pack, IReadOnlyList<string> Problems)
{
    internal bool IsVerified => Pack is not null;
}

/// <summary>
/// Verifies a pack before anything in it is parsed into a store (v1.1 FIG. 2, step 200), in this order:
/// <list type="number">
/// <item>every path keeps the grammar;</item>
/// <item><c>META/files.json</c> is canonical and well formed;</item>
/// <item>the pack holds exactly the listed files, each with its length and SHA-256 (CRLF and Git LFS are named as causes);</item>
/// <item>every JSON file keeps the JSON profile (JSONL files too, read once the signatures hold);</item>
/// <item><c>pack.json</c> names the same id and version as the list;</item>
/// <item>the digest is not revoked and the revocations are fresh;</item>
/// <item>every required role has a valid signature from a key scoped to this id;</item>
/// <item>the host ledger allows this id, version and digest.</item>
/// </list>
/// Integrity failures stop before the signatures are read; everything else is reported together.
/// </summary>
internal static class PackVerifier
{
    // explain: when given, told the outcome of every step in order (ampack verify --explain); the verdict is the same.
    internal static PackVerification Verify(PackContent content, TrustPolicy policy, DateTimeOffset now, HostLedger? ledger = null, bool allowDowngrade = false,
        Action<PackStep>? explain = null)
    {
        // Record files are read only once the signatures hold: parsing is the costly step, linear in up to 256 MiB, so an
        // untrusted pack never gets it (review 1, L3). The verdict is the same either way.
        var (integrity, list, listBytes, manifest) = CheckIntegrity(content, explain, records: false);
        if (integrity.Count > 0) return new(null, integrity);
        var problems = new List<string>();
        // What this host revoked before still holds, whatever lists this run was given (fifth review).
        if (ledger is not null) policy = ledger.Apply(policy);

        var digest = FilesList.DigestOf(listBytes!);
        var lifetime = new List<string>();
        if (policy.RevokedDigests.Contains(digest)) lifetime.Add($"The pack {digest} is revoked.");
        if (manifest!.ValidUntil is { } until && until < now) lifetime.Add($"The pack was valid until {until:u}.");
        if (policy.RevocationsExpire is { } expires && expires < now) lifetime.Add($"The host's revocation list expired at {expires:u}; refresh it before verifying.");
        problems.AddRange(lifetime);
        explain?.Invoke(new("revocation and expiry", lifetime.Count == 0, lifetime.Count > 0 ? lifetime : [
            $"{digest} is not revoked" + (policy.RevocationsExpire is { } fresh ? $"; the revocation list is fresh until {fresh:u}" : ""),
            manifest.ValidUntil is { } validUntil ? $"valid until {validUntil:u}" : "no validUntil"]));

        var signatures = new List<PackSignature>();
        var junk = new List<string>();
        var envelopeProblems = new List<string>();
        var envelopes = content.Files.Keys.Where(PackPaths.IsSignature).Order(StringComparer.Ordinal).ToList();
        // Bounded: every envelope is checked against one hash of the files list (fifth review).
        if (envelopes.Count > Dsse.MaximumEnvelopes) junk.Add($"The pack carries {envelopes.Count} signature envelopes; at most {Dsse.MaximumEnvelopes}.");
        var listHex = FilesList.Hex(listBytes!);
        foreach (var file in envelopes.Take(Dsse.MaximumEnvelopes))
        {
            var (valid, invalid, malformed) = Dsse.Verify(file, content.Files[file], listHex, list!, policy, now);
            signatures.AddRange(valid);
            (malformed ? junk : envelopeProblems).AddRange(invalid);
        }
        problems.AddRange(envelopeProblems);
        var missingRoles = policy.RequiredRoles.Where(r => signatures.All(s => s.Role != r)).ToList();
        foreach (var role in missingRoles)
            problems.Add($"No valid {role} signature: a pack is refused unless a key the host trusts for '{list!.Id}' signed it as {role}.");
        // Invalid extra envelopes are reported, but they do not refuse a pack whose required signatures hold.
        if (missingRoles.Count == 0)
            problems.RemoveAll(p => p.StartsWith(PackPaths.SignaturesFolder, StringComparison.Ordinal));
        // A file that is not an envelope at all is unlisted content travelling with the pack: refused (PACK-review 1.9).
        problems.AddRange(junk);
        explain?.Invoke(new("signatures", missingRoles.Count == 0 && junk.Count == 0, [
            .. signatures.Select(s => $"counted: {s.Role} by key {Dsse.Short(s.KeyId)} at {s.SignedAt:u} ({s.File})"),
            .. envelopeProblems.Select(p => (missingRoles.Count == 0 ? "ignored (the required roles hold): " : "not counted: ") + p),
            .. junk.Select(p => "refuses the pack: " + p),
            .. missingRoles.Select(r => $"missing: no valid {r} signature from a key the policy trusts for '{list!.Id}'"),
            $"required roles: {string.Join(", ", policy.RequiredRoles)}"]));
        // A pack whose signatures do not hold is refused already: its records are not parsed.
        if (missingRoles.Count == 0 && junk.Count == 0)
        {
            var recordProblems = LineProblems(content).ToList();
            problems.AddRange(recordProblems);
            explain?.Invoke(new("records", recordProblems.Count == 0, recordProblems.Count > 0 ? recordProblems
                : ["every JSONL file: UTF-8, LF, one JSON value per line, no repeated key, NFC strings, integers within 2^53"]));
        }

        if (manifest.Federation is { } federation)
        {
            var federationProblems = FederationProblems(federation, signatures, policy);
            problems.AddRange(federationProblems);
            explain?.Invoke(new("federation", federationProblems.Count == 0, federationProblems.Count > 0 ? federationProblems : [
                $"published by {federation.Publisher}, signed by a key this host bound to that WebID",
                federation.IsPublic ? "for everyone" : $"for {federation.Audience!.Count} WebID(s); this host is one of them",
                .. federation.Purpose is { } purpose ? [$"purpose: {purpose}"] : Array.Empty<string>()]));
        }

        var ledgerRefusal = ledger?.Refusal(list!.Id, list.Version, digest, allowDowngrade);
        if (ledgerRefusal is not null) problems.Add(ledgerRefusal);
        if (ledger is not null)
            explain?.Invoke(new("ledger", ledgerRefusal is null, [ledgerRefusal ?? $"{list!.Id} {list.Version} with {digest[..23]}… is allowed by the ledger"]));
        return problems.Count > 0
            ? new(null, problems)
            : new(new VerifiedPack(list!.Id, list.Version, digest, manifest!, signatures,
                content.Files.Where(f => FilesList.IsListed(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal))
            {
                Attestations = content.Files.Where(f => PackPaths.IsAttestation(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal),
            }, []);
    }

    /// <summary>
    /// The federated profile (federated packs §3): the publisher WebID is the signer's, by the host's own binding, and the host
    /// is in the audience. A trusted key bound to another WebID cannot publish in this one's name.
    /// </summary>
    internal static List<string> FederationProblems(PackFederation federation, IReadOnlyList<PackSignature> signatures, TrustPolicy policy)
    {
        var problems = new List<string>();
        var publishers = signatures.Where(s => s.Role == "publisher").Select(s => policy.Find(s.KeyId)).OfType<TrustedKey>().ToList();
        foreach (var other in publishers.Where(k => k.WebId is not null && k.WebId != federation.Publisher))
            problems.Add($"The pack names publisher {federation.Publisher}, but key {Dsse.Short(other.KeyId)} that signed it as publisher is bound to {other.WebId}.");
        if (!publishers.Any(k => k.WebId == federation.Publisher))
            problems.Add($"No publisher signature from a key this host bound to {federation.Publisher}: a federated pack needs one (trust entry 'webId').");
        if (!federation.Admits(policy.HostWebIds))
            problems.Add(policy.HostWebIds.Count == 0
                ? "The pack is published for named WebIDs, and this host names none of its own (trust policy 'host.webIds')."
                : $"The pack is published for {PackFederation.Describe(federation.Audience)}; this host is {string.Join(", ", policy.HostWebIds)}.");
        return problems;
    }

    /// <summary>Steps 1–5: everything that needs no trust. <c>ampack lint</c> and <c>build</c> use it alone.</summary>
    internal static (List<string> Problems, FilesList? List, byte[]? ListBytes, PackManifest? Manifest) CheckIntegrity(PackContent content,
        Action<PackStep>? explain = null, bool records = true)
    {
        var problems = PackPaths.Problems(content.Files.Keys).ToList();
        explain?.Invoke(new("paths", problems.Count == 0, problems.Count > 0 ? problems : [$"{content.Files.Count} files, every path in the grammar"]));
        if (problems.Count > 0) return (problems, null, null, null);
        if (!content.Files.TryGetValue(PackPaths.FilesList, out var listBytes))
        {
            explain?.Invoke(new("files list", false, [$"There is no {PackPaths.FilesList}: build the pack first."]));
            return ([$"There is no {PackPaths.FilesList}: build the pack first."], null, null, null);
        }
        var (list, listProblem) = FilesList.Parse(listBytes);
        explain?.Invoke(new("files list", list is not null, list is null ? [listProblem!]
            : [$"{list.Id} {list.Version}, {list.Files.Count} files; RFC 8785 canonical", $"digest {FilesList.DigestOf(listBytes)}"]));
        if (list is null) return ([listProblem!], null, null, null);

        var listed = list.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var path in content.Files.Keys.Where(p => FilesList.IsListed(p) && !listed.ContainsKey(p)).Order(StringComparer.Ordinal))
            problems.Add($"'{path}' is not in {PackPaths.FilesList}.");
        foreach (var file in list.Files)
        {
            if (!content.Files.TryGetValue(file.Path, out var bytes)) { problems.Add($"'{file.Path}' is listed but missing."); continue; }
            if (bytes.LongLength == file.Length && FilesList.Hex(bytes) == file.Sha256) continue;
            problems.Add($"'{file.Path}' does not match {PackPaths.FilesList}{Cause(bytes, file)}.");
        }
        // Beside the listed files, only signatures and attestations, each shaped and bounded (§16).
        problems.AddRange(PackAttestations.Problems(content.Files));
        explain?.Invoke(new("content", problems.Count == 0, problems.Count > 0 ? problems
            : [$"exactly the listed files, each with its length and SHA-256 ({list.Files.Sum(f => f.Length):N0} bytes)"]));
        if (problems.Count > 0) return (problems, null, null, null);

        foreach (var (path, bytes) in content.Files.Where(f => FilesList.IsListed(f.Key) && f.Key.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(f => f.Key, StringComparer.Ordinal))
            if (PackJson.Check(bytes, integersOnly: path == PackPaths.LockFile) is { } problem) problems.Add($"'{path}' {problem}.");
        if (records) problems.AddRange(LineProblems(content));
        explain?.Invoke(new("JSON profile", problems.Count == 0, problems.Count > 0 ? problems
            : [records ? "every JSON and JSONL file: UTF-8, LF, no repeated key, NFC strings, integers within 2^53"
                : "every JSON file: UTF-8, LF, no repeated key, NFC strings, integers within 2^53 (JSONL files once the signatures hold)"]));
        var profileProblems = problems.Count;
        if (!content.Files.TryGetValue(PackManifest.FileName, out var manifestBytes))
            problems.Add($"There is no {PackManifest.FileName}.");
        else
        {
            var (manifest, manifestProblem) = PackManifest.Parse(manifestBytes);
            if (manifest is null) problems.Add(manifestProblem!);
            else if (manifest.Id != list.Id || manifest.Version != list.Version)
                problems.Add($"{PackManifest.FileName} names {manifest.Id} {manifest.Version}, but {PackPaths.FilesList} names {list.Id} {list.Version}.");
            else
            {
                problems.AddRange(KindProblems(manifest, content.Files));
                explain?.Invoke(new("pack.json and kind", problems.Count == profileProblems, problems.Count > profileProblems ? problems.Skip(profileProblems).ToList()
                    : [$"{manifest.Kind} pack, tier {manifest.Tier}, protection {manifest.Protection}, code {manifest.Code}"]));
                if (problems.Count == 0) return (problems, list, listBytes, manifest);
                return (problems, null, null, null);
            }
        }
        explain?.Invoke(new("pack.json and kind", false, problems.Skip(profileProblems).ToList()));
        return (problems, null, null, null);
    }

    // Every listed JSONL file, line by line, in the JSON profile.
    private static IEnumerable<string> LineProblems(PackContent content) =>
        content.Files.Where(f => FilesList.IsListed(f.Key) && f.Key.EndsWith(".jsonl", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => PackJson.CheckLines(f.Value) is { } problem ? $"'{f.Key}' {problem}." : null).OfType<string>();

    /// <summary>What each kind must and must not hold (v1.2 §3).</summary>
    private static IEnumerable<string> KindProblems(PackManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        var records = PackLayout.RecordFiles(files.Keys).Count;
        var all = files.Keys.ToList();
        switch (manifest.Kind)
        {
            case "knowledge" or "snapshot" when records == 0:
                yield return $"A {manifest.Kind} pack holds records under {PackLayout.Records}; this one holds none.";
                break;
            case "definition" when records > 0:
                yield return $"A definition pack declares only; its records come from the host's importer, not from {PackLayout.Records}.";
                break;
            case "composite" when !all.Contains(PackPaths.LockFile) || manifest.Contains.Count == 0:
                yield return $"A composite names what it contains and pins it in {PackPaths.LockFile}.";
                break;
            case "set" when !all.Any(p => p.StartsWith("packs/", StringComparison.Ordinal)):
                yield return "A pack set carries its members under packs/.";
                break;
        }
        // Review 1, L6: a composite pins packs and holds no records of its own (spec §3).
        if (manifest.Kind == "composite" && records > 0) yield return $"A composite pack holds no records of its own under {PackLayout.Records}.";
        if (manifest.Kind == "composite" && all.Contains(PackPaths.LockFile))
            foreach (var problem in CompositeProblems(manifest, files)) yield return problem;
        if (manifest.Protection != "open" && !all.Contains("META/seal.json"))
            yield return $"A pack marked {manifest.Protection} must carry META/seal.json.";
        // The pack card is read strictly by every verifier, so it shows the same everywhere (§15).
        if (files.TryGetValue(PackCard.FileName, out var card) && PackCard.Parse(card).Problem is { } cardProblem) yield return cardProblem;
    }

    /// <summary>A composite names the same members in pack.json, members.json and its lock (v1.2 §5).</summary>
    private static IEnumerable<string> CompositeProblems(PackManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        var (entries, lockProblem) = PackLock.Parse(files[PackPaths.LockFile]);
        if (entries is null) { yield return lockProblem!; yield break; }
        if (!files.TryGetValue(PackLayout.Members, out var membersBytes)) { yield return $"A composite pack carries {PackLayout.Members}."; yield break; }
        if (MemberIds(membersBytes) is not { } named) { yield return $"{PackLayout.Members} is malformed."; yield break; }
        var contains = manifest.Contains.Order(StringComparer.Ordinal).ToList();
        if (!named.Order(StringComparer.Ordinal).SequenceEqual(contains))
            yield return $"{PackLayout.Members} and {PackManifest.FileName} name different members.";
        if (!entries.Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(contains))
            yield return $"{PackPaths.LockFile} pins other members than the composite contains.";
    }

    private static readonly HashSet<string> MemberFields = new(StringComparer.Ordinal) { "id", "version", "priority", "enabled", "contains" };

    // A member's values typed as the loader reads them (review 3): an id and a version range as text, a 32-bit priority, a
    // Boolean switch, and a list of ids it contains.
    private static bool IsMember(System.Text.Json.JsonElement m)
    {
        static bool Text(System.Text.Json.JsonElement e) => e.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(e.GetString());
        return m.TryGetProperty("id", out var id) && Text(id)
            && m.TryGetProperty("version", out var version) && Text(version)
            && (!m.TryGetProperty("priority", out var priority) || priority.ValueKind == System.Text.Json.JsonValueKind.Number && priority.TryGetInt32(out _))
            && (!m.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            && (!m.TryGetProperty("contains", out var contains)
                || contains.ValueKind == System.Text.Json.JsonValueKind.Array && contains.EnumerateArray().All(Text));
    }

    private static string[]? MemberIds(byte[] membersBytes)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(membersBytes);
            // Review 1, L6: members.json holds its members and nothing else, as every reader takes it (spec §2).
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || document.RootElement.EnumerateObject().Any(p => p.Name != "members")) return null;
            var members = document.RootElement.GetProperty("members").EnumerateArray().ToList();
            // Each member holds what a composite member is and nothing else, as the loader reads it (review 2, L6).
            if (members.Any(m => m.ValueKind != System.Text.Json.JsonValueKind.Object || m.EnumerateObject().Any(p => !MemberFields.Contains(p.Name)) || !IsMember(m)))
                return null;
            return [.. members.Select(m => m.GetProperty("id").GetString()!)];
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// A federated set's members (federated packs §3): each is federated itself, by the same publisher, for an audience within
    /// the set's. Otherwise a member taken out of the set would load on a host the set was never published for (FED-review M5).
    /// </summary>
    internal static string? MemberFederationProblem(PackFederation set, VerifiedPack member)
    {
        if (member.Manifest.Federation is not { } own)
            return $"The member {member.Id} of a federated set is not federated itself: out of the set it would reach any host.";
        if (own.Publisher != set.Publisher) return $"The member {member.Id} names publisher {own.Publisher}, not the set's {set.Publisher}.";
        if (!set.IsPublic && (own.Audience is null || own.Audience.Any(a => !set.Audience!.Contains(a))))
            return $"The member {member.Id} is published for a wider audience than its set.";
        return null;
    }

    /// <summary>
    /// A pack verified in full: a single pack as <see cref="Verify"/> does, a set together with every member it carries. What
    /// fetches and receipts rely on, so a set is never taken on its outer signature alone (FED-review H2).
    /// </summary>
    internal static PackVerification VerifyWhole(PackContent content, TrustPolicy policy, DateTimeOffset now, HostLedger? ledger = null, bool allowDowngrade = false)
    {
        var first = Verify(content, policy, now, ledger, allowDowngrade);
        if (first.Pack is not { Manifest.Kind: "set" }) return first;
        var (set, members) = VerifySet(content, policy, now, ledger, allowDowngrade, first);
        var refused = members.Where(m => !m.Value.IsVerified).OrderBy(m => m.Key, StringComparer.Ordinal)
            .SelectMany(m => m.Value.Problems.Select(p => $"packs/{m.Key}/: {p}")).ToList();
        return !set.IsVerified ? set : refused.Count > 0 ? new PackVerification(null, refused) : set;
    }

    /// <summary>
    /// A pack set and each of its members, verified independently: the set's own signature binds the members together, and
    /// each member keeps its own list and signatures (v1.2 §5).
    /// </summary>
    internal static (PackVerification Set, IReadOnlyDictionary<string, PackVerification> Members) VerifySet(PackContent content, TrustPolicy policy,
        DateTimeOffset now, HostLedger? ledger = null, bool allowDowngrade = false, PackVerification? verified = null)
    {
        // The ledger's kept revocations once for the whole set, not again for each member (sixth review).
        if (ledger is not null) policy = ledger.Apply(policy);
        // The set as a whole, verified once: a caller that already did passes its result (review 1, I2).
        var set = verified ?? Verify(content, policy, now, ledger, allowDowngrade);
        var members = new Dictionary<string, PackVerification>(StringComparer.Ordinal);
        if (set.Pack is not { } outer) return (set, members);
        if (outer.Manifest.Kind != "set") return (new PackVerification(null, [$"{outer.Id} is a {outer.Manifest.Kind} pack, not a set."]), members);
        Dictionary<string, Dictionary<string, byte[]>> split;
        try { split = PackSets.Members(content.Files); }
        catch (InvalidDataException e) { return (new PackVerification(null, [e.Message]), members); }
        if (!split.Keys.Order(StringComparer.Ordinal).SequenceEqual(outer.Manifest.Contains.Order(StringComparer.Ordinal)))
            return (new PackVerification(null, ["The set's pack.json names other members than it carries."]), members);
        // Review 1, L5: a set carries packs, not sets (a set inside a set would leave its own members unchecked here), and no
        // member shares the set's id (the two would collide in a host's ledger).
        if (split.ContainsKey(outer.Id)) return (new PackVerification(null, [$"The set {outer.Id} carries a member with its own id."]), members);
        foreach (var (id, files) in split)
        {
            var member = Verify(new PackContent(files, content.Source + "#" + id), policy, now, ledger, allowDowngrade);
            members[id] = member.Pack is { } carried && carried.Id != id
                ? new PackVerification(null, [$"The member in packs/{id}/ is {carried.Id}."])
                : member.Pack is { Manifest.Kind: "set" }
                    ? new PackVerification(null, [$"packs/{id}/ is a pack set: a set carries packs, not sets."])
                : member.Pack is { } inner && outer.Manifest.Federation is { } federation && MemberFederationProblem(federation, inner) is { } problem
                    ? new PackVerification(null, [problem]) : member;
        }
        return (set, members);
    }

    /// <summary>The usual reasons a checked-out pack stops matching its list.</summary>
    private static string Cause(byte[] bytes, ListedFile file)
    {
        if (bytes.AsSpan().StartsWith("version https://git-lfs.github.com/spec/"u8))
            return " (it is a Git LFS pointer: fetch the real file, or keep packs out of LFS)";
        if (bytes.AsSpan().IndexOf("\r\n"u8) >= 0)
        {
            var lf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal));
            if (lf.LongLength == file.Length && FilesList.Hex(lf) == file.Sha256)
                return " (Git converted its line endings to CRLF: add '* -text -filter -diff' to the pack's .gitattributes)";
        }
        return "";
    }
}
