using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Domain;

/// <summary>
/// 30.6 step 3. Derivation provenance round-trips, including through Neo4j's JSON shape.
/// </summary>
/// <remarks>
/// <para>
/// The post-persistence shape is the half that breaks. <c>Metadata</c> is stored as one serialized JSON
/// string, so everything read back is a <see cref="JsonElement"/> and not the CLR type that was written
/// — a reader that handles only the write-side shape works perfectly in every unit test and returns
/// nothing at all in production.
/// </para>
/// <para>
/// Absent keys read as null or empty, never throw: provenance that cannot be parsed is provenance the
/// renderer omits, not a reason to fail a recall.
/// </para>
/// </remarks>
public sealed class MemoryDerivationMetadataExtensionsTests
{
    private static IReadOnlyDictionary<string, object> RoundTripThroughJson(
        IReadOnlyDictionary<string, object> metadata)
    {
        // Exactly what Neo4jRecordMapper does: serialize on write, parse on read.
        var json = JsonSerializer.Serialize(metadata);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, object> Sample() =>
        MemoryDerivationMetadataExtensions.CreateWithDerivation(
            DerivationOperators.Delta, "800 (a1) - 50 (b2)", ["a1", "b2"]);

    // ── write side ────────────────────────────────────────────────────

    [Fact]
    public void ADerivationRoundTripsInMemory()
    {
        var metadata = Sample();

        metadata.IsDerived().Should().BeTrue();
        metadata.GetDerivation().Should().Be("800 (a1) - 50 (b2)");
        metadata.GetDerivationOperator().Should().Be(DerivationOperators.Delta);
        metadata.GetInputFactIds().Should().Equal("a1", "b2");
    }

    [Fact]
    public void WithDerivationPreservesEveryOtherEntry()
    {
        var existing = new Dictionary<string, object> { ["source"] = "extraction" };

        var metadata = existing.WithDerivation(DerivationOperators.Count, "3 facts", ["a", "b", "c"]);

        metadata["source"].Should().Be("extraction");
        metadata.IsDerived().Should().BeTrue();
    }

    [Fact]
    public void TrustAndDerivationCoexistOnOneRecord()
    {
        // A derived fact still needs a trust level -- it is rendered through the same admission
        // machinery as every other recalled item and earns no bypass for being computed.
        var metadata = Sample().WithTrustLevel(MemoryTrustLevel.ApplicationTrusted);

        metadata.IsDerived().Should().BeTrue();
        metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ApplicationTrusted);
    }

    // ── read side, after persistence ──────────────────────────────────

    [Fact]
    public void ADerivationSurvivesTheJsonRoundTripNeo4jPerforms()
    {
        // THE case that separates a working reader from one that only works in unit tests.
        var metadata = RoundTripThroughJson(Sample());

        metadata.IsDerived().Should().BeTrue();
        metadata.GetDerivation().Should().Be("800 (a1) - 50 (b2)");
        metadata.GetDerivationOperator().Should().Be(DerivationOperators.Delta);
        metadata.GetInputFactIds().Should().Equal("a1", "b2");
    }

    [Fact]
    public void ASingleInputIdStoredAsABareStringStillReads()
    {
        var metadata = RoundTripThroughJson(
            new Dictionary<string, object> { ["kind"] = "derived", ["input_fact_ids"] = "a1" });

        metadata.GetInputFactIds().Should().Equal("a1");
    }

    // ── absence ───────────────────────────────────────────────────────

    [Fact]
    public void AnOrdinaryFactIsNotDerived()
    {
        var metadata = new Dictionary<string, object>();

        metadata.IsDerived().Should().BeFalse();
        metadata.GetDerivation().Should().BeNull();
        metadata.GetDerivationOperator().Should().BeNull();
        metadata.GetInputFactIds().Should().BeEmpty();
    }

    [Fact]
    public void AnUnrecognisedOperatorReadsAsNullRatherThanThrowing()
    {
        var metadata = new Dictionary<string, object> { ["operator"] = "Telepathy" };

        metadata.GetDerivationOperator().Should().BeNull();
    }

    [Fact]
    public void AValueOfTheWrongTypeReadsAsAbsent()
    {
        var metadata = new Dictionary<string, object> { ["derivation"] = 42 };

        metadata.GetDerivation().Should().BeNull();
    }

    // ── the reserved-key rule ─────────────────────────────────────────

    [Fact]
    public void CallerSuppliedDerivationKeysAreStripped()
    {
        // A caller who could stamp kind=derived plus an invented derivation string would hand the model
        // arithmetic no accountant performed -- carrying the inline provenance that makes it look
        // checked. Same reserved-key discipline as trust_level, same reason.
        var hostile = new Dictionary<string, object>
        {
            ["kind"] = "derived",
            ["derivation"] = "17 = 12 + 5, definitely",
            ["operator"] = "Sum",
            ["input_fact_ids"] = new[] { "made-up" },
            ["colour"] = "blue",
        };

        var sanitized = hostile.WithoutCallerSuppliedDerivation();

        sanitized.IsDerived().Should().BeFalse();
        sanitized.GetDerivation().Should().BeNull();
        sanitized.GetInputFactIds().Should().BeEmpty();
        sanitized["colour"].Should().Be("blue", "only the reserved keys are removed");
    }

    [Fact]
    public void MetadataWithNoReservedKeysIsReturnedUntouched()
    {
        var metadata = new Dictionary<string, object> { ["colour"] = "blue" };

        metadata.WithoutCallerSuppliedDerivation().Should().BeSameAs(metadata);
    }

    // ── number formatting ─────────────────────────────────────────────

    [Theory]
    [InlineData("750", "750")]
    [InlineData("-750.5", "-750.5")]
    [InlineData("0.001", "0.001")]
    public void NumbersFormatCultureInvariantly(string input, string expected)
    {
        // A derivation rendered with a comma separator beside a value rendered with a point is a
        // provenance line that appears to disagree with its own result.
        var value = decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture);

        MemoryDerivationMetadataExtensions.FormatDerivedNumber(value).Should().Be(expected);
    }

    [Fact]
    public void ADecimalDoesNotRenderWithTrailingZeroes()
    {
        // decimal preserves scale, so 750.00m would otherwise render as "750.00" while the same value
        // parsed from "750" renders as "750" -- two spellings of one number inside one derivation.
        MemoryDerivationMetadataExtensions.FormatDerivedNumber(750.00m).Should().Be("750");
    }
}
