// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Packs.Format;

namespace Ampack;

/// <summary>
/// A verified ampack memory pack, and the three things a client does with a pack file: <see cref="Verify(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, PackOptions?)"/>,
/// <see cref="Inspect(ReadOnlyMemory{byte})"/> and <see cref="Load(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, PackOptions?)"/>.
/// A pack is accepted only when a key in the host's own trust policy signed it as publisher for its id.
/// This client verifies and reads. It does not build, sign or certify a pack.
/// </summary>
public sealed class Pack
{
    // A knowledge pack as a host imports it (specification, R-CORE-102).
    private const int MaximumRecords = 5000, MaximumRecordFiles = 16, MaximumLineBytes = 64 * 1024, MaximumRecordDepth = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IReadOnlyDictionary<string, byte[]> _files;

    private Pack(VerifiedPack verified, IReadOnlyList<Pack> members)
    {
        _files = verified.Files;
        Id = verified.Id;
        Version = verified.Version;
        Digest = verified.Digest;
        Kind = verified.Manifest.Kind;
        Profiles = ProfilesOf(verified);
        Members = members;
    }

    /// <summary>The pack's id, for example <c>io.example.notes</c>.</summary>
    public string Id { get; }

    /// <summary>The pack's version.</summary>
    public string Version { get; }

    /// <summary>The pack's digest: <c>sha256:</c> and the hex of its signed file list.</summary>
    public string Digest { get; }

    /// <summary>What kind of pack it is: knowledge, definition, snapshot, composite or set.</summary>
    public string Kind { get; }

    /// <summary>The profiles of the format the pack uses; <c>core</c> alone when it uses none.</summary>
    public IReadOnlyList<string> Profiles { get; }

    /// <summary>For a pack set: its member packs, each verified on its own, in the order of their ids. Empty otherwise.</summary>
    public IReadOnlyList<Pack> Members { get; }

    /// <summary>The paths of every file of the pack, as verified.</summary>
    public IEnumerable<string> Paths => _files.Keys;

    /// <summary>Whether the pack holds a file at <paramref name="path"/>.</summary>
    public bool Contains(string path) => _files.ContainsKey(path);

    /// <summary>A file's bytes, read-only; null when the pack has no such file.</summary>
    public ReadOnlyMemory<byte>? Bytes(string path)
    {
        // Written out: in a conditional expression the null would itself convert to an empty ReadOnlyMemory, never to "no file".
        if (!_files.TryGetValue(path, out var bytes)) return null;
        return new ReadOnlyMemory<byte>(bytes);
    }

    /// <summary>A file's text (UTF-8); null when the pack has no such file.</summary>
    public string? Text(string path) => _files.TryGetValue(path, out var bytes) ? StrictUtf8.GetString(bytes) : null;

    /// <summary>A JSON file of the pack; null when the pack has no such file. The caller disposes the document.</summary>
    public JsonDocument? Json(string path) => _files.TryGetValue(path, out var bytes) ? JsonDocument.Parse(bytes) : null;

    /// <summary>The pack's manifest, <c>pack.json</c>. The caller disposes the document.</summary>
    public JsonDocument Manifest => JsonDocument.Parse(_files["pack.json"]);

    /// <summary>The pack's display title, when its manifest gives one.</summary>
    public string? Title => TitleOf(_files);

    /// <summary>The pack card (owner, intended use, limitations), <c>card.json</c>; null when the pack carries none.</summary>
    public JsonDocument? Card => Json("card.json");

    /// <summary>The types and relations the pack declares, <c>ontology.json</c>; null when it declares none.</summary>
    public JsonDocument? Ontology => Json("ontology.json");

    /// <summary>The pack's record files, in name order.</summary>
    public IReadOnlyList<string> RecordFiles => PackLayout.RecordFiles(_files.Keys);

    /// <summary>
    /// The pack's own record lines, file by file in name order. A set or a composite has none: see <see cref="Members"/>.
    /// Each line is checked for its shape only: an object with a string type and key, properties as an object, links as
    /// <c>{"relation", "target"}</c> pairs, no field the format does not define, and for a knowledge pack the limits a host
    /// imports within (5,000 records, 16 files, 64 KiB a line, nesting 8). Whether a record fits the pack's ontology is for
    /// the host that imports it to check.
    /// </summary>
    /// <exception cref="PackRefusedException">A line is not a record, or a limit is passed.</exception>
    public IReadOnlyList<PackRecord> Records()
    {
        var paths = RecordFiles;
        var knowledge = Kind == "knowledge";
        if (knowledge && paths.Count > MaximumRecordFiles)
            throw new PackRefusedException([$"A knowledge pack holds at most {MaximumRecordFiles} record files; this one has {paths.Count}."]);
        var records = new List<PackRecord>();
        foreach (var path in paths)
        {
            var data = _files[path].AsSpan();
            var line = 0;
            while (!data.IsEmpty)
            {
                var stop = data.IndexOf((byte)'\n');
                var raw = stop < 0 ? data : data[..stop];
                data = stop < 0 ? [] : data[(stop + 1)..];
                line++;
                var at = $"{path}:{line}";
                if (knowledge && raw.Length > MaximumLineBytes) throw new PackRefusedException([$"{at}: longer than {MaximumLineBytes} bytes."]);
                records.Add(Record(raw, path, line, at, knowledge));
                if (knowledge && records.Count > MaximumRecords) throw new PackRefusedException([$"A knowledge pack holds at most {MaximumRecords} records."]);
            }
        }
        return records;
    }

