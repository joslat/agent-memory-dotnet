using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// PACK-harden, from the 26 Oct review: bytes after a deflate stream's end refuse the archive (4.13), whether one byte or more
/// than the reader's byte-at-a-time tail; and an archive file's size is checked before it is read (4.14).
/// </summary>
public sealed class HardenArchiveTests
{
    private static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a deflated record line, again and again\n", 200)));

    private static byte[] Deflated(byte[] content)
    {
        using var output = new MemoryStream();
        using (var deflater = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) deflater.Write(content);
        return output.ToArray();
    }

    // One deflated entry whose compressed bytes are given, in the strict profile otherwise.
    private static byte[] Archive(string name, byte[] content, byte[] packed)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var crc = Crc32.Compute(content);
        var local = new byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(12), 0x0021);
        BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)packed.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), (uint)content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)nameBytes.Length);
        var central = new byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(central, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(6), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(10), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(14), 0x0021);
        BinaryPrimitives.WriteUInt32LittleEndian(central.AsSpan(16), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(central.AsSpan(20), (uint)packed.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(central.AsSpan(24), (uint)content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(central.AsSpan(28), (ushort)nameBytes.Length);
        var centralStart = local.Length + nameBytes.Length + packed.Length;
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)(central.Length + nameBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)centralStart);
        return [.. local, .. nameBytes, .. packed, .. central, .. nameBytes, .. end];
    }

    [Fact]
    public void A_deflate_stream_that_ends_exactly_where_its_bytes_end_reads()
    {
        StrictZip.Read(Archive("records/a.jsonl", Text, Deflated(Text)), PackLimits.Default)["records/a.jsonl"].Should().Equal(Text);

        // Larger than the reader's byte-at-a-time tail: big chunks first, then the tail, still exact.
        var noise = new byte[300_000];
        new Random(4_13).NextBytes(noise);
        StrictZip.Read(Archive("assets/noise.bin", noise, Deflated(noise)), PackLimits.Default)["assets/noise.bin"].Should().Equal(noise);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(70_000)]
    public void Bytes_after_the_end_of_a_deflate_stream_refuse_the_archive(int extra)
    {
        var junk = Enumerable.Repeat((byte)0x5A, extra).ToArray();

        FluentActions.Invoking(() => StrictZip.Read(Archive("records/a.jsonl", Text, [.. Deflated(Text), .. junk]), PackLimits.Default))
            .Should().Throw<InvalidDataException>().WithMessage("*bytes after the end of its deflate stream*");
    }

    [Fact]
    public void An_archive_file_larger_than_the_limit_is_refused_before_it_is_read()
    {
        var path = Path.Combine(Path.GetTempPath(), "harden-" + Guid.NewGuid().ToString("N") + ".ampack");
        try
        {
            using (var file = File.Create(path)) file.SetLength(4096);
            FluentActions.Invoking(() => PackReader.ReadArchiveFile(path, PackLimits.Default with { MaxArchiveBytes = 1024 }))
                .Should().Throw<InvalidDataException>().WithMessage("*larger than 1024 bytes*");
            FluentActions.Invoking(() => PackReader.Read(path, PackLimits.Default with { MaxArchiveBytes = 1024 }))
                .Should().Throw<InvalidDataException>().WithMessage("*larger than 1024 bytes*");
            FluentActions.Invoking(() => PackReader.ReadArchiveFile(path + ".missing")).Should().Throw<FileNotFoundException>();
        }
        finally { File.Delete(path); }
    }
}
