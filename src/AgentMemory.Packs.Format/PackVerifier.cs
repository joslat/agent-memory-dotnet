using System.Text;

namespace AgentMemory.Packs.Format;

/// <summary>A pack that passed every check, with what the host needs next.</summary>
internal sealed record VerifiedPack(string Id, string Version, string Digest, PackManifest Manifest, IReadOnlyList<PackSignature> Signatures,
    IReadOnlyDictionary<string, byte[]> Files);

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
    internal static PackVerification Verify(PackContent content, TrustPolicy policy, DateTimeOffset now, HostLedger? ledger = null, bool allowDowngrade = false)
    {
        var (integrity, list, listBytes, manifest) = CheckIntegrity(content);
        if (integrity.Count > 0) return new(null, integrity);
        var problems = new List<string>();

        var digest = FilesList.DigestOf(listBytes!);
        if (policy.RevokedDigests.Contains(digest)) problems.Add($"The pack {digest} is revoked.");
        if (manifest!.ValidUntil is { } until && until < now) problems.Add($"The pack was valid until {until:u}.");
        if (policy.RevocationsExpire is { } expires && expires < now) problems.Add($"The host's revocation list expired at {expires:u}; refresh it before verifying.");

        var signatures = new List<PackSignature>();
        foreach (var file in content.Files.Keys.Where(PackPaths.IsSignature).Order(StringComparer.Ordinal))
        {
            var (valid, invalid) = Dsse.Verify(file, content.Files[file], listBytes, list!, policy, now);
            signatures.AddRange(valid);
            problems.AddRange(invalid);
        }
        foreach (var role in policy.RequiredRoles.Where(r => signatures.All(s => s.Role != r)))
            problems.Add($"No valid {role} signature: a pack is refused unless a key the host trusts for '{list!.Id}' signed it as {role}.");
        // Invalid extra envelopes are reported, but they do not refuse a pack whose required signatures hold.
        if (policy.RequiredRoles.All(r => signatures.Any(s => s.Role == r)))
            problems.RemoveAll(p => p.StartsWith(PackPaths.SignaturesFolder, StringComparison.Ordinal));

        if (ledger?.Refusal(list!.Id, list.Version, digest, allowDowngrade) is { } refusal) problems.Add(refusal);
        return problems.Count > 0
            ? new(null, problems)
            : new(new VerifiedPack(list!.Id, list.Version, digest, manifest!, signatures,
                content.Files.Where(f => FilesList.IsListed(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal)), []);
    }

    /// <summary>Steps 1–5: everything that needs no trust. <c>ampack lint</c> and <c>build</c> use it alone.</summary>
    internal static (List<string> Problems, FilesList? List, byte[]? ListBytes, PackManifest? Manifest) CheckIntegrity(PackContent content)
    {
        var problems = PackPaths.Problems(content.Files.Keys).ToList();
        if (problems.Count > 0) return (problems, null, null, null);
        if (!content.Files.TryGetValue(PackPaths.FilesList, out var listBytes))
            return ([$"There is no {PackPaths.FilesList}: build the pack first."], null, null, null);
        var (list, listProblem) = FilesList.Parse(listBytes);
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
        if (problems.Count > 0) return (problems, null, null, null);

        foreach (var (path, bytes) in content.Files.Where(f => FilesList.IsListed(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var problem = path.EndsWith(".jsonl", StringComparison.Ordinal) ? PackJson.CheckLines(bytes)
                : path.EndsWith(".json", StringComparison.Ordinal) ? PackJson.Check(bytes, integersOnly: path == PackPaths.LockFile) : null;
            if (problem is not null) problems.Add($"'{path}' {problem}.");
        }
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
                problems.AddRange(KindProblems(manifest, content.Files.Keys));
                if (problems.Count == 0) return (problems, list, listBytes, manifest);
            }
        }
        return (problems, null, null, null);
    }

    /// <summary>What each kind must and must not hold (v1.2 §3).</summary>
    private static IEnumerable<string> KindProblems(PackManifest manifest, IEnumerable<string> paths)
    {
        var records = PackLayout.RecordFiles(paths).Count;
        var all = paths.ToList();
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
        if (manifest.Protection != "open" && !all.Contains("META/seal.json"))
            yield return $"A pack marked {manifest.Protection} must carry META/seal.json.";
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
