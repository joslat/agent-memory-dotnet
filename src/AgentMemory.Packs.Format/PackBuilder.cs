namespace AgentMemory.Packs.Format;

/// <summary>
/// Builds a pack from its authoring folder: writes the canonical <c>META/files.json</c>, signs it, and packs the result
/// into a strict <c>.ampack</c>. Building twice from the same files gives the same list, the same digest, and (with the
/// same toolchain) the same archive bytes. It is the one writer (review 7, F2 and F3): every rule a writer holds is held
/// here, by <c>Sign</c> for every signature and by <see cref="Pack"/> for every archive, so no writer path can skip one.
/// </summary>
internal static class PackBuilder
{
    /// <summary>The <c>.gitattributes</c> a pack folder needs so that Git never rewrites its bytes (v1.1 M1).</summary>
    internal const string GitAttributes = "* -text -filter -diff\n";

    /// <summary>
    /// The files with a fresh <c>META/files.json</c>. Existing signatures and attestations are kept only when the list did not
    /// change; otherwise they no longer name this content and are dropped.
    /// </summary>
    internal static (Dictionary<string, byte[]> Files, bool SignaturesDropped) Build(IReadOnlyDictionary<string, byte[]> source)
    {
        if (!source.TryGetValue(PackManifest.FileName, out var manifestBytes)) throw new InvalidDataException($"There is no {PackManifest.FileName}.");
        var (manifest, problem) = PackManifest.Parse(manifestBytes);
        if (manifest is null) throw new InvalidDataException(problem);
        if (WriterProblem(manifest) is { } refused) throw new InvalidDataException(refused);
        var listBytes = FilesList.Build(manifest.Id, manifest.Version, source);
        var unchanged = source.TryGetValue(PackPaths.FilesList, out var previous) && previous.AsSpan().SequenceEqual(listBytes);
        var files = source.Where(f => FilesList.IsListed(f.Key) || (unchanged && (PackPaths.IsSignature(f.Key) || PackPaths.IsAttestation(f.Key))))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        files[PackPaths.FilesList] = listBytes;
        return (files, !unchanged && source.Keys.Any(p => PackPaths.IsSignature(p) || PackPaths.IsAttestation(p)));
    }

    /// <summary>
    /// What a writer refuses to build or sign, or null (§2.1 rule 9): a <c>pack.json</c> of a later minor than it writes. A
    /// reader may read a later minor as far as it knows it; a writer signs only what it understands. <c>ampack lint</c> says it
    /// too, so lint and build agree.
    /// </summary>
    internal static string? WriterProblem(PackManifest manifest) => manifest.FormatMinor > PackManifest.Minor
        ? $"{PackManifest.FileName} has format agentmemory.pack/1.{manifest.FormatMinor}; this writer writes {PackManifest.Format} and signs only what it understands."
        : null;

    /// <summary>Adds (or replaces) this key's signature in <paramref name="role"/>.</summary>
    internal static string Sign(Dictionary<string, byte[]> files, string privateKeyPem, string role, DateTimeOffset signedAt)
    {
        using var signer = PackSigner.FromPem(privateKeyPem);
        return Sign(files, signer, role, signedAt);
    }

    /// <summary>
    /// Adds (or replaces) this signer's signature in <paramref name="role"/>: a key in memory, a keychain, a KMS. Every pack
    /// signature passes here (build, sign, countersign, set), so the writer's rules hold for each (review 7, F2: the minor rule
    /// held in build only, and countersign put an assurer's signature on a later minor it could not read).
    /// </summary>
    internal static string Sign(Dictionary<string, byte[]> files, PackSigner signer, string role, DateTimeOffset signedAt)
    {
        var listBytes = files[PackPaths.FilesList];
        var (list, problem) = FilesList.Parse(listBytes);
        if (list is null) throw new InvalidDataException(problem);
        if (files.TryGetValue(PackManifest.FileName, out var manifestBytes))
        {
            var (manifest, manifestProblem) = PackManifest.Parse(manifestBytes);
            if (manifest is null) throw new InvalidDataException(manifestProblem);
            if (WriterProblem(manifest) is { } refused) throw new InvalidDataException(refused);
        }
        // What no reader would accept is never written (round 10, A-M2): a signing time ahead of every reader's clock.
        if (TrustPolicy.InTheFuture(signedAt, DateTimeOffset.UtcNow))
            throw new InvalidDataException($"The signing time {signedAt:u} is in the future; every reader would refuse the signature (check SOURCE_DATE_EPOCH).");
        var envelope = Dsse.Sign(listBytes, list.Id, list.Version, role, signedAt, signer);
        var path = Dsse.PathFor(role, signer.KeyId);
        files[path] = envelope;
        // A timestamp token is over the envelope it sits beside; a new envelope leaves the old token naming nothing (§2.1).
        files.Remove(path + PackPaths.TimestampSuffix);
        return path;
    }

    /// <summary>
    /// The <c>.ampack</c> bytes, read back before they are returned (review 7, F3: the writers wrote archives both verifiers
    /// refuse, past the compression ratio, the file count or the token rules): under the reader's own limits and every check
    /// that needs no trust, the paths, the signed list, exactly the listed files, the signature folder and the attestations.
    /// What no reader would accept is never written.
    /// </summary>
    /// <exception cref="InvalidDataException">A reader would refuse the archive; the message says why.</exception>
    internal static byte[] Pack(IReadOnlyDictionary<string, byte[]> files, bool compress = true)
    {
        var archive = StrictZip.Write(files, compress);
        var back = PackReader.ReadArchive(archive, "the written archive");
        if (PackVerifier.CheckIntegrity(back, records: false).Problems is { Count: > 0 } problems)
            throw new InvalidDataException("No reader would accept this pack, so it was not written: " + string.Join(" ", problems));
        return archive;
    }
}
