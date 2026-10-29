namespace AgentMemory.Packs.Format;

/// <summary>A pack's files in memory, read once from a folder or an <c>.ampack</c> file; nothing is extracted to disk.</summary>
internal sealed record PackContent(IReadOnlyDictionary<string, byte[]> Files, string Source);

/// <summary>Reads the two forms of a pack into the same <see cref="PackContent"/>, so one verifier serves both (v1.1 §1, FIG. 1).</summary>
internal static class PackReader
{
    /// <summary>The file extension of the single-file form.</summary>
    internal const string Extension = ".ampack";

    internal static PackContent Read(string path, PackLimits? limits = null) =>
        Directory.Exists(path) ? ReadFolder(path, limits) : ReadArchive(ReadArchiveFile(path, limits), path, limits);

    /// <summary>
    /// An <c>.ampack</c> file's bytes, its size checked against the archive limit before one byte is read (PACK-harden,
    /// review 4.14), and it must be a plain file, not a link.
    /// </summary>
    internal static byte[] ReadArchiveFile(string path, PackLimits? limits = null)
    {
        limits ??= PackLimits.Default;
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException($"No pack at {path}.", path);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"{path} is a link or reparse point.");
        if (file.Length > limits.MaxArchiveBytes) throw new InvalidDataException($"The archive is larger than {limits.MaxArchiveBytes} bytes.");
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        // A plain file is read into an array of exactly its size, once (review 1, I1: a growing buffer and its copy held two to
        // three times the archive). Anything that reads past its reported length (a pipe or a device reports 0 and never ends,
        // red team 12; or a file growing as it is read) falls back to a bounded read.
        var exact = new byte[file.Length];
        var filled = 0;
        int read;
        while (filled < exact.Length && (read = stream.Read(exact, filled, exact.Length - filled)) > 0) filled += read;
        var more = new byte[1];
        if (filled == exact.Length && stream.Read(more, 0, 1) == 0) return exact;
        using var buffer = new MemoryStream();
        buffer.Write(exact, 0, filled);
        if (filled == exact.Length) buffer.Write(more, 0, 1);
        var chunk = new byte[81920];
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limits.MaxArchiveBytes) throw new InvalidDataException($"The archive is larger than {limits.MaxArchiveBytes} bytes.");
        }
        return buffer.ToArray();
    }

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
        Walk(folder, "", files, limits, depth: 1);
        if (files.Count > limits.MaxFiles) throw new InvalidDataException($"A pack holds at most {limits.MaxFiles} files.");
        if (files.FirstOrDefault(f => f.Info.Length > limits.MaxFileBytes) is { Info: not null } big)
            throw new InvalidDataException($"'{big.Path}' is larger than {limits.MaxFileBytes} bytes.");
        if (files.Sum(f => f.Info.Length) > limits.MaxTotalBytes) throw new InvalidDataException($"The pack is larger than {limits.MaxTotalBytes} bytes.");
        // Review 1, L2: each file read with its bound, not by the size listed before: a file that grew since the listing, or one
        // that is not a plain file and reads past its reported size, is refused rather than read without limit.
        var read = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var (path, info) in files)
        {
            var bytes = Bounded(info.FullName, limits.MaxFileBytes, path);
            total += bytes.Length;
            if (total > limits.MaxTotalBytes) throw new InvalidDataException($"The pack is larger than {limits.MaxTotalBytes} bytes.");
            read[path] = bytes;
        }
        return new(read, root);
    }

    // A file's bytes, at most `limit` of them: more is refused.
    private static byte[] Bounded(string file, long limit, string path)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + n > limit) throw new InvalidDataException($"'{path}' is larger than {limit} bytes.");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    // Bounded while it walks (review 1, L2): no deeper than a pack path may be, no more files than a pack may hold.
    private static void Walk(DirectoryInfo folder, string prefix, List<(string, FileInfo)> files, PackLimits limits, int depth)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
        {
            if (prefix.Length == 0 && PackPaths.IgnoredAtRoot.Contains(entry.Name)) continue;
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"'{prefix}{entry.Name}' is a link or reparse point.");
            if (entry is DirectoryInfo sub)
            {
                if (depth >= PackPaths.MaximumSegments) throw new InvalidDataException($"'{prefix}{entry.Name}/' is deeper than a pack path may be.");
                Walk(sub, prefix + entry.Name + "/", files, limits, depth + 1);
            }
            else if (entry is FileInfo file)
            {
                files.Add((prefix + entry.Name, file));
                if (files.Count > limits.MaxFiles) throw new InvalidDataException($"A pack holds at most {limits.MaxFiles} files.");
            }
        }
    }
}
