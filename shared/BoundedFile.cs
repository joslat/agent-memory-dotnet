// One namespace per assembly that compiles this file, as shared/Nfc.cs does: the format library and the host library each
// read files with it, and an assembly that sees both must not find two types of one name.
#if AGENTMEMORY_PACKS_FORMAT
namespace AgentMemory.Packs.Format;
#else
namespace AgentMemory.Extensibility.Modules;
#endif

/// <summary>
/// The one way pack code reads a file (review 6, M2: bounded reading was a helper some commands used, while the library read
/// policies, ledgers, activation records and indexes whole and through links). Compiled into each assembly that reads files,
/// from this one source, so the rule cannot drift between them.
/// </summary>
internal static class BoundedFile
{
    /// <summary>
    /// A file's bytes, at most <paramref name="maximum"/> of them: never through a link nor from a folder, checked before it is
    /// opened and again on what was opened (review 6, L5: the opened handle's own times and length must be the path's, so a
    /// link swapped in for the open and out again is refused); a pipe or a device, whose length reads as 0, is read with the
    /// same bound, checked before each block is kept.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no file there.</exception>
    /// <exception cref="InvalidDataException">It is a link or a folder, larger than <paramref name="maximum"/>, or changed as it was opened.</exception>
    internal static byte[] Read(string path, long maximum, string what, FileShare share = FileShare.Read)
    {
        var file = new FileInfo(path);
        if (Directory.Exists(path)) throw new InvalidDataException($"{what} at {path} is a folder.");
        if (!file.Exists) throw new FileNotFoundException($"No file at {path}.", path);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget is not null) throw new InvalidDataException($"{what} at {path} is a link or reparse point.");
        if (file.Length > maximum) throw new InvalidDataException($"{what} is larger than {maximum} bytes.");
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, share, 81920, FileOptions.SequentialScan);
        // What was opened is what was checked: the path is still no link, and the handle is the file the path names now (its
        // own write and creation times, and its length unless it reports none).
        var opened = new FileInfo(file.FullName);
        if (opened.Attributes.HasFlag(FileAttributes.ReparsePoint) || opened.LinkTarget is not null
            || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != opened.LastWriteTimeUtc || File.GetCreationTimeUtc(stream.SafeFileHandle) != opened.CreationTimeUtc
            || stream.CanSeek && stream.Length != opened.Length && opened.Length != 0)
            throw new InvalidDataException($"{what} at {path} changed while it was opened.");
        // A plain file is read into an array of exactly its size, once (review 1, I1: a growing buffer and its copy held two to
        // three times the archive). Anything that reads past its reported length (a pipe or a device reports 0 and never ends,
        // red team 12; or a file growing as it is read) falls back to a bounded read.
        var exact = new byte[Math.Min(stream.CanSeek ? stream.Length : 0, maximum)];
        var filled = 0;
        int read;
        while (filled < exact.Length && (read = stream.Read(exact, filled, exact.Length - filled)) > 0) filled += read;
        var more = new byte[1];
        if (filled == exact.Length && stream.Read(more, 0, 1) == 0) return exact;
        using var buffer = new MemoryStream();
        buffer.Write(exact, 0, filled);
        if (filled == exact.Length)
        {
            buffer.Write(more, 0, 1);
            if (buffer.Length > maximum) throw new InvalidDataException($"{what} is larger than {maximum} bytes.");
        }
        var chunk = new byte[81920];
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            // Checked before the write (review 4: a device that never ends grew the buffer past the limit first).
            if (buffer.Length + read > maximum) throw new InvalidDataException($"{what} is larger than {maximum} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
