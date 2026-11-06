using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>The strict ZIP profile against the attacks that broke JAR, APK v1 and wheel signing (red-team H2).</summary>
public sealed class StrictZipTests
{
    private static Dictionary<string, byte[]> Two() => new(StringComparer.Ordinal)
    {
        ["abcd"] = Encoding.ASCII.GetBytes("first file, long enough to be worth deflating: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        ["wxyz"] = Encoding.ASCII.GetBytes("second"),
    };

    private static Action Reading(byte[] zip) => () => StrictZip.Read(zip, PackLimits.Default);

    [Fact]
    public void Its_own_archives_read_back_exactly()
    {
        var files = Two();
        StrictZip.Read(StrictZip.Write(files), PackLimits.Default).Should().BeEquivalentTo(files);
        StrictZip.Read(StrictZip.Write(files, compress: false), PackLimits.Default).Should().BeEquivalentTo(files);
    }

    [Fact]
    public void Bytes_before_the_first_entry_are_refused()
    {
        var zip = StrictZip.Write(Two());
        Reading([.. "PK-prefix"u8.ToArray(), .. zip]).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Bytes_after_the_end_record_are_refused()
    {
        var zip = StrictZip.Write(Two());
        Reading([.. zip, 0x00]).Should().Throw<InvalidDataException>().WithMessage("*does not end*");
    }

    [Fact]
    public void A_repeated_entry_name_is_refused()
    {
        var zip = StrictZip.Write(Two());
        Rename(zip, "wxyz", "abcd");
        Reading(zip).Should().Throw<InvalidDataException>().WithMessage("*out of order or repeated*");
    }

    [Fact]
    public void A_path_that_climbs_out_is_refused()
    {
        var zip = StrictZip.Write(Two());
        Rename(zip, "wxyz", "../x");
        Reading(zip).Should().Throw<InvalidDataException>().WithMessage("*not a safe pack path*");
    }

    [Fact]
    public void A_local_header_that_disagrees_with_the_central_directory_is_refused()
    {
        var zip = StrictZip.Write(Two());
        // The second entry's local name only: a reader of local headers would see another file than a central-directory reader.
        var local = IndexOf(zip, "wxyz"u8.ToArray(), 0);
        zip[local] = (byte)'v';
        Reading(zip).Should().Throw<InvalidDataException>().WithMessage("*local header that differs*");
    }

    [Fact]
    public void Flags_extra_fields_and_attributes_are_refused()
    {
        var withFlag = StrictZip.Write(Two());
        BinaryPrimitives.WriteUInt16LittleEndian(withFlag.AsSpan(CentralOffset(withFlag) + 8), 0x0008);
        Reading(withFlag).Should().Throw<InvalidDataException>().WithMessage("*flags*");

        var withAttributes = StrictZip.Write(Two());
        BinaryPrimitives.WriteUInt32LittleEndian(withAttributes.AsSpan(CentralOffset(withAttributes) + 38), 0x81A40000);
        Reading(withAttributes).Should().Throw<InvalidDataException>().WithMessage("*attributes*");
    }

    [Fact]
    public void An_ordinary_zip_tool_archive_is_not_a_strict_archive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in Two())
                using (var entry = archive.CreateEntry(name).Open()) entry.Write(bytes);
        Reading(stream.ToArray()).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_bomb_is_refused_from_its_declared_ratio_before_inflating()
    {
        // The writer stores what it would deflate past the ratio a reader allows (review 7, F3), so the bomb is written past it.
        var zip = StrictZip.Write(new Dictionary<string, byte[]> { ["bomb"] = new byte[10_000_000] }, maxRatio: long.MaxValue);
        Reading(zip).Should().Throw<InvalidDataException>().WithMessage("*compression ratio*");
    }

    [Fact]
    public void An_entry_that_inflates_beyond_its_declared_size_is_refused()
    {
        var zip = StrictZip.Write(new Dictionary<string, byte[]> { ["data"] = Encoding.ASCII.GetBytes(new string('a', 2000)) });
        // Shrink the declared size in both headers, so the deflate stream holds more than the entry admits.
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(22), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(CentralOffset(zip) + 24), 1000);
        Reading(zip).Should().Throw<InvalidDataException>().WithMessage("*more bytes than it declares*");
    }

