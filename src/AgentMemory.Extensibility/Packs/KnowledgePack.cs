using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Shared;

namespace AgentMemory.Extensibility.Packs;

/// <summary>
/// A verified knowledge pack (Extensibility 0.13 §K1): a data-only module whose seed records a host imports as shared
/// memory. Opening verifies the bundle's signature first, then checks every record against the manifest; a pack that opens
/// is one whose every record a store will admit.
/// </summary>
[Experimental("AMEXT001")]
public sealed class KnowledgePack
{
    /// <summary>The bundle folder inside a NuGet package.</summary>
    public const string PackageFolder = "agentmemory/";
    /// <summary>The manifest's path in a bundle.</summary>
    public const string ManifestFile = "manifest.json";
    /// <summary>At most this many records a pack holds in 0.x.</summary>
    public const int MaximumRecords = 5000;
    /// <summary>At most this many UTF-8 bytes a seed line holds.</summary>
    public const int MaximumLineBytes = 64 * 1024;

    /// <summary>At most this many bytes an icon holds.</summary>
    public const int MaximumIconBytes = 64 * 1024;

    /// <summary>
    /// When a pack loaded from an <c>.ampack</c> stops being valid here (its own validUntil, its set's, the trust's and its keys',
    /// whichever is first); null when it does not expire. Composing the pack carries it, however it is composed (review 1, M9).
    /// </summary>
    public DateTimeOffset? ValidUntil { get; internal set; }

    // A copy that stops being valid at until: the instance others hold is never changed under them (review 2).
    internal KnowledgePack WithValidUntil(DateTimeOffset? until)
    {
        var copy = (KnowledgePack)MemberwiseClone();
        copy.ValidUntil = until;
        return copy;
    }

    private KnowledgePack(ModuleManifest manifest, VerifiedBundle bundle, IReadOnlyList<ModuleProposal> proposals, ReadOnlyMemory<byte>? icon)
    { Manifest = manifest; Bundle = bundle; Proposals = proposals; Icon = icon; }

    /// <summary>The pack's verified PNG icon, when its manifest names one (Extensibility 0.13 §D).</summary>
    public ReadOnlyMemory<byte>? Icon { get; }

    /// <summary>The pack's manifest.</summary>
    public ModuleManifest Manifest { get; }
    /// <summary>The verified bundle it came from: digest, signer and key.</summary>
    public VerifiedBundle Bundle { get; }
    /// <summary>The seed records as shared assertions with pack provenance, targets before the records that link to them.</summary>
    public IReadOnlyList<ModuleProposal> Proposals { get; }
    /// <summary>The pack id.</summary>
    public string Id => Manifest.Id;
    /// <summary>The pack version.</summary>
    public string Version => Manifest.Version.Value;
    /// <summary>The bundle digest.</summary>
    public string Digest => Bundle.Digest;
    /// <summary>The pack resolved on its own against the running SDK: what a store needs to migrate and write it.</summary>
    public ModuleResolution Resolution => ResolutionOf(Manifest);

    /// <summary>Resolves a pack manifest on its own against the running SDK; refused when its SDK range excludes this SDK.</summary>
    public static ModuleResolution ResolutionOf(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var resolution = ModuleResolver.Resolve([manifest], ModuleSet.SdkVersion, ModuleHostFeatures.Known);
        if (!resolution.Succeeded) throw new KnowledgePackException(resolution.Errors);
        return resolution;
    }

    /// <summary>Reads, verifies and checks a pack folder.</summary>
    public static KnowledgePack OpenDirectory(string path, BundleTrust trust, string? pin = null) =>
        Open(BundleFiles.ReadDirectory(path), trust, pin);

    /// <summary>The folder a referenced pack package copies its bundle to, under the host's output.</summary>
    public const string ReferencedFolder = "agentmemory-packs";

    /// <summary>
    /// Opens a pack the host references as a NuGet package: its <c>buildTransitive</c> props copy the bundle to
    /// <c>agentmemory-packs/&lt;id&gt;/</c> in the output. Verified like any other bundle.
    /// </summary>
    public static KnowledgePack OpenReferenced(string id, BundleTrust trust, string? pin = null)
    {
        if (!ModuleIdentity.IsValidModuleId(id)) throw new ArgumentException("Expected a pack id.", nameof(id));
        var pack = OpenDirectory(Path.Combine(AppContext.BaseDirectory, ReferencedFolder, id), trust, pin);
        if (pack.Id != id) throw new KnowledgePackException([$"The bundle in {ReferencedFolder}/{id} is the pack {pack.Id}."]);
        return pack;
    }

