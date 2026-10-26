namespace AgentMemory.Packs.Format;

/// <summary>
/// Builds a pack from its authoring folder: writes the canonical <c>META/files.json</c>, signs it, and packs the result
/// into a strict <c>.ampack</c>. Building twice from the same files gives the same list, the same digest, and (with the
/// same toolchain) the same archive bytes.
/// </summary>
internal static class PackBuilder
{
    /// <summary>The <c>.gitattributes</c> a pack folder needs so that Git never rewrites its bytes (v1.1 M1).</summary>
    internal const string GitAttributes = "* -text -filter -diff\n";

    /// <summary>
    /// The files with a fresh <c>META/files.json</c>. Existing signatures are kept only when the list did not change;
    /// otherwise they no longer sign this content and are dropped.
    /// </summary>
    internal static (Dictionary<string, byte[]> Files, bool SignaturesDropped) Build(IReadOnlyDictionary<string, byte[]> source)
    {
        if (!source.TryGetValue(PackManifest.FileName, out var manifestBytes)) throw new InvalidDataException($"There is no {PackManifest.FileName}.");
        var (manifest, problem) = PackManifest.Parse(manifestBytes);
        if (manifest is null) throw new InvalidDataException(problem);
        var listBytes = FilesList.Build(manifest.Id, manifest.Version, source);
        var unchanged = source.TryGetValue(PackPaths.FilesList, out var previous) && previous.AsSpan().SequenceEqual(listBytes);
        var files = source.Where(f => FilesList.IsListed(f.Key) || (unchanged && PackPaths.IsSignature(f.Key)))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        files[PackPaths.FilesList] = listBytes;
        return (files, !unchanged && source.Keys.Any(PackPaths.IsSignature));
    }

    /// <summary>Adds (or replaces) this key's signature in <paramref name="role"/>.</summary>
    internal static string Sign(Dictionary<string, byte[]> files, string privateKeyPem, string role, DateTimeOffset signedAt)
    {
        var listBytes = files[PackPaths.FilesList];
        var (list, problem) = FilesList.Parse(listBytes);
        if (list is null) throw new InvalidDataException(problem);
        var envelope = Dsse.Sign(listBytes, list.Id, list.Version, role, signedAt, privateKeyPem);
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var path = Dsse.PathFor(role, PackKey.KeyIdOf(key));
        files[path] = envelope;
        return path;
    }

    /// <summary>The <c>.ampack</c> bytes.</summary>
    internal static byte[] Pack(IReadOnlyDictionary<string, byte[]> files, bool compress = true) => StrictZip.Write(files, compress);
}
