using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class MemoryDecayServiceTests
{
    private readonly IEntityRepository _entityRepo;
    private readonly IFactRepository _factRepo;
    private readonly IPreferenceRepository _prefRepo;
    private readonly IClock _clock;
    private readonly DateTimeOffset _now = new(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public MemoryDecayServiceTests()
    {
        _entityRepo = Substitute.For<IEntityRepository>();
        _factRepo = Substitute.For<IFactRepository>();
        _prefRepo = Substitute.For<IPreferenceRepository>();
        _clock = Substitute.For<IClock>();
        _clock.UtcNow.Returns(_now);
    }

    private MemoryDecayService CreateSut(MemoryDecayOptions? options = null) =>
        new(_entityRepo, _factRepo, _prefRepo, _clock,
            Options.Create(options ?? new MemoryDecayOptions()),
            NullLogger<MemoryDecayService>.Instance);

    // ── ComputeScore formula tests ──────────────────────────────────────

    [Fact]
    public void ComputeScore_FreshMemory_ReturnsHighScore()
    {
        var sut = CreateSut();
        var createdAt = _now;

        var score = sut.ComputeScore(confidence: 1.0, createdAt, lastAccessedAt: null, accessCount: 0);

        // Just created => daysSinceAccess ≈ 0 => exp(0) = 1.0 => score = 1.0
        score.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void ComputeScore_AtHalfLife_ReturnsHalfConfidence()
    {
        var options = new MemoryDecayOptions { DecayHalfLifeDays = 30 };
        var sut = CreateSut(options);

        var createdAt = _now.AddDays(-30); // exactly one half-life ago

        var score = sut.ComputeScore(confidence: 1.0, createdAt, lastAccessedAt: null, accessCount: 0);

        // At half-life: score = 1.0 * 0.5 = 0.5
        score.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void ComputeScore_AccessBoostAddsToScore()
    {
        var options = new MemoryDecayOptions { AccessBoostFactor = 0.2, DecayHalfLifeDays = 30 };
        var sut = CreateSut(options);

        var createdAt = _now.AddDays(-30);

        var scoreWithoutAccess = sut.ComputeScore(1.0, createdAt, null, 0);
        var scoreWithAccess = sut.ComputeScore(1.0, createdAt, null, 5);

        // 5 accesses × 0.2 = 1.0 boost
        (scoreWithAccess - scoreWithoutAccess).Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void ComputeScore_LastAccessedAtResetsDecay()
    {
        var sut = CreateSut();
        var createdAt = _now.AddDays(-100);
        var accessedAt = _now.AddDays(-1);

        var score = sut.ComputeScore(1.0, createdAt, accessedAt, 0);

        // Should use lastAccessedAt (1 day ago) not createdAt (100 days ago)
        score.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void ComputeScore_ZeroConfidence_StillGetsAccessBoost()
    {
        var options = new MemoryDecayOptions { AccessBoostFactor = 0.1 };
        var sut = CreateSut(options);

        var score = sut.ComputeScore(0.0, _now, null, 10);

        // 0 * exp(...) + 0.1 * 10 = 1.0
        score.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void ComputeScore_VeryOldMemory_ScoreApproachesZero()
    {
        var options = new MemoryDecayOptions { DecayHalfLifeDays = 30 };
        var sut = CreateSut(options);

        var createdAt = _now.AddDays(-365);

        var score = sut.ComputeScore(1.0, createdAt, null, 0);

        // After ~12 half-lives: 1.0 * 2^(-12) ≈ 0.000244
        score.Should().BeLessThan(0.01);
    }

    [Fact]
    public void ComputeScore_DoubleHalfLife_ReturnsQuarterConfidence()
    {
        var options = new MemoryDecayOptions { DecayHalfLifeDays = 10 };
        var sut = CreateSut(options);

        var createdAt = _now.AddDays(-20); // 2 half-lives

        var score = sut.ComputeScore(1.0, createdAt, null, 0);

        // 1.0 * exp(-ln2/10 * 20) = 1.0 * (1/4) = 0.25
        score.Should().BeApproximately(0.25, 0.01);
    }

    // ── PruneExpiredMemoriesAsync ───────────────────────────────────────

    [Fact]
    public async Task PruneExpiredMemoriesAsync_ReturnsZeroForCoreImpl()
    {
        var sut = CreateSut();

        var pruned = await sut.PruneExpiredMemoriesAsync("session-1");

        // Core implementation delegates to repo layer; returns 0 as placeholder
        pruned.Should().Be(0);
    }

    // ── UpdateAccessTimestampAsync ──────────────────────────────────────

    [Fact]
    public async Task UpdateAccessTimestampAsync_DoesNotThrow()
    {
        var sut = CreateSut();

        var act = () => sut.UpdateAccessTimestampAsync("node-1", "Entity");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateAccessTimestampAsync_ThrowsForNullNodeId()
    {
        var sut = CreateSut();

        var act = () => sut.UpdateAccessTimestampAsync(null!, "Entity");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpdateAccessTimestampAsync_ThrowsForEmptyLabel()
    {
        var sut = CreateSut();

        var act = () => sut.UpdateAccessTimestampAsync("node-1", "");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── CalculateRetentionScoreAsync ────────────────────────────────────

    [Fact]
    public async Task CalculateRetentionScoreAsync_ThrowsForNullNodeId()
    {
        var sut = CreateSut();

        var act = () => sut.CalculateRetentionScoreAsync(null!, "Entity");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CalculateRetentionScoreAsync_ThrowsForEmptyLabel()
    {
        var sut = CreateSut();

        var act = () => sut.CalculateRetentionScoreAsync("node-1", " ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── MemoryDecayOptions defaults ─────────────────────────────────────

    [Fact]
    public void DefaultOptions_HaveSensibleDefaults()
    {
        var options = new MemoryDecayOptions();

        options.DecayHalfLifeDays.Should().Be(30);
        options.MinRetentionScore.Should().Be(0.1);
        options.MaxMemoriesPerSession.Should().Be(10_000);
        options.AccessBoostFactor.Should().Be(0.2);
        options.EnableAutoPrune.Should().BeFalse();
    }

    [Fact]
    public void DefaultOptions_StaticInstance_MatchesDefault()
    {
        var defaults = MemoryDecayOptions.Default;

        defaults.DecayHalfLifeDays.Should().Be(30);
        defaults.EnableAutoPrune.Should().BeFalse();
    }
}
