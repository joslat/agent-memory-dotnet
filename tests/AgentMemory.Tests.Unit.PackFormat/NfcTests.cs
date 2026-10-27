using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The format's own NFC (PACK-conformance-v12) agrees with the platform's normalizer, so a host under invariant
/// globalization, where .NET reads every string as normalized, refuses exactly what a host with ICU and the Python verifier
/// refuse.
/// </summary>
public sealed class NfcTests
{
    // The platform's verdict, or null where it refuses the code points outright (noncharacters such as U+FFFE).
    private static bool? Platform(string s)
    {
        try { return s.IsNormalized(NormalizationForm.FormC); }
        catch (ArgumentException) { return null; }
    }

    [Fact]
    public void Every_single_code_point_agrees_with_the_platform()
    {
        "Å".Normalize(NormalizationForm.FormD).Length.Should().Be(2, "this test needs a process that can normalize");
        var disagreements = new List<int>();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF) continue;
            var s = char.ConvertFromUtf32(cp);
            if (Platform(s) is { } platform && Nfc.Check(s) != platform) disagreements.Add(cp);
        }
        // Code points assigned after the tables' Unicode version may differ; none of them should be among the first 0x30000.
        disagreements.Where(cp => cp < 0x30000).Should().BeEmpty(string.Join(", ", disagreements.Take(20).Select(cp => cp.ToString("X4"))));
    }

    [Fact]
    public void Random_mixes_of_letters_marks_and_jamo_agree_with_the_platform()
    {
        int[] pool =
        [
            'a', 'e', 'q', 'A', 'o', 'u', 0x00E9, 0x00C5, 0x0301, 0x0300, 0x0323, 0x0327, 0x031B, 0x0345, 0x0308, 0x0344, 0x0340, 0x0958,
            0x1100, 0x1101, 0x1161, 0x1162, 0x11A8, 0x11A9, 0xAC00, 0xAC01, 0x0CC6, 0x0CD5, 0x0CC2, 0x0DD9, 0x0DCF, 0x0DDF, 0x1B05, 0x1B35,
            0x05D0, 0x05B4, 0x0915, 0x093C, 0x2126, 0x212B, 0x1E0A, 0x0307, 0x1E9B, 0x0FB3, 0x0F80, 0x0F81, 0x3099, 0x304B, 0x309A, 0x1D15E,
            0x1EA0, 0x030A, 0x01D5, 0x0F73, 0x11A7, 0xD7A3, 0xFB2C, 0x05BC, 0x05C1, 0x0B47, 0x0B3E, 0x0B57, 0x1025, 0x102E,
        ];
        var random = new Random(15);
        var disagreements = new List<string>();
        for (var run = 0; run < 200_000; run++)
        {
            var builder = new StringBuilder();
            for (var n = random.Next(1, 7); n > 0; n--) builder.Append(char.ConvertFromUtf32(pool[random.Next(pool.Length)]));
            var s = builder.ToString();
            if (Platform(s) is { } platform && Nfc.Check(s) != platform)
                disagreements.Add(string.Join(" ", s.EnumerateRunes().Select(r => r.Value.ToString("X4"))));
        }
        disagreements.Should().BeEmpty(string.Join(" | ", disagreements.Take(10)));
    }

    [Theory]
    [InlineData(new[] { 0x63, 0x61, 0x66, 0xE9 }, true)]
    [InlineData(new[] { 0x63, 0x61, 0x66, 0x65, 0x301 }, false)]
    [InlineData(new[] { 0x71, 0x301 }, true)]
    [InlineData(new[] { 0x61, 0x323, 0x301 }, false)]
    [InlineData(new[] { 0x1100, 0x1161 }, false)]
    [InlineData(new[] { 0xAC00 }, true)]
    [InlineData(new[] { 0x212B }, false)]
    [InlineData(new[] { 0x915, 0x93C }, true)]
    [InlineData(new[] { 0x958 }, false)]
    [InlineData(new[] { 0xC5, 0x323 }, false)]
    public void Known_strings(int[] codePoints, bool nfc) =>
        Nfc.Check(string.Concat(codePoints.Select(char.ConvertFromUtf32))).Should().Be(nfc);

    [Fact]
    public void An_unpaired_surrogate_is_not_NFC()
    {
        Nfc.Check(new string([(char)0xD800])).Should().BeFalse();
        Nfc.Check("a" + new string([(char)0xDC00]) + char.ConvertFromUtf32(0x301)).Should().BeFalse();
    }

    [Fact]
    public void A_precomposed_letter_followed_by_a_mark_normalizes_like_the_platform()
    {
        // The case a quick check alone gets wrong: Å then dot below reorders, and Ạ with a ring above is the result.
        Nfc.Normalize([0x00C5, 0x0323]).Should().Equal(0x1EA0, 0x030A);
        "Ạ̊".Normalize(NormalizationForm.FormC).EnumerateRunes().Select(r => r.Value).Should().Equal(0x1EA0, 0x030A);
    }
}
