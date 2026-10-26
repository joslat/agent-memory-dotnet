using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A pack signing key: its id (the SHA-256 of the public key), and both halves as PEM. Keep the private half secret.</summary>
[Experimental("AMEXT001")]
public sealed record PackSigningKey(string KeyId, string PrivateKeyPem, string PublicKeyPem);

/// <summary>
/// Authoring memory packs from a host or a tool such as DemoBrain's composer (pack format v1.2): migrate a module bundle to
/// the v1.2 layout, create keys, write a trust policy, and build, sign and pack a folder's files into an <c>.ampack</c>.
/// The same operations as the <c>ampack</c> command line.
/// </summary>
[Experimental("AMEXT001")]
public static class PackAuthoring
{
    /// <summary>A new ECDSA P-256 signing key.</summary>
    public static PackSigningKey CreateKey()
    {
        var key = PackKey.Create();
        return new PackSigningKey(key.KeyId, key.PrivateKeyPem, key.PublicKeyPem);
    }

    /// <summary>An existing module bundle (<c>manifest.json</c>, seed files, icon) as v1.2 pack files (PACK-migrate).</summary>
    public static IReadOnlyDictionary<string, byte[]> FromModuleBundle(IReadOnlyDictionary<string, byte[]> bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return PackLayout.FromModule(bundle);
    }

