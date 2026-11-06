// SPDX-License-Identifier: Apache-2.0
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The strict ZIP profile of an <c>.ampack</c> file (v1.1 H2). The signature covers the file list, not the archive, so the
/// archive itself must leave no room for two readers to disagree (Master Key, Janus, "My ZIP isn't your ZIP"):
/// <list type="bullet">
/// <item>entries are contiguous from offset 0 and sorted by name; the central directory follows the last entry and the
/// end record follows it, with no prefix, suffix, gap, overlap or comment;</item>
/// <item>each local header equals its central-directory entry (name, method, flags, time, CRC, sizes);</item>
/// <item>methods stored (0) or deflate (8) only; flags 0 (no data descriptors, no encryption); no extra fields; no
/// directory entries; no ZIP64; MS-DOS attributes 0; time fixed at 1980-01-01 00:00;</item>
/// <item>inflation is counted against the declared size, a ratio cap and a total cap, and every CRC is checked; a deflate
/// stream must end exactly where the entry's compressed bytes end (no bytes after it that another reader might use).</item>
/// </list>
/// Reading never extracts to disk: it returns the bytes, which the verifier then checks and the importer then uses.
/// </summary>
internal static class StrictZip
{
    private const uint LocalSignature = 0x04034b50, CentralSignature = 0x02014b50, EndSignature = 0x06054b50;
    private const int LocalHeaderSize = 30, CentralHeaderSize = 46, EndSize = 22;
    private const ushort DosDate1980 = 0x0021; // 1980-01-01
    private const ushort Stored = 0, Deflate = 8;