    /// <summary>
    /// Verifies a pack (or a pack set and each of its members) against a trust policy. A refusal is a verdict, not an
    /// exception: <see cref="PackVerdict.Reasons"/> says why.
    /// </summary>
    /// <param name="archive">The bytes of the <c>.ampack</c> file.</param>
    /// <param name="trustPolicy">The bytes of the host's trust policy (<c>trust.json</c>).</param>
    /// <param name="options">Revocation documents and the moment to judge at.</param>
    public static PackVerdict Verify(ReadOnlyMemory<byte> archive, ReadOnlyMemory<byte> trustPolicy, PackOptions? options = null)
    {
        var (verified, _, reasons) = Verified(archive, trustPolicy, options);
        return verified is null
            ? new PackVerdict { Reasons = reasons }
            : new PackVerdict { Id = verified.Id, Version = verified.Version, Digest = verified.Digest, Kind = verified.Manifest.Kind, Profiles = ProfilesOf(verified) };
    }

    /// <summary>Verifies the pack file at <paramref name="packPath"/> against the trust policy file at <paramref name="trustPolicyPath"/>.</summary>
    /// <remarks>Files are read with a size bound and never through a link.</remarks>
    public static PackVerdict Verify(string packPath, string trustPolicyPath, PackOptions? options = null)
    {
        try { return Verify(ReadPack(packPath), ReadPolicy(trustPolicyPath), options); }
        catch (Exception e) when (IsRefusal(e)) { return new PackVerdict { Reasons = [e.Message] }; }
    }

