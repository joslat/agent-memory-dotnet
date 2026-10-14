#pragma warning disable AMEXT001 // Explicit operator tooling consumes the held module API.
using System.Security.Cryptography;
using System.Text;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;

namespace AgentMemory.Cli.Commands;

/// <summary>Validated, frozen operator input; no module activation or database access.</summary>
internal sealed record ModuleCommandInput
{
    private ModuleCommandInput() { }
    public required string Operation { get; init; }
    public string? Actor { get; init; }
    public required string ProfileSha256 { get; init; }
    public required string Store { get; init; }
    public required ModuleResolution Resolution { get; init; }
    public required IReadOnlyList<ModuleMigrationStep> Scripts { get; init; }
    public required IReadOnlyList<ModuleSourceIdentity> Sources { get; init; }
    public required IReadOnlyList<string> TargetHostFeatures { get; init; }
    public required string CompositionFingerprint { get; init; }

    public static ModuleCommandInput Load(CliArgs args)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "profile", "store", "actor", "host-features", "uri", "user", "password", "database", "embedding-dimensions" };
        foreach (var option in args.Options)
        {
            if (!allowed.Contains(option.Key)) throw new FormatException("Unknown modules option: --" + option.Key);
            if (string.IsNullOrWhiteSpace(option.Value)) throw new FormatException("--" + option.Key + " requires a value.");
        }
        var operation = args.Subcommand?.ToLowerInvariant();
        if (args.Positionals.Count != 1 || operation is not ("plan" or "apply"))
            throw new FormatException("modules requires exactly one operation: plan or apply.");
        var actor = args.Get("actor");
        if (operation == "apply" && string.IsNullOrWhiteSpace(actor))
            throw new FormatException("modules apply requires an explicit --actor.");
        if (string.IsNullOrWhiteSpace(args.Get("profile"))) throw new FormatException("modules requires --profile <path>.");
        var profilePath = Path.GetFullPath(args.Get("profile")!);
        var profileBytes = ReadSnapshot(profilePath, ModuleCompositionProfile.MaximumUtf8Bytes);
        var profile = ModuleCompositionProfile.Parse(Decode(profileBytes));
        var store = args.Get("store");
        if (args.HasFlag("store") && string.IsNullOrWhiteSpace(store)) throw new FormatException("--store requires an alias.");
        if (store is null)
        {
            if (profile.Stores.Count != 1) throw new FormatException("Specify --store when the profile does not declare exactly one store.");
            store = profile.Stores.Keys.Single();
        }
        if (!profile.Stores.ContainsKey(store)) throw new FormatException("Unknown profile store: " + store);
        foreach (var binding in profile.Stores)
            if (binding.Value.Provider != "neo4j-direct")
                throw new FormatException("Unsupported module migration provider: " + binding.Value.Provider);

        // These are explicit target-host declarations, not probes of running host services.
        var features = new HashSet<string>(StringComparer.Ordinal) { "ModuleSchema" };
        if (args.HasFlag("host-features"))
        {
            if (string.IsNullOrWhiteSpace(args.Get("host-features"))) throw new FormatException("--host-features requires a comma-separated list.");
            foreach (var feature in args.Get("host-features")!.Split(',', StringSplitOptions.TrimEntries))
            {
                if (feature is not ("History" or "Reminders" or "ModuleSchema"))
                    throw new FormatException("Unknown target host feature: " + feature);
                features.Add(feature);
            }
        }
        var manifests = new List<ModuleManifest>();
        var scripts = new List<ModuleMigrationStep>();
        var sources = new List<ModuleSourceIdentity>();
        foreach (var entry in profile.Modules)
        {
            var path = Path.GetFullPath((entry.Assembly ?? entry.Manifest)!, Path.GetDirectoryName(profilePath)!);
            var bytes = ReadSnapshot(path, entry.Assembly is null ? ModuleManifest.MaximumUtf8Bytes : ModuleManifest.MaximumAssemblyBytes);
            var sha = Hash(bytes);
            if (entry.Sha256 is not null && entry.Sha256 != sha)
                throw new FormatException("Source SHA-256 does not match the profile pin for " + entry.Id);
            var manifest = entry.Assembly is null ? ModuleManifest.Parse(Decode(bytes)) : ModuleManifest.LoadFromAssemblyImage(bytes);
            if (manifest.Id != entry.Id) throw new FormatException("Source module identity does not match profile entry " + entry.Id);
            if (!entry.Version.Contains(manifest.Version)) throw new FormatException("Source module version does not match profile entry " + entry.Id);
            manifests.Add(manifest);
            // Manifest and scripts use exactly the same captured image; there is no second filesystem read.
            if (entry.Assembly is not null) scripts.AddRange(ModuleMigrationScripts.LoadFromAssemblyImage(bytes));
            sources.Add(new(entry.Id, manifest.Version.Value, path, entry.Assembly is null ? "manifest" : "assembly", sha));
        }
        var sdkVersion = ModuleVersion.Parse(typeof(ModuleManifest).Assembly.GetName().Version!.ToString(3));
        var composition = ModuleProfileResolver.Resolve(profile, manifests, sdkVersion, features,
            profile.Stores.ToDictionary(s => s.Key, _ => "neo4j-direct", StringComparer.Ordinal));
        if (!composition.Succeeded) throw new FormatException(string.Join(Environment.NewLine, composition.Errors));
        var resolution = composition.ForStore(store);
        var selectedIds = resolution.Modules.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return new ModuleCommandInput
        {
            Operation = operation, Actor = actor, ProfileSha256 = Hash(profileBytes), Store = store,
            Resolution = resolution, CompositionFingerprint = composition.CompositionFingerprint!,
            Scripts = scripts.Where(s => selectedIds.Contains(s.ModuleId)).ToList().AsReadOnly(),
            Sources = sources.AsReadOnly(), TargetHostFeatures = features.Order(StringComparer.Ordinal).ToList().AsReadOnly()
        };
    }

    private static byte[] ReadSnapshot(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new FormatException("Module input size exceeds " + maximum + " bytes: " + path);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Module source changed while being read: " + path);
        return bytes;
    }
    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException e) { throw new FormatException("Module JSON must be valid UTF-8.", e); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed record ModuleSourceIdentity(string ModuleId, string Version, string Path, string Kind, string Sha256);
