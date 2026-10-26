namespace AgentMemory.Packs.Format;

/// <summary>A pack's files in memory, read once from a folder or an <c>.ampack</c> file; nothing is extracted to disk.</summary>
internal sealed record PackContent(IReadOnlyDictionary<string, byte[]> Files, string Source);

/// <summary>Reads the two forms of a pack into the same <see cref="PackContent"/>, so one verifier serves both (v1.1 §1, FIG. 1).</summary>
internal static class PackReader
{
    /// <summary>The file extension of the single-file form.</summary>
    internal const string Extension = ".ampack";

    internal static PackContent Read(string path, PackLimits? limits = null) =>
        Directory.Exists(path) ? ReadFolder(path, limits) : ReadArchive(File.ReadAllBytes(path), path, limits);

    internal static PackContent ReadArchive(byte[] archive, string source, PackLimits? limits = null) =>
        new(StrictZip.Read(archive, limits ?? PackLimits.Default), source);

    /// <summary>
    /// Every file under <paramref name="root"/> by its <c>/</c> path, skipping the version-control files allowed at the root
    /// (v1.1 M1). Links and reparse points are refused, and the limits are checked before any file is read.
    /// </summary>
    internal static PackContent ReadFolder(string root, PackLimits? limits = null)
    {
        limits ??= PackLimits.Default;
        var folder = new DirectoryInfo(root);
        if (!folder.Exists) throw new DirectoryNotFoundException($"No pack folder at {root}.");
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("The pack folder is a link.");
        var files = new List<(string Path, FileInfo Info)>();
        Walk(folder, "", files);
        if (files.Count > limits.MaxFiles) throw new InvalidDataException($"A pack holds at most {limits.MaxFiles} files.");
        if (files.FirstOrDefault(f => f.Info.Length > limits.MaxFileBytes) is { Info: not null } big)
            throw new InvalidDataException($"'{big.Path}' is larger than {limits.MaxFileBytes} bytes.");
        if (files.Sum(f => f.Info.Length) > limits.MaxTotalBytes) throw new InvalidDataException($"The pack is larger than {limits.MaxTotalBytes} bytes.");
        return new(files.ToDictionary(f => f.Path, f => File.ReadAllBytes(f.Info.FullName), StringComparer.Ordinal), root);
    }

    private static void Walk(DirectoryInfo folder, string prefix, List<(string, FileInfo)> files)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            if (prefix.Length == 0 && PackPaths.IgnoredAtRoot.Contains(entry.Name)) continue;
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"'{prefix}{entry.Name}' is a link or reparse point.");
            if (entry is DirectoryInfo sub) Walk(sub, prefix + entry.Name + "/", files);
            else if (entry is FileInfo file) files.Add((prefix + entry.Name, file));
        }
    }
}
