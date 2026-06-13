using FluentAssertions;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// Unit coverage for the D1/D2 ranking knobs and the <see cref="MemoryProfile"/> "start at parity,
/// dial up" switch: parity is off by default, profiles supply enabled defaults, and an explicitly-set
/// knob always overrides the profile. Clamping keeps the effective values in range.
/// </summary>
public sealed class MemoryRankingOptionsTests
{
    [Fact]
    public void Default_IsParity_AndSemanticOnly()
    {
        var o = new MemoryRankingOptions();

        o.Profile.Should().Be(MemoryProfile.Parity);
        o.EffectiveRecencyWeight.Should().Be(0.0);
        o.EffectiveStructuralDecayGamma.Should().Be(1.0);
        o.RecencyRerankEnabled.Should().BeFalse();
        o.StructuralDecayEnabled.Should().BeFalse();
    }

    [Fact]
    public void MemoryOptions_Ranking_DefaultsToParity()
    {
        new MemoryOptions().Ranking.Should().NotBeNull();
        new MemoryOptions().Ranking.Profile.Should().Be(MemoryProfile.Parity);
    }

    [Theory]
    [InlineData(MemoryProfile.Enhanced)]
    [InlineData(MemoryProfile.Bitemporal)]
    public void EnhancedAndBitemporal_EnableBothSignals(MemoryProfile profile)
    {
        var o = new MemoryRankingOptions { Profile = profile };

        o.EffectiveRecencyWeight.Should().BeGreaterThan(0.0);
        o.EffectiveStructuralDecayGamma.Should().BeLessThan(1.0);
        o.RecencyRerankEnabled.Should().BeTrue();
        o.StructuralDecayEnabled.Should().BeTrue();
    }

    [Fact]
    public void ExplicitRecencyWeight_OverridesProfile()
    {
        // Even at Parity (profile default 0), an explicit weight wins.
        var on = new MemoryRankingOptions { Profile = MemoryProfile.Parity, RecencyWeight = 0.5 };
        on.EffectiveRecencyWeight.Should().Be(0.5);
        on.RecencyRerankEnabled.Should().BeTrue();

        // And it can turn Enhanced's recency back off.
        var off = new MemoryRankingOptions { Profile = MemoryProfile.Enhanced, RecencyWeight = 0.0 };
        off.EffectiveRecencyWeight.Should().Be(0.0);
        off.RecencyRerankEnabled.Should().BeFalse();
    }

    [Fact]
    public void ExplicitGamma_OverridesProfile()
    {
        var on = new MemoryRankingOptions { Profile = MemoryProfile.Parity, StructuralDecayGamma = 0.5 };
        on.EffectiveStructuralDecayGamma.Should().Be(0.5);
        on.StructuralDecayEnabled.Should().BeTrue();

        var off = new MemoryRankingOptions { Profile = MemoryProfile.Enhanced, StructuralDecayGamma = 1.0 };
        off.EffectiveStructuralDecayGamma.Should().Be(1.0);
        off.StructuralDecayEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(0.3, 0.3)]
    public void RecencyWeight_IsClampedTo01(double input, double expected)
    {
        new MemoryRankingOptions { RecencyWeight = input }
            .EffectiveRecencyWeight.Should().Be(expected);
    }

    [Theory]
    [InlineData(0.0, 1.0)]   // 0 is meaningless for γ^hops ⇒ off
    [InlineData(-1.0, 1.0)]  // negative ⇒ off
    [InlineData(2.0, 1.0)]   // > 1 ⇒ off
    [InlineData(0.7, 0.7)]
    public void Gamma_IsClampedToValidRange(double input, double expected)
    {
        new MemoryRankingOptions { StructuralDecayGamma = input }
            .EffectiveStructuralDecayGamma.Should().Be(expected);
    }

    // ── D3: query-intent presets (ForIntent) ──────────────────────────

    [Fact]
    public void ForIntent_Default_LeavesWeightsUnchanged()
    {
        var configured = new MemoryRankingOptions { RecencyWeight = 0.25, StructuralDecayGamma = 0.7 };

        var result = configured.ForIntent(RankingIntent.Default);

        result.EffectiveRecencyWeight.Should().Be(0.25);
        result.EffectiveStructuralDecayGamma.Should().Be(0.7);
    }

    [Fact]
    public void ForIntent_Latest_RaisesRecency_ButNeverLowersIt()
    {
        // From parity (recency 0) Latest raises it; an already-higher configured weight is kept.
        new MemoryRankingOptions().ForIntent(RankingIntent.Latest)
            .EffectiveRecencyWeight.Should().BeGreaterThanOrEqualTo(0.6);
        new MemoryRankingOptions { RecencyWeight = 0.9 }.ForIntent(RankingIntent.Latest)
            .EffectiveRecencyWeight.Should().Be(0.9, "Latest must not lower an already-recency-heavy weight");
    }

    [Fact]
    public void ForIntent_Analog_ZeroesRecency_ButKeepsStructuralDecay()
    {
        // Analog favours structurally/semantically similar memories regardless of age (precedent retrieval).
        var result = new MemoryRankingOptions { RecencyWeight = 0.9, StructuralDecayGamma = 0.7 }
            .ForIntent(RankingIntent.Analog);

        result.EffectiveRecencyWeight.Should().Be(0.0, "Analog must zero recency so old-but-relevant precedents surface");
        result.RecencyRerankEnabled.Should().BeFalse();
        result.EffectiveStructuralDecayGamma.Should().Be(0.7, "structural decay is left untouched");
    }

    [Fact]
    public void RecallOptions_Intent_DefaultsToDefault()
    {
        new RecallOptions().Intent.Should().Be(RankingIntent.Default);
    }
}