    /// <summary>
    /// Verifies a pack as <see cref="Verify(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, PackOptions?)"/> does and returns it for reading.
    /// Nothing is returned from a pack that did not verify.
    /// </summary>
    /// <exception cref="PackRefusedException">The pack is refused; <see cref="PackRefusedException.Reasons"/> says why.</exception>
    public static Pack Load(ReadOnlyMemory<byte> archive, ReadOnlyMemory<byte> trustPolicy, PackOptions? options = null)
    {
        var (verified, members, reasons) = Verified(archive, trustPolicy, options);
        if (verified is null) throw new PackRefusedException(reasons);
        return new Pack(verified, [.. members.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => new Pack(m, []))]);
    }

    /// <summary>Loads the pack file at <paramref name="packPath"/> against the trust policy file at <paramref name="trustPolicyPath"/>.</summary>
    /// <exception cref="PackRefusedException">The pack is refused, or a file cannot be read.</exception>
    public static Pack Load(string packPath, string trustPolicyPath, PackOptions? options = null)
    {
        ReadOnlyMemory<byte> archive, policy;
        try { (archive, policy) = (ReadPack(packPath), ReadPolicy(trustPolicyPath)); }
        catch (Exception e) when (IsRefusal(e)) { throw new PackRefusedException([e.Message]); }
        return Load(archive, policy, options);
    }

    /// <summary>
    /// What an archive says about itself, read in the strict archive profile. No signature is judged: never treat this as a
    /// verdict.
    /// </summary>
    /// <exception cref="PackRefusedException">The bytes are not an archive in the format's profile.</exception>
    public static PackSummary Inspect(ReadOnlyMemory<byte> archive)
    {
        IReadOnlyDictionary<string, byte[]> files;
        try { files = PackReader.ReadArchive(archive.ToArray(), "the archive").Files; }
        catch (Exception e) when (IsRefusal(e)) { throw new PackRefusedException([e.Message]); }
        string? id = null, version = null, kind = null;
        if (files.TryGetValue("pack.json", out var manifest))
        {
            try
            {
                using var document = JsonDocument.Parse(manifest);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    (id, version, kind) = (Text(document.RootElement, "id"), Text(document.RootElement, "version"), Text(document.RootElement, "kind"));
            }
            catch (JsonException) { }
        }
        return new PackSummary
        {
            Files = files.Count,
            Bytes = files.Values.Sum(f => (long)f.Length),
            Id = id,
            Version = version,
            Kind = kind,
            Digest = files.TryGetValue(PackPaths.FilesList, out var list) ? "sha256:" + FilesList.Hex(list) : null,
            Envelopes = files.Keys.Count(p => p.StartsWith(PackPaths.SignaturesFolder, StringComparison.Ordinal) && !p.EndsWith(PackPaths.TimestampSuffix, StringComparison.Ordinal)),
            Title = TitleOf(files),
        };
    }

    /// <summary>Inspects the pack file at <paramref name="packPath"/>.</summary>
    /// <exception cref="PackRefusedException">The file cannot be read, or is not an archive in the format's profile.</exception>
    public static PackSummary Inspect(string packPath)
    {
        ReadOnlyMemory<byte> archive;
        try { archive = ReadPack(packPath); }
        catch (Exception e) when (IsRefusal(e)) { throw new PackRefusedException([e.Message]); }
        return Inspect(archive);
    }

    private static byte[] ReadPack(string path) => PackReader.ReadArchiveFile(path);

    private static byte[] ReadPolicy(string path) => PackReader.ReadFile(path, TrustPolicy.MaximumBytes, "The trust policy");

    // The one verification Verify and Load stand on: the pack and, for a set, its members; or every reason it is refused.
    private static (VerifiedPack? Pack, IReadOnlyList<VerifiedPack> Members, IReadOnlyList<string> Reasons) Verified(
        ReadOnlyMemory<byte> archive, ReadOnlyMemory<byte> trustPolicy, PackOptions? options)
    {
        var now = options?.At ?? DateTimeOffset.UtcNow;
        try
        {
            var policy = TrustPolicy.Parse(trustPolicy.Span);
            if (options?.Revocations is { Count: > 0 } revocations)
            {
                if (revocations.FirstOrDefault(r => r.Length > Revocations.MaximumDocumentBytes) is { Length: > 0 })
                    return (null, [], [$"A revocation document is larger than {Revocations.MaximumDocumentBytes} bytes."]);
                policy = Revocations.ApplyAll(policy, [.. revocations.Select(r => r.ToArray())], now, ledger: null, out _);
            }
            var content = PackReader.ReadArchive(archive.ToArray(), "the archive");
            var first = PackVerifier.VerifyOne(content, policy, now);
            if (first.Pack is not { } pack) return (null, [], Some(first.Problems));
            if (pack.Manifest.Kind != "set") return (pack, [], []);
            // A set as a whole, then each member on its own (the format's rule: a set is never taken on its outer signature).
            var (set, members) = PackVerifier.VerifySet(content, policy, now, verified: first);
            if (set.Pack is null) return (null, [], Some(set.Problems));
            var refused = members.Where(m => !m.Value.IsVerified).OrderBy(m => m.Key, StringComparer.Ordinal)
                .SelectMany(m => m.Value.Problems.Select(p => $"packs/{m.Key}/: {p}")).ToList();
            return refused.Count > 0 ? (null, [], refused) : (pack, [.. members.Values.Select(m => m.Pack!)], []);
        }
        catch (Exception e) when (IsRefusal(e)) { return (null, [], [e.Message]); }
    }

    private static IReadOnlyList<string> Some(IReadOnlyList<string> problems) => problems.Count > 0 ? problems : ["The pack is refused."];

    // Input that does not hold (a malformed archive, policy or document) is a refusal, as the format's own tool treats it.
    private static bool IsRefusal(Exception e) => e is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
        or JsonException or CryptographicException or FormatException or InvalidOperationException or KeyNotFoundException;

    private static IReadOnlyList<string> ProfilesOf(VerifiedPack pack)
    {
        var used = PackProfiles.Used(pack.Files.Keys, pack.Manifest);
        return used.Count == 0 ? ["core"] : [.. used.Select(u => u.Implemented ? u.Name : u.Name + " (not implemented by this reader)")];
    }

    private static string? TitleOf(IReadOnlyDictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("pack.json", out var manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(manifest);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("display", out var display)
                && display.ValueKind == JsonValueKind.Object ? Text(display, "title") : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // internal for the tests: the line check on its own.
    internal static PackRecord Record(ReadOnlySpan<byte> line, string file, int number, string at, bool knowledge)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = knowledge ? MaximumRecordDepth : 64 }); }
        catch (JsonException) { throw new PackRefusedException([$"{at}: not a JSON object within the nesting a record may have."]); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PackRefusedException([$"{at}: a record is a JSON object."]);
            var unknown = root.EnumerateObject().Select(p => p.Name)
                .Where(n => n is not ("type" or "key" or "properties" or "links") && !PackRegistry.LifeFields.Contains(n) && !PackRegistry.TrustFields.Contains(n))
                .Order(StringComparer.Ordinal).ToArray();
            if (unknown.Length > 0) throw new PackRefusedException([$"{at}: unknown field {string.Join(", ", unknown)}."]);
            if (Text(root, "type") is not { } type || Text(root, "key") is not { } key)
                throw new PackRefusedException([$"{at}: a record needs a string type and key."]);
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("properties", out var props))
            {
                if (props.ValueKind != JsonValueKind.Object) throw new PackRefusedException([$"{at}: properties is an object."]);
                foreach (var property in props.EnumerateObject()) properties[property.Name] = property.Value.Clone();
            }
            var links = new List<PackLink>();
            if (root.TryGetProperty("links", out var linked))
            {
                if (linked.ValueKind != JsonValueKind.Array) throw new PackRefusedException([$"{at}: links is a list of {{\"relation\", \"target\"}} with string values."]);
                foreach (var link in linked.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Object || link.EnumerateObject().Any(p => p.Name is not ("relation" or "target"))
                        || Text(link, "relation") is not { } relation || Text(link, "target") is not { } target)
                        throw new PackRefusedException([$"{at}: links is a list of {{\"relation\", \"target\"}} with string values."]);
                    links.Add(new PackLink(relation, target));
                }
            }
            var extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("type" or "key" or "properties" or "links")) extra[property.Name] = property.Value.Clone();
            return new PackRecord(type, key, properties, links, extra, file, number);
        }
    }
}

