// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Bundles;

/// <summary>Reads a bundle folder into memory once, for <see cref="BundleVerifier"/>.</summary>
[Experimental("AMEXT001")]
public static class BundleFiles
{
    /// <summary>
    /// Every file under <paramref name="root"/> by its <c>/</c>-separated relative path. Links and reparse points are refused,
    /// as are more than 512 files or 16 MiB, before any file is read; each file is then read through the one bounded reader with
    /// what is left of the 16 MiB, so a file that grows or is swapped after the listing is refused (review 7, D8).
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
        var read = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long left = BundleSigning.MaxBytes;
        foreach (var file in files)
        {
            var bytes = BoundedFile.Read(file.FullName, left, "A bundle file");
            left -= bytes.LongLength;
            read[Path.GetRelativePath(folder.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/')] = bytes;
        }
        return read.AsReadOnly();
    }
}