    /// <summary>
    /// Builds a pack from its source files: writes the canonical file list, signs it with <paramref name="privateKeyPem"/> in
    /// <paramref name="role"/>, and returns the <c>.ampack</c> bytes.
    /// </summary>
    /// <exception cref="PackLoadException">The files do not form a valid pack.</exception>
    public static byte[] Build(IReadOnlyDictionary<string, byte[]> source, string privateKeyPem, string role = "publisher")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        Dictionary<string, byte[]> files;
        try { (files, _) = PackBuilder.Build(source); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        if (PackVerifier.CheckIntegrity(new PackContent(files, "build")).Problems is { Count: > 0 } problems) throw new PackLoadException(problems);
        PackBuilder.Sign(files, privateKeyPem, role, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return PackBuilder.Pack(files);
    }

    /// <summary>
    /// A snapshot as v1.2 snapshot-pack files (§3): <c>pack.json</c> (kind <c>snapshot</c>, requiring the snapshot's module from
    /// its version up to the next minor) and one record file per type, records by key. Build and sign it with
    /// <see cref="Build"/>; a host imports it with <see cref="PackLoader.ToSnapshot"/>.
    /// </summary>
    /// <param name="snapshot">The records of one module at one source, such as the code map of a repository at a commit.</param>
    /// <param name="id">The snapshot pack's own id, in the publisher's namespace.</param>
    /// <param name="version">Its version.</param>
    /// <param name="repository">Where the records were taken from, if it is to be named.</param>
    /// <param name="commit">The commit, 7 to 64 hexadecimal characters; a host records <c>git:</c> and it as the source.</param>
    /// <param name="builtAt">When it was built; left out for a reproducible build.</param>
    /// <exception cref="PackLoadException">The snapshot or the identity is invalid.</exception>
    public static IReadOnlyDictionary<string, byte[]> FromSnapshot(ModuleSnapshot snapshot, string id, string version, string? repository = null,
        string? commit = null, DateTimeOffset? builtAt = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var problems = new List<string>(snapshot.Problems());
        if (!PackManifest.IsValidId(id ?? "")) problems.Add($"'{id}' is not a reverse-DNS id.");
        if (!SemVer.TryParse(version ?? "", out _)) problems.Add($"'{version}' is not a SemVer version.");
        if (!SemVer.TryParse(snapshot.Module.Version.Value, out var of)) problems.Add($"{snapshot.Module.Id} has no SemVer version.");
        if (problems.Count > 0) throw new PackLoadException(problems);

        var pack = new JsonObject
        {
            ["format"] = PackManifest.Format, ["id"] = id, ["version"] = version, ["kind"] = "snapshot", ["sdk"] = snapshot.Module.Sdk.Value,
            ["requires"] = new JsonObject
            {
                ["modules"] = new JsonObject { [snapshot.Module.Id] = $"[{snapshot.Module.Version.Value},{of.Major}.{of.Minor + 1})" },
            },
        };
        var source = new JsonObject();
        if (repository is not null) source["repository"] = repository;
        if (commit is not null) source["commit"] = commit;
        if (builtAt is { } at) source["builtAt"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        if (source.Count > 0) pack["source"] = source;
        pack["tier"] = "open";
        pack["protection"] = "open";
        pack["code"] = "E0";

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PackManifest.FileName] = PackLayout.Write(pack) };
        foreach (var type in snapshot.Records.GroupBy(r => r.Type, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            using var stream = new MemoryStream();
            foreach (var record in type.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", record.Type);
                    writer.WriteString("key", record.Key);
                    writer.WriteStartObject("properties");
                    foreach (var (name, value) in record.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(name);
                        value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                    if (record.Links.Count > 0)
                    {
                        writer.WriteStartArray("links");
                        foreach (var link in record.Links)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("relation", link.Relation);
                            writer.WriteString("target", link.Target);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                stream.WriteByte((byte)'\n');
            }
            files[PackLayout.Records + type.Key + ".jsonl"] = stream.ToArray();
        }
        return files;
    }

    /// <summary>
    /// Builds a composite pack locked to exactly the given member packs (v1.2 §5): each member's id, version and digest go into
    /// <c>META/lock.json</c>, which the composite's signature then covers.
    /// </summary>
    /// <param name="source">The composite's source files, such as <see cref="FromModuleBundle"/> made from a composite manifest.</param>
    /// <param name="members">The member <c>.ampack</c> files, built and signed.</param>
    /// <param name="privateKeyPem">The key that signs the composite.</param>
    /// <param name="role">The role it signs in.</param>
    /// <exception cref="PackLoadException">A member is not an intact pack, or two share an id.</exception>
    public static byte[] BuildComposite(IReadOnlyDictionary<string, byte[]> source, IEnumerable<byte[]> members, string privateKeyPem, string role = "publisher")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(members);
        var entries = new List<LockEntry>();
        foreach (var member in members)
        {
            var (files, _) = Intact(member);
            var list = FilesList.Parse(files[PackPaths.FilesList]).List!;
            entries.Add(new LockEntry(list.Id, list.Version, FilesList.DigestOf(files[PackPaths.FilesList])));
        }
        if (entries.GroupBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new PackLoadException([$"{twice.Key} is given twice: a lock pins exactly one version of each pack."]);
        var locked = new Dictionary<string, byte[]>(source, StringComparer.Ordinal) { [PackPaths.LockFile] = PackLock.Build(entries) };
        return Build(locked, privateKeyPem, role);
    }

    /// <summary>
    /// Builds a pack set (v1.2 §5): the given signed packs side by side under <c>packs/&lt;id&gt;/</c>, each keeping its own
    /// files list and signatures, and the set's own <c>pack.json</c> and signature over everything.
    /// </summary>
    /// <exception cref="PackLoadException">A member is not an intact signed pack, two share an id, or there is none.</exception>
    public static byte[] BuildSet(string id, string version, IEnumerable<byte[]> members, string privateKeyPem, string role = "publisher")
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        if (!PackManifest.IsValidId(id ?? "")) throw new PackLoadException([$"'{id}' is not a reverse-DNS id."]);
        var contents = new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var (files, memberId) = Intact(member);
            if (!files.Keys.Any(PackPaths.IsSignature)) throw new PackLoadException([$"{memberId}: a set carries signed members only."]);
            if (!contents.TryAdd(memberId, files)) throw new PackLoadException([$"{memberId} is given twice."]);
        }
        if (contents.Count == 0) throw new PackLoadException(["A set carries at least one member."]);
        Dictionary<string, byte[]> built;
        try { (built, _) = PackBuilder.Build(PackSets.Source(id!, version, contents)); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        PackBuilder.Sign(built, privateKeyPem, role, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return PackBuilder.Pack(built);
    }

    // A member archive that reads under the strict profile and whose files match its own list.
    private static (IReadOnlyDictionary<string, byte[]> Files, string Id) Intact(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        PackContent content;
        try { content = PackReader.ReadArchive(archive, "member"); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        var (problems, list, _, _) = PackVerifier.CheckIntegrity(content);
        if (problems.Count > 0) throw new PackLoadException(problems);
        return (content.Files, list!.Id);
    }

    /// <summary>A trust policy (<c>agentmemory.pack.trust/1.1</c>) that trusts one key for the given namespaces as publisher.</summary>
    public static string TrustPolicy(string publicKeyPem, params string[] idPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        if (idPrefixes is not { Length: > 0 }) throw new ArgumentException("A key is trusted for at least one namespace.", nameof(idPrefixes));
        var policy = new JsonObject
        {
            ["format"] = global::AgentMemory.Packs.Format.TrustPolicy.Format,
            ["keys"] = new JsonArray((JsonNode)new JsonObject
            {
                ["keyId"] = PackKey.KeyIdOf(publicKeyPem), ["publicKey"] = publicKeyPem,
                ["idPrefixes"] = new JsonArray([.. idPrefixes.Select(p => (JsonNode)p)]), ["roles"] = new JsonArray((JsonNode)"publisher"),
            }),
        };
        return Encoding.UTF8.GetString(PackLayout.Write(policy));
    }
}
