using System.Text;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Feature 02: the format counts and orders text by character, a Unicode code point, so every language reads a length and an
/// order the same. .NET strings are UTF-16, where an emoji is two units and ordinal order differs from code-point order.
/// </summary>
public sealed class PackTextTests
{
    [Fact]
    public void A_length_counts_characters_not_UTF16_units()
    {
        // Facts, not theory data: a lone surrogate does not survive the test runner's serialization of theory data.
        PackText.Length("").Should().Be(0);
        PackText.Length("abc").Should().Be(3);
        PackText.Length("\U0001F600").Should().Be(1);
        PackText.Length("a\U0001F600b").Should().Be(3);
        PackText.Length("\uD800").Should().Be(1, "a lone surrogate is one");
        PackText.Length("\uDC00\uD800").Should().Be(2, "a low surrogate before a high one is no pair");
    }

    [Fact]
    public void The_order_is_code_point_order_where_UTF16_order_differs()
    {
        // U+FF61 is below U+10000 as a code point; as UTF-16, U+10000 begins with the surrogate D800, below FF61.
        string.CompareOrdinal("\uFF61", "\U00010000").Should().BePositive("this is the case .NET's ordinal order gets the other way");
        PackText.Compare("\uFF61", "\U00010000").Should().BeNegative();
        PackText.Compare("\U00010000", "\uFF61").Should().BePositive();
        PackText.Compare("a", "ab").Should().BeNegative();
        PackText.Compare("\U0001F600", "\U0001F600").Should().Be(0);
    }

    [Fact]
    public void The_order_is_UTF8_byte_order_on_every_generated_pair()
    {
        // Code-point order equals UTF-8 byte order: what a reader in any language gets by comparing the bytes it read.
        var random = new Random(20261031);
        int[] alphabet = ['a', 'z', 0x7F, 0xE9, 0x7FF, 0x800, 0xD7FF, 0xE000, 0xFF61, 0xFFFF, 0x10000, 0x1F600, 0x10FFFF];
        string Text() => string.Concat(Enumerable.Range(0, random.Next(0, 5)).Select(_ => char.ConvertFromUtf32(alphabet[random.Next(alphabet.Length)])));
        for (var n = 0; n < 20_000; n++)
        {
            var (a, b) = (Text(), Text());
            var bytes = Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
            Math.Sign(PackText.Compare(a, b)).Should().Be(Math.Sign(bytes), $"'{a}' against '{b}'");
        }
    }

    [Fact]
    public void Code_points_are_read_whole_and_a_lone_surrogate_as_itself() =>
        PackText.CodePoints("a\U0001F600\uD800b").Should().Equal('a', 0x1F600, 0xD800, 'b');
}