/// <summary>What to verify with, besides the pack and the trust policy.</summary>
public sealed class PackOptions
{
    /// <summary>The host's revocation documents, applied to the trust policy before the pack is judged.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Revocations { get; init; } = [];

    /// <summary>The moment to judge validity windows at. Default: now.</summary>
    public DateTimeOffset? At { get; init; }
}

/// <summary>The outcome of verifying a pack: what verified, or why it is refused.</summary>
public sealed class PackVerdict
{
    /// <summary>Whether the pack verified.</summary>
    public bool Verified => Reasons.Count == 0;

    /// <summary>Why the pack is refused; empty when it verified.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>The verified pack's id; null when refused.</summary>
    public string? Id { get; init; }

    /// <summary>The verified pack's version; null when refused.</summary>
    public string? Version { get; init; }

    /// <summary>The verified pack's digest; null when refused.</summary>
    public string? Digest { get; init; }

    /// <summary>The verified pack's kind; null when refused.</summary>
    public string? Kind { get; init; }

    /// <summary>The profiles the verified pack uses; empty when refused.</summary>
    public IReadOnlyList<string> Profiles { get; init; } = [];
}

/// <summary>What an archive says about itself. No signature was judged.</summary>
public sealed class PackSummary
{
    /// <summary>How many files the archive holds.</summary>
    public int Files { get; init; }

    /// <summary>Their size together, unpacked.</summary>
    public long Bytes { get; init; }

    /// <summary>The id its manifest names, if it names one.</summary>
    public string? Id { get; init; }

    /// <summary>The version its manifest names.</summary>
    public string? Version { get; init; }

    /// <summary>The kind its manifest names.</summary>
    public string? Kind { get; init; }

    /// <summary>The digest of its file list, if it has one.</summary>
    public string? Digest { get; init; }

    /// <summary>How many signature envelopes it carries.</summary>
    public int Envelopes { get; init; }

    /// <summary>The display title its manifest gives.</summary>
    public string? Title { get; init; }
}

/// <summary>One record line of a pack, as it is written.</summary>
/// <param name="Type">The record's type.</param>
/// <param name="Key">The record's key, unique for its type in the pack.</param>
/// <param name="Properties">The record's properties.</param>
/// <param name="Links">The record's links to other records.</param>
/// <param name="Extra">The other fields the format allows on a line (validFrom, expiresAt, confidenceMicro and the like), not checked here.</param>
/// <param name="File">The file inside the pack, for a message.</param>
/// <param name="Line">The line number in that file, from 1.</param>
public sealed record PackRecord(string Type, string Key, IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlyList<PackLink> Links,
    IReadOnlyDictionary<string, JsonElement> Extra, string File, int Line);

/// <summary>A link from one record to another.</summary>
/// <param name="Relation">The relation's name.</param>
/// <param name="Target">The key of the record it points to.</param>
public sealed record PackLink(string Relation, string Target);

/// <summary>A pack, or a document beside it, is not accepted.</summary>
public sealed class PackRefusedException : Exception
{
    /// <summary>Creates the refusal with its reasons.</summary>
    public PackRefusedException(IReadOnlyList<string> reasons) : base(string.Join(" ", reasons)) => Reasons = reasons;

    /// <summary>Every reason the pack is refused.</summary>
    public IReadOnlyList<string> Reasons { get; }
}