    /// <summary>Reads, verifies and checks the <c>agentmemory/</c> bundle inside a NuGet package, in memory.</summary>
    public static KnowledgePack OpenPackage(Stream package, BundleTrust trust, string? pin = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        using (var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(PackageFolder, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
                total += entry.Length;
                if (files.Count >= 512 || total > 16L * 1024 * 1024) throw new KnowledgePackException(["The package's pack bundle is larger than 512 files or 16 MiB."]);
                // An entry is read at most to the size it states, which the total above already counts (review 8, A10: a copy
                // without a bound inflated whatever the entry held, whatever it stated).
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                for (int read; (read = stream.Read(chunk, 0, chunk.Length)) > 0;)
                {
                    if (buffer.Length + read > entry.Length)
                        throw new KnowledgePackException([$"The package's '{entry.FullName}' inflates past the size it states."]);
                    buffer.Write(chunk, 0, read);
                }
                if (!files.TryAdd(entry.FullName[PackageFolder.Length..], buffer.ToArray()))
                    throw new KnowledgePackException([$"The package repeats '{entry.FullName}'."]);
            }
        }
        if (files.Count == 0) throw new KnowledgePackException([$"The package has no {PackageFolder} bundle."]);
        return Open(files, trust, pin);
    }

    /// <summary>Verifies a bundle read into memory, then checks it as a pack.</summary>
    public static KnowledgePack Open(IReadOnlyDictionary<string, byte[]> files, BundleTrust trust, string? pin = null)
    {
        var verification = BundleVerifier.Verify(files, trust, pin);
        if (verification.Bundle is not { } bundle) throw new KnowledgePackException(verification.Problems);
        return Open(bundle);
    }

    /// <summary>Checks a verified bundle as a pack: its manifest, then every seed record.</summary>
    public static KnowledgePack Open(VerifiedBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!bundle.Files.TryGetValue(ManifestFile, out var manifestBytes))
            throw new KnowledgePackException([$"A pack bundle needs {ManifestFile}."]);
        ModuleManifest manifest;
        try { manifest = ModuleManifest.Parse(Utf8(manifestBytes, ManifestFile)); }
        catch (FormatException e) { throw new KnowledgePackException([e.Message]); }
        var problems = manifest.Validate().ToList();
        if (!manifest.IsKnowledgePack) problems.Add("The manifest declares no seed files, so it is a module, not a knowledge pack.");
        if (manifest.Id != bundle.Id || manifest.Version.Value != bundle.Version)
            problems.Add($"The manifest is {manifest.Id} {manifest.Version.Value}, but the signed files list names {bundle.Id} {bundle.Version}.");
        if (problems.Count > 0) throw new KnowledgePackException(problems);
        _ = ResolutionOf(manifest);
        var seed = manifest.Seed!;
        var iconPath = manifest.Display?.Icon;
        foreach (var extra in bundle.Files.Keys.Where(f => f != ManifestFile && f != iconPath && !seed.Contains(f, StringComparer.Ordinal)).Order(StringComparer.Ordinal))
            problems.Add($"'{extra}' is neither the manifest, its icon nor a seed file.");
        ReadOnlyMemory<byte>? icon = null;
        if (iconPath is not null)
        {
            if (!bundle.Files.TryGetValue(iconPath, out var image)) problems.Add($"The icon '{iconPath}' is missing.");
            else if (IconProblem(image.Span) is { } problem) problems.Add($"The icon '{iconPath}' {problem}.");
            else icon = image;
        }

