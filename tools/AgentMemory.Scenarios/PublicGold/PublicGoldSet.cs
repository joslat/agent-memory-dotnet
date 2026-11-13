namespace AgentMemory.Scenarios.PublicGold;

/// <summary>
/// The independent identity gold sets (ILC-23): where their corpora are, their frozen slices, and building a scenario and
/// its answer key from them. Optional and off by default: nothing here runs unless --identity-gold is given.
/// </summary>
internal static class PublicGoldSet
{
    public const string EcbPlus = "ecbplus";
    public const string ConEl = "conel2";
    public static readonly IReadOnlyList<string> Names = [EcbPlus, ConEl];

    /// <summary>The corpus folder: the argument, else ECBPLUS_DATASET / CONEL2_DATASET, else artifacts/datasets/&lt;name&gt;.</summary>
    public static string? ResolveDataset(string name, string? explicitPath, Func<string, string?> environment, string repositoryRoot)
    {
        var variable = name switch
        {
            EcbPlus => "ECBPLUS_DATASET",
            ConEl => "CONEL2_DATASET",
            _ => throw new ArgumentException($"Unknown identity gold '{name}'; known: {string.Join(", ", Names)}.", nameof(name)),
        };
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        if (environment(variable) is { Length: > 0 } fromEnvironment) return fromEnvironment;
        var fallback = Path.Combine(repositoryRoot, "artifacts", "datasets", name);
        return Directory.Exists(fallback) ? fallback : null;
    }

    public static string SlicePath(string name, string baseDirectory) =>
        Path.Combine(baseDirectory, "scenarios", "public-gold", $"{name}-slice-v1.json");

    /// <summary>How to get the corpus when it is missing (it is never redistributed here).</summary>
    public static string DownloadHint(string name) => name switch
    {
        EcbPlus => "Download ECB+.zip and ECBplus_coreference_sentences.csv from https://github.com/cltl/ecbPlus (folder ECB+_LREC2014), " +
                   "unzip ECB+.zip into artifacts/datasets/ecbplus/extracted, or set ECBPLUS_DATASET / pass --dataset.",
        ConEl => "Download dataset/Conversational_Entity_Linking_Annotations/ConEL22_EL_Test.json from " +
                 "https://github.com/informagi/conversational-entity-linking-2022 into artifacts/datasets/conel2, or set CONEL2_DATASET / pass --dataset.",
        _ => "",
    };

    public static (Scenario Scenario, IdentityGold Gold, string SliceVersion) Build(string name, string slicePath, string dataset, int? sessions) => name switch
    {
        EcbPlus => Tuple(EcbPlusAdapter.Build(EcbPlusAdapter.LoadSlice(slicePath), dataset, sessions), EcbPlusAdapter.SliceVersion),
        ConEl => Tuple(ConElAdapter.Build(ConElAdapter.LoadSlice(slicePath), dataset, sessions), ConElAdapter.SliceVersion),
        _ => throw new ArgumentException($"Unknown identity gold '{name}'.", nameof(name)),
    };

    /// <summary>The slice file the selection rule picks from this corpus (identifiers and hashes, no text).</summary>
    public static string SelectJson(string name, string dataset) => name switch
    {
        EcbPlus => EcbPlusAdapter.SliceJson(EcbPlusAdapter.Select(dataset)),
        ConEl => ConElAdapter.SliceJson(ConElAdapter.Select(dataset)),
        _ => throw new ArgumentException($"Unknown identity gold '{name}'.", nameof(name)),
    };

    private static (Scenario, IdentityGold, string) Tuple((Scenario Scenario, IdentityGold Gold) built, string version) =>
        (built.Scenario, built.Gold, version);
}
