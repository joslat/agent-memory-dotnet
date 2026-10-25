using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Bundles;

/// <summary>Reads a bundle folder into memory once, for <see cref="BundleVerifier"/>.</summary>
[Experimental("AMEXT001")]
public static class BundleFiles
{
    /// <summary>
    /// Every file under <paramref name="root"/> by its <c>/</c>-separated relative path. Links and reparse points are refused,
    /// as are more than 512 files or 16 MiB, before any file is read.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> ReadDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var folder = new DirectoryInfo(root);
        if (!folder.Exists) throw new DirectoryNotFoundException($"No bundle folder at {root}.");
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("The bundle folder is a link.");
        var entries = folder.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList();
        if (entries.Any(e => e.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidDataException("The bundle folder contains a link or reparse point.");
        var files = entries.OfType<FileInfo>().ToList();
        if (files.Count > BundleSigning.MaxFiles) throw new InvalidDataException($"A bundle holds at most {BundleSigning.MaxFiles} files.");
        if (files.Sum(f => f.Length) > BundleSigning.MaxBytes) throw new InvalidDataException("The bundle is larger than 16 MiB.");
        return files.ToDictionary(
            f => Path.GetRelativePath(folder.FullName, f.FullName).Replace(Path.DirectorySeparatorChar, '/'),
            f => File.ReadAllBytes(f.FullName), StringComparer.Ordinal).AsReadOnly();
    }
}