    /// <summary>Writes <paramref name="files"/> as a strict, deterministic archive (same files, same toolchain, same bytes).</summary>
    /// <param name="files">The entries, by name.</param>
    /// <param name="compress">Whether to deflate an entry where that makes it smaller.</param>
    /// <param name="maxRatio">The largest compression ratio an entry is deflated at; by default the one every reader allows.
    /// Only tests pass more, to write the bomb a reader must refuse.</param>
    internal static byte[] Write(IReadOnlyDictionary<string, byte[]> files, bool compress = true, long? maxRatio = null)
    {
        // Review 1, I1: each entry is deflated into one reused scratch buffer and kept only when smaller, and the archive is
        // written into an array of exactly its size: a large pack costs about twice its size to build, not five to seven times.
        // The writer refuses what no reader would accept, rather than writing it (review 1, L10): a name outside the path
        // grammar, which also keeps every name ASCII.
        if (PackPaths.Problems(files.Keys).FirstOrDefault() is { } problem) throw new InvalidDataException(problem);
        var entries = new List<(string Name, byte[] Content, byte[] Packed, ushort Method)>(files.Count);
        using (var scratch = new MemoryStream())
            foreach (var (name, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var packed = compress ? DeflateBytes(content, scratch, maxRatio ?? PackLimits.Default.MaxRatio) : null;
                entries.Add(packed is null ? (name, content, content, Stored) : (name, content, packed, Deflate));
            }
        var length = entries.Sum(e => (long)LocalHeaderSize + e.Name.Length + e.Packed.Length + CentralHeaderSize + e.Name.Length) + EndSize;
        var buffer = new byte[checked((int)length)];
        using var output = new MemoryStream(buffer, writable: true);
        var central = new List<(string Name, ushort Method, uint Crc, uint Compressed, uint Size, uint Offset)>();
        var header = new byte[CentralHeaderSize];
        foreach (var (name, content, packed, method) in entries)
        {
            var crc = Crc32.Compute(content);
            var offset = checked((uint)output.Position);
            var nameBytes = Encoding.ASCII.GetBytes(name);
            Array.Clear(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header, LocalSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), method == Deflate ? (ushort)20 : (ushort)10);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), method);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), DosDate1980);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), checked((uint)packed.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), checked((uint)content.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), checked((ushort)nameBytes.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0);
            output.Write(header, 0, LocalHeaderSize);
            output.Write(nameBytes);
            output.Write(packed);
            central.Add((name, method, crc, (uint)packed.Length, (uint)content.Length, offset));
        }
        var centralStart = checked((uint)output.Position);
        foreach (var entry in central)
        {
            var nameBytes = Encoding.ASCII.GetBytes(entry.Name);
            Array.Clear(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header, CentralSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 20); // made by: spec 2.0, MS-DOS
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), entry.Method == Deflate ? (ushort)20 : (ushort)10);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), entry.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), DosDate1980);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), entry.Compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), entry.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), checked((ushort)nameBytes.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), entry.Offset);
            output.Write(header);
            output.Write(nameBytes);
        }
        var centralSize = checked((uint)output.Position - centralStart);
        Span<byte> end = stackalloc byte[EndSize];
        end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], checked((ushort)central.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], checked((ushort)central.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], centralSize);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], centralStart);
        output.Write(end);
        if (output.Position != length) throw new InvalidOperationException("The archive's size was computed wrongly.");
        return buffer;
    }

    /// <summary>Reads a strict archive into memory, or throws <see cref="InvalidDataException"/> naming the first rule broken.</summary>
    internal static Dictionary<string, byte[]> Read(byte[] zip, PackLimits limits)
    {
        if (zip.Length > limits.MaxArchiveBytes) throw Bad($"is larger than {limits.MaxArchiveBytes} bytes");
        if (zip.Length < EndSize) throw Bad("is too short to be a ZIP archive");
        var end = zip.AsSpan()[^EndSize..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != EndSignature) throw Bad("does not end with the end-of-central-directory record (a comment or trailing bytes)");
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0) throw Bad("spans disks");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[10..]) != count) throw Bad("has inconsistent entry counts");
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        var centralStart = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != 0) throw Bad("has an archive comment");
        if (count == 0xFFFF || centralSize == 0xFFFFFFFF || centralStart == 0xFFFFFFFF) throw Bad("uses ZIP64");
        if (count > limits.MaxFiles) throw Bad($"holds more than {limits.MaxFiles} files");
        if ((long)centralStart + centralSize != zip.Length - EndSize) throw Bad("has bytes between the central directory and the end record");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long cursor = 0, totalSize = 0;
        var position = (int)centralStart;
        string? previous = null;
        for (var i = 0; i < count; i++)
        {
            if (position + CentralHeaderSize > zip.Length - EndSize) throw Bad("has a truncated central directory");
            var c = zip.AsSpan()[position..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(c) != CentralSignature) throw Bad($"has a bad central-directory entry {i}");
            var madeBy = BinaryPrimitives.ReadUInt16LittleEndian(c[4..]);
            var needed = BinaryPrimitives.ReadUInt16LittleEndian(c[6..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(c[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(c[10..]);
            var time = BinaryPrimitives.ReadUInt16LittleEndian(c[12..]);
            var date = BinaryPrimitives.ReadUInt16LittleEndian(c[14..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(c[16..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(c[20..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(c[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(c[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(c[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(c[32..]);
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(c[34..]);
            var internalAttributes = BinaryPrimitives.ReadUInt16LittleEndian(c[36..]);
            var externalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(c[38..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(c[42..]);
            if (position + CentralHeaderSize + nameLength > zip.Length - EndSize) throw Bad("has a truncated central directory");
            var name = AsciiName(c.Slice(CentralHeaderSize, nameLength));
            var where = $"entry '{name}'";
            if ((madeBy >> 8) != 0 || (madeBy & 0xFF) > 63) throw Bad($"{where} was not made by an MS-DOS profile writer");
            if (needed is not (10 or 20)) throw Bad($"{where} needs ZIP version {needed}");
            if (flags != 0) throw Bad($"{where} sets flags {flags:x4} (data descriptors, encryption and options are refused)");
            if (method is not (Stored or Deflate)) throw Bad($"{where} uses compression method {method}");
            if (time != 0 || date != DosDate1980) throw Bad($"{where} has a time other than 1980-01-01 00:00");
            if (extraLength != 0 || commentLength != 0) throw Bad($"{where} has an extra field or comment");
            if (disk != 0 || internalAttributes != 0 || externalAttributes != 0) throw Bad($"{where} has attributes set");
            if (name.EndsWith('/')) throw Bad($"{where} is a directory entry");
            if (!PackPaths.IsSafe(name)) throw Bad($"{where} is not a safe pack path");
            if (previous is not null && string.CompareOrdinal(previous, name) >= 0) throw Bad($"{where} is out of order or repeated");
            if (!folded.Add(name)) throw Bad($"{where} differs from another only by case");
            if (offset != cursor) throw Bad($"{where} does not start where the previous entry ended");
            if (method == Stored && compressed != size) throw Bad($"{where} is stored with two different sizes");

            // The local header must say exactly what the central directory says.
            if (cursor + LocalHeaderSize + nameLength > centralStart) throw Bad($"{where} runs into the central directory");
            var l = zip.AsSpan()[(int)cursor..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(l) != LocalSignature) throw Bad($"{where} has no local header at its offset");
            if (BinaryPrimitives.ReadUInt16LittleEndian(l[4..]) != needed || BinaryPrimitives.ReadUInt16LittleEndian(l[6..]) != flags
                || BinaryPrimitives.ReadUInt16LittleEndian(l[8..]) != method || BinaryPrimitives.ReadUInt16LittleEndian(l[10..]) != time
                || BinaryPrimitives.ReadUInt16LittleEndian(l[12..]) != date || BinaryPrimitives.ReadUInt32LittleEndian(l[14..]) != crc
                || BinaryPrimitives.ReadUInt32LittleEndian(l[18..]) != compressed || BinaryPrimitives.ReadUInt32LittleEndian(l[22..]) != size
                || BinaryPrimitives.ReadUInt16LittleEndian(l[26..]) != nameLength || BinaryPrimitives.ReadUInt16LittleEndian(l[28..]) != 0
                || !l.Slice(LocalHeaderSize, nameLength).SequenceEqual(c.Slice(CentralHeaderSize, nameLength)))
                throw Bad($"{where} has a local header that differs from its central-directory entry");
            var dataStart = cursor + LocalHeaderSize + nameLength;
            if (dataStart + compressed > centralStart) throw Bad($"{where} runs into the central directory");

            if (size > limits.MaxFileBytes) throw Bad($"{where} is larger than {limits.MaxFileBytes} bytes");
            totalSize += size;
            if (totalSize > limits.MaxTotalBytes) throw Bad($"unpacks to more than {limits.MaxTotalBytes} bytes");
            // A deflated entry of 0 bytes that declares content is an unbounded ratio, refused before its buffer is allocated (review 6, I8).
            if (method == Deflate && (compressed == 0 ? size > 0 : size / compressed > limits.MaxRatio)) throw Bad($"{where} declares a compression ratio above {limits.MaxRatio}:1");
            var content = method == Stored ? zip.AsSpan((int)dataStart, (int)compressed).ToArray() : Inflate(zip, (int)dataStart, (int)compressed, size, where);
            if (Crc32.Compute(content) != crc) throw Bad($"{where} fails its CRC");
            files[name] = content;
            previous = name;
            cursor = dataStart + compressed;
            position += CentralHeaderSize + nameLength;
        }
        if (cursor != centralStart) throw Bad("has bytes between the last entry and the central directory");
        if (position != centralStart + centralSize) throw Bad("has a central directory of the wrong size");
        return files;
    }

    // A deflate stream must end exactly where the entry's compressed bytes end (PACK-harden, review 4.13): with its final
    // block (a truncated stream makes the inflater ask for bytes past the end), and with nothing after it (inflating all but
    // the last byte must then fail, since the final block's last bits are in that byte). Two passes, in large chunks.
    // The entry's compressed bytes are read in place from the archive, and the second pass only counts what it inflates into a
    // small reused buffer (review 1, I1: a deflated entry cost its compressed copy and a second full-size output before).
    private static byte[] Inflate(byte[] zip, int start, int length, uint size, string where)
    {
        var output = new byte[size];
        var (read, complete) = Decode(zip, start, length, output, size, where);
        if (!complete) throw Bad($"{where} has a deflate stream without its final block");
        if (read < size) throw Bad($"{where} inflates to fewer bytes than it declares");
        if (length > 0 && Decode(zip, start, length - 1, null, size, where) is { Complete: true, Read: var again } && again == size)
            throw Bad($"{where} has bytes after the end of its deflate stream");
        return output;
    }

    // Inflates the first `length` bytes of the entry: into `output` (or, when null, a scratch buffer, only counting), how much,
    // and whether the stream ended without asking for more input.
    private static (long Read, bool Complete) Decode(byte[] zip, int start, int length, byte[]? output, uint size, string where)
    {
        using var input = new BoundedInput(zip, start, length);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        var scratch = output is null ? new byte[(int)Math.Min(size + 1L, 64 * 1024)] : null;
        long read = 0;
        var more = false;
        try
        {
            while (read < size)
            {
                var n = output is not null
                    ? inflater.Read(output, (int)read, (int)(size - read))
                    : inflater.Read(scratch!, 0, (int)Math.Min(scratch!.Length, size - read));
                if (n == 0) break;
                read += n;
            }
            more = read == size && inflater.Read(new byte[1], 0, 1) != 0;
        }
        // Cut one byte short, a stream may also read as damaged: that is not a complete stream either.
        catch (InvalidDataException) when (output is null) { return (read, false); }
        // Damaged, named as the format names it rather than in the runtime's words, as the second verifier says it (review 7, M8).
        catch (InvalidDataException e) { throw new InvalidDataException($"The archive {where} is not a valid deflate stream.", e); }
        if (more) throw Bad($"{where} inflates to more bytes than it declares");
        return (read, !input.AskedPastEnd);
    }

    /// <summary>The first bytes of a buffer, noting whether the inflater asked for more than there are.</summary>
    // Reads `length` bytes of `data` from `start`: an entry's compressed bytes, straight from the archive (no copy).
    private sealed class BoundedInput(byte[] data, int start, int length) : Stream
    {
        private int _position;

        public bool AskedPastEnd { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            var n = Math.Min(buffer.Length, length - _position);
            if (n == 0) { AskedPastEnd = true; return 0; }
            data.AsSpan(start + _position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // The deflated bytes when smaller than the content and within the ratio every reader allows, otherwise null (the entry is
    // stored; review 7, F3: a sparse file deflated past 250:1 was written and then refused); `scratch` is reused across entries.
    private static byte[]? DeflateBytes(byte[] content, MemoryStream scratch, long maxRatio)
    {
        scratch.SetLength(0);
        using (var deflater = new DeflateStream(scratch, CompressionLevel.SmallestSize, leaveOpen: true)) deflater.Write(content);
        return scratch.Length < content.Length && scratch.Length > 0 && content.LongLength / scratch.Length <= maxRatio
            ? scratch.ToArray() : null;
    }

    private static string AsciiName(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) if (b is < 0x20 or > 0x7E) throw Bad("has an entry name that is not printable ASCII");
        return Encoding.ASCII.GetString(bytes);
    }

    private static InvalidDataException Bad(string what) => new($"The archive {what}.");
}

/// <summary>CRC-32 (IEEE 802.3), as ZIP uses it.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
