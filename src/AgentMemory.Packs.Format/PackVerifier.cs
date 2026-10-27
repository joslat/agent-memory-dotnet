using System.Text;

namespace AgentMemory.Packs.Format;

/// <summary>A pack that passed every check, with what the host needs next.</summary>
internal sealed record VerifiedPack(string Id, string Version, string Digest, PackManifest Manifest, IReadOnlyList<PackSignature> Signatures,
    IReadOnlyDictionary<string, byte[]> Files);

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
/// <item>every JSON and JSONL file keeps the JSON profile;</item>
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
        var (integrity, list, listBytes, manifest) = CheckIntegrity(content, explain);
        if (integrity.Count > 0) return new(null, integrity);
        var problems = new List<string>();

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
        foreach (var file in content.Files.Keys.Where(PackPaths.IsSignature).Order(StringComparer.Ordinal))
        {
            var (valid, invalid, malformed) = Dsse.Verify(file, content.Files[file], listBytes, list!, policy, now);
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

        var ledgerRefusal = ledger?.Refusal(list!.Id, list.Version, digest, allowDowngrade);
        if (ledgerRefusal is not null) problems.Add(ledgerRefusal);
        if (ledger is not null)
            explain?.Invoke(new("ledger", ledgerRefusal is null, [ledgerRefusal ?? $"{list!.Id} {list.Version} with {digest[..23]}… is allowed by the ledger"]));
        return problems.Count > 0
            ? new(null, problems)
            : new(new VerifiedPack(list!.Id, list.Version, digest, manifest!, signatures,
                content.Files.Where(f => FilesList.IsListed(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal)), []);
    }

    /// <summary>Steps 1–5: everything that needs no trust. <c>ampack lint</c> and <c>build</c> use it alone.</summary>
    internal static (List<string> Problems, FilesList? List, byte[]? ListBytes, PackManifest? Manifest) CheckIntegrity(PackContent content,
        Action<PackStep>? explain = null)
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
        explain?.Invoke(new("content", problems.Count == 0, problems.Count > 0 ? problems
            : [$"exactly the listed files, each with its length and SHA-256 ({list.Files.Sum(f => f.Length):N0} bytes)"]));
        if (problems.Count > 0) return (problems, null, null, null);

        foreach (var (path, bytes) in content.Files.Where(f => FilesList.IsListed(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var problem = path.EndsWith(".jsonl", StringComparison.Ordinal) ? PackJson.CheckLines(bytes)
                : path.EndsWith(".json", StringComparison.Ordinal) ? PackJson.Check(bytes, integersOnly: path == PackPaths.LockFile) : null;
            if (problem is not null) problems.Add($"'{path}' {problem}.");
        }
        explain?.Invoke(new("JSON profile", problems.Count == 0, problems.Count > 0 ? problems
            : ["every JSON and JSONL file: UTF-8, LF, no repeated key, NFC strings, integers within 2^53"]));
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
        if (manifest.Kind == "composite" && all.Contains(PackPaths.LockFile))
            foreach (var problem in CompositeProblems(manifest, files)) yield return problem;
        if (manifest.Protection != "open" && !all.Contains("META/seal.json"))
            yield return $"A pack marked {manifest.Protection} must carry META/seal.json.";
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

    private static string[]? MemberIds(byte[] membersBytes)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(membersBytes);
            return [.. document.RootElement.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)];
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// A pack set and each of its members, verified independently: the set's own signature binds the members together, and
    /// each member keeps its own list and signatures (v1.2 §5).
    /// </summary>
    internal static (PackVerification Set, IReadOnlyDictionary<string, PackVerification> Members) VerifySet(PackContent content, TrustPolicy policy,
        DateTimeOffset now, HostLedger? ledger = null, bool allowDowngrade = false)
    {
        var set = Verify(content, policy, now, ledger, allowDowngrade);
        var members = new Dictionary<string, PackVerification>(StringComparer.Ordinal);
        if (set.Pack is not { } outer) return (set, members);
        if (outer.Manifest.Kind != "set") return (new PackVerification(null, [$"{outer.Id} is a {outer.Manifest.Kind} pack, not a set."]), members);
        Dictionary<string, Dictionary<string, byte[]>> split;
        try { split = PackSets.Members(content.Files); }
        catch (InvalidDataException e) { return (new PackVerification(null, [e.Message]), members); }
        if (!split.Keys.Order(StringComparer.Ordinal).SequenceEqual(outer.Manifest.Contains.Order(StringComparer.Ordinal)))
            return (new PackVerification(null, ["The set's pack.json names other members than it carries."]), members);
        foreach (var (id, files) in split)
        {
            var member = Verify(new PackContent(files, content.Source + "#" + id), policy, now, ledger, allowDowngrade);
            members[id] = member.Pack is { } verified && verified.Id != id
                ? new PackVerification(null, [$"The member in packs/{id}/ is {verified.Id}."]) : member;
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
