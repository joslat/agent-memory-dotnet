namespace AgentMemory.Packs.Format;

/// <summary>Size limits checked before a single byte is inflated or parsed (v1.1 H2). A host may lower them, or raise them for a large pack it trusts.</summary>
public sealed record PackLimits
{
    /// <summary>The defaults: 4,096 files, 64 MiB per file, 256 MiB in all, 512 MiB for the archive, ratio 250:1.</summary>
    public static PackLimits Default { get; } = new();

    /// <summary>The most files a pack may hold.</summary>
    public int MaxFiles { get; init; } = 4096;
    /// <summary>The largest single file, unpacked.</summary>
    public long MaxFileBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>All files together, unpacked.</summary>
    public long MaxTotalBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>The archive itself.</summary>
    public long MaxArchiveBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>The highest compression ratio a deflated entry may declare.</summary>
    public long MaxRatio { get; init; } = 250;
}