    [Fact]
    public void A_corrupted_body_fails_its_CRC()
    {
        var zip = StrictZip.Write(Two(), compress: false);
        zip[30 + 4 + 2] ^= 0x20; // inside the first entry's stored bytes
        Reading(zip).Should().Throw<InvalidDataException>().WithMessage("*CRC*");
    }

    [Fact]
    public void Names_that_differ_only_by_case_or_alias_a_Windows_device_are_refused()
    {
        PackPaths.Problems(["Notes.json", "notes.json"]).Should().ContainSingle().Which.Should().Contain("only by case");
        PackPaths.IsSafe("records/CON.jsonl").Should().BeFalse();
        PackPaths.IsSafe("records/a.").Should().BeFalse();
        PackPaths.IsSafe("/abs").Should().BeFalse();
        PackPaths.IsSafe("a/../b").Should().BeFalse();
        PackPaths.IsSafe("records/note.jsonl").Should().BeTrue();
    }

    private static int CentralOffset(byte[] zip) => (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));

    /// <summary>Renames an entry in both its local header and its central-directory entry (same length).</summary>
    private static void Rename(byte[] zip, string from, string to)
    {
        var a = Encoding.ASCII.GetBytes(from);
        var b = Encoding.ASCII.GetBytes(to);
        for (var at = IndexOf(zip, a, 0); at >= 0; at = IndexOf(zip, a, at + 1)) b.CopyTo(zip, at);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        var found = haystack.AsSpan(start).IndexOf(needle);
        return found < 0 ? -1 : found + start;
    }

    [Fact]
    public void Review9_D1_every_size_limit_refuses_the_archive_that_passes_it()
    {
        // Only the ratio had a guard; deleting the per-file or total cap let every read door inflate far past it unseen.
        var zip = StrictZip.Write(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["a"] = new byte[2000], ["b"] = new byte[2000] });
        FluentActions.Invoking(() => StrictZip.Read(zip, PackLimits.Default with { MaxFileBytes = 1999 })).Should().Throw<InvalidDataException>().WithMessage("*larger than 1999 bytes*");
        FluentActions.Invoking(() => StrictZip.Read(zip, PackLimits.Default with { MaxTotalBytes = 3999 })).Should().Throw<InvalidDataException>().WithMessage("*unpacks to more than 3999 bytes*");
        FluentActions.Invoking(() => StrictZip.Read(zip, PackLimits.Default with { MaxArchiveBytes = zip.Length - 1 })).Should().Throw<InvalidDataException>().WithMessage("*is larger than*");
        FluentActions.Invoking(() => StrictZip.Read(zip, PackLimits.Default with { MaxFiles = 1 })).Should().Throw<InvalidDataException>().WithMessage("*holds more than 1 files*");
        StrictZip.Read(zip, PackLimits.Default with { MaxFileBytes = 2000, MaxTotalBytes = 4000, MaxArchiveBytes = zip.Length, MaxFiles = 2 }).Should().HaveCount(2);
    }

    [Fact]
    public void Review9_D2_a_json_file_over_one_mebibyte_is_refused_before_it_is_parsed_verified_or_not()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["ontology.json"] = new byte[(1 << 20) + 1] };
        PackContentBounds.Problems(files, verified: false).Should().ContainSingle().Which.Should().Contain("'ontology.json' is larger than 1 MiB");
        PackContentBounds.Problems(files, verified: true).Should().ContainSingle().Which.Should().Contain("'ontology.json' is larger than 1 MiB");
    }
}
