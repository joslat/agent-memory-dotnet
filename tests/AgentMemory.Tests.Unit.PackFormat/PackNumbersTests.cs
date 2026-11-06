using System.Text.Json;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Feature 02: a pack document's numbers are integers within ±(2⁵³−1), each unit-bearing field in the unit and range the
/// registry gives it, and the walk multiplies millionths with one rounding rule, down.
/// </summary>
public sealed class PackNumbersTests
{
    private static long? Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return PackNumbers.Integer(document.RootElement);
    }

    [Fact]
    public void An_integer_is_written_as_one_within_the_safe_range()
    {
        Read("800000").Should().Be(800_000);
        Read("-50000").Should().Be(-50_000);
        Read("9007199254740991").Should().Be(PackNumbers.MaxSafe);
        Read("9007199254740992").Should().BeNull("2^53 is past what every JSON reader holds exactly");
        Read("0.8").Should().BeNull("a decimal is not an integer");
        Read("800000.5").Should().BeNull();
        // Written as an integer, as both verifiers read every integer: a JSON integer token, Python's int.
        Read("800000.0").Should().BeNull("an integer written with a fraction is not written as one");
        Read("8e5").Should().BeNull("nor with an exponent");
        Read("\"800000\"").Should().BeNull();
    }

    [Fact]
    public void Each_unit_field_is_checked_by_the_registry_s_range()
    {
        PackNumbers.Problem("weightMicro", 1).Should().BeNull();
        PackNumbers.Problem("weightMicro", 0).Should().Contain("1 to 1000000");
        PackNumbers.Problem("liftMicro", -1_000_000).Should().BeNull();
        PackNumbers.Problem("toleranceMicro", 500_001).Should().Contain("0 to 500000");
        PackNumbers.Problem("confidencePermille", 950).Should().BeNull();
        PackNumbers.Problem("confidencePermille", 951).Should().Be("expected 900, 950, 975 or 990");
    }

    [Fact]
    public void A_product_of_millionths_is_rounded_down()
    {
        PackNumbers.MulFloor(900_000, 850_000).Should().Be(765_000);
        // 999,999 x 500,001 / 10^6 = 500,000.499999: down, never to the nearest.
        PackNumbers.MulFloor(999_999, 500_001).Should().Be(500_000);
        PackNumbers.MulFloor(1_000_000, 1_000_000).Should().Be(1_000_000);
        PackNumbers.MulFloor(1, 999_999).Should().Be(0);
    }

    [Fact]
    public void A_decimal_a_person_writes_is_read_exactly()
    {
        PackNumbers.ParseMicro("0.35").Should().Be(350_000);
        PackNumbers.ParseMicro("-0.05").Should().Be(-50_000);
        PackNumbers.ParseMicro("0.1234565").Should().BeNull("seven decimals are below a millionth");
        PackNumbers.ParseMicro("1e-3").Should().BeNull();
        PackNumbers.ShowMicro(350_000).Should().Be("0.35");
        PackNumbers.ShowMicro(-50_000).Should().Be("-0.05");
    }
}