        var records = new List<ModuleProposal>();
        var keys = new HashSet<(string Type, string Key)>();
        foreach (var file in seed)
        {
            if (!bundle.Files.TryGetValue(file, out var bytes)) { problems.Add($"The seed file '{file}' is missing."); continue; }
            foreach (var proposal in Lines(file, bytes,
                line => new PackSource { Id = manifest.Id, Version = manifest.Version.Value, Digest = bundle.Digest, File = file, Line = line }, problems))
            {
                var at = proposal.Source.Excerpt!;
                foreach (var error in ModuleProposalAdmission.AdmitSeed(manifest, proposal)) problems.Add(at + ": " + error);
                if (!keys.Add((proposal.Type, proposal.Key))) problems.Add(at + $": repeats {proposal.Type} '{proposal.Key}'.");
                records.Add(proposal);
            }
        }
        if (records.Count > MaximumRecords) problems.Add($"A pack holds at most {MaximumRecords} records; this one has {records.Count}.");
        foreach (var record in records)
            foreach (var link in record.Links)
            {
                // An undeclared relation was already reported with its line by the admission check.
                if (manifest.Ontology!.Relations.SingleOrDefault(r => r.Name == link.Relation && r.From == record.Type) is not { } relation) continue;
                if (!keys.Contains((relation.To, link.Target)))
                    problems.Add($"{record.Source.Pack!.File}:{record.Source.Pack.Line}: links {link.Relation} to {relation.To} '{link.Target}', which the pack does not hold.");
            }
        if (problems.Count > 0) throw new KnowledgePackException(problems);
        return new KnowledgePack(manifest, bundle, Ordered(manifest, records), icon);
    }

    // A PNG by its signature and header, small enough to show anywhere; the bytes are never decoded here.
    private static string? IconProblem(ReadOnlySpan<byte> image)
    {
        if (image.Length > MaximumIconBytes) return $"is larger than {MaximumIconBytes / 1024} KiB";
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (image.Length < 24 || !image[..8].SequenceEqual(signature) || !image.Slice(12, 4).SequenceEqual("IHDR"u8)) return "is not a PNG";
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(image.Slice(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(image.Slice(20, 4));
        return width is >= 16 and <= 512 && height is >= 16 and <= 512 ? null : "is not 16 to 512 pixels a side";
    }

    /// <summary>
    /// The records of one file in the pack line format: one JSON object per line, <c>{"type","key","properties","links"}</c>,
    /// no blank line before the end, each line at most <see cref="MaximumLineBytes"/>. Every malformed line adds a problem.
    /// </summary>
    internal static List<ModuleProposal> Lines(string file, ReadOnlyMemory<byte> bytes, Func<int, PackSource> source, List<string> problems)
    {
        var records = new List<ModuleProposal>();
        var lines = Utf8(bytes, file).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var at = file + ":" + (index + 1);
            if (line.Length == 0)
            {
                if (index != lines.Length - 1) problems.Add(at + ": blank line.");
                continue;
            }
            if (Encoding.UTF8.GetByteCount(line) > MaximumLineBytes) { problems.Add(at + $": longer than {MaximumLineBytes} bytes."); continue; }
            if (Record(line, at, source(index + 1), problems) is { } proposal) records.Add(proposal);
        }
        return records;
    }

    private static ModuleProposal? Record(string line, string at, PackSource source, List<string> problems)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { problems.Add(at + ": a seed record is a JSON object."); return null; }
            var unknown = root.EnumerateObject().Select(p => p.Name).Where(n => n is not ("type" or "key" or "properties" or "links")).ToArray();
            if (unknown.Length > 0) { problems.Add(at + ": unknown field " + string.Join(", ", unknown) + "."); return null; }
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
            { problems.Add(at + ": a seed record needs a string type and key."); return null; }
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("properties", out var props))
            {
                if (props.ValueKind != JsonValueKind.Object) { problems.Add(at + ": properties is an object."); return null; }
                foreach (var property in props.EnumerateObject()) properties[property.Name] = property.Value.Clone();
            }
            var links = new List<ProposedLink>();
            if (root.TryGetProperty("links", out var linked))
            {
                if (linked.ValueKind != JsonValueKind.Array) { problems.Add(at + ": links is an array."); return null; }
                foreach (var link in linked.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Object || link.EnumerateObject().Any(p => p.Name is not ("relation" or "target"))
                        || !link.TryGetProperty("relation", out var relation) || relation.ValueKind != JsonValueKind.String
                        || !link.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.String)
                    { problems.Add(at + ": a link is {\"relation\", \"target\"} with string values."); return null; }
                    links.Add(new ProposedLink { Relation = relation.GetString()!, Target = target.GetString()! });
                }
            }
            return new ModuleProposal
            {
                Type = type.GetString()!, Key = key.GetString()!, Properties = properties.AsReadOnly(), Links = links.AsReadOnly(),
                Source = new ProposalSource { Excerpt = at, Pack = source },
            };
        }
        catch (JsonException e) { problems.Add(at + ": not valid JSON (" + e.Message + ")."); return null; }
    }

    // Targets first: a record's links resolve only to records already written in the same import.
    private static IReadOnlyList<ModuleProposal> Ordered(ModuleManifest manifest, IReadOnlyList<ModuleProposal> records)
    {
        var types = manifest.Ontology!.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var order = DependencyOrder.Resolve(types, types.Keys.Order(StringComparer.Ordinal),
            t => manifest.Ontology.Relations.Where(r => r.From == t.Name).Select(r => r.To).Distinct(StringComparer.Ordinal),
            (parent, id) => new KnowledgePackException([$"{parent} links to unknown type {id}."]),
            cycle => new KnowledgePackException(["The pack's relations form a cycle: " + string.Join(", ", cycle) + "."]));
        var rank = order.Select((t, i) => (t.Name, i)).ToDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);
        return records.Select((r, i) => (r, i)).OrderBy(p => rank[p.r.Type]).ThenBy(p => p.i).Select(p => p.r).ToList().AsReadOnly();
    }

    private static string Utf8(ReadOnlyMemory<byte> bytes, string file)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.Span).TrimStart('﻿'); }
        catch (DecoderFallbackException) { throw new KnowledgePackException([$"'{file}' is not valid UTF-8."]); }
    }
}

/// <summary>A pack that cannot be opened or imported, with every problem found.</summary>
[Experimental("AMEXT001")]
public sealed class KnowledgePackException : InvalidOperationException
{
    /// <summary>Creates the exception with its problems.</summary>
    public KnowledgePackException(IReadOnlyList<string> problems)
        : base("The knowledge pack cannot be used:" + Environment.NewLine + string.Join(Environment.NewLine, (problems ?? []).Select(p => "- " + p))) =>
        Problems = problems ?? [];

    /// <summary>Every problem found.</summary>
    public IReadOnlyList<string> Problems { get; }
}
