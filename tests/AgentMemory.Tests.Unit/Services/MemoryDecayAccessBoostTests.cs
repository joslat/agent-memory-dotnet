using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// BUG-R7: the access term is a lifetime counter added to a time-decayed confidence term, so it
/// neither decays nor saturates gracefully. These lock the two observed failure modes.
/// </summary>
public sealed class MemoryDecayAccessBoostTests
{
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public MemoryDecayAccessBoostTests() => _clock.UtcNow.Returns(_now);

    private MemoryDecayService CreateSut(MemoryDecayOptions? options = null) =>
        new(Substitute.For<IEntityRepository>(),
            Substitute.For<IFactRepository>(),
            Substitute.For<IPreferenceRepository>(),
            _clock,
            Options.Create(options ?? new MemoryDecayOptions()),
            NullLogger<MemoryDecayService>.Instance);

    /// <summary>
    /// A heavily-accessed item must not be able to swamp the decayed-confidence signal outright.
    /// With a linear boost, access_count = 10,000 scores 2,000 — three orders of magnitude above the
    /// [0,1] range the score is supposed to occupy.
    /// </summary>
    [Fact]
    public void ComputeScore_RunawayAccessCount_DoesNotDominateTheConfidenceSignal()
    {
        var sut = CreateSut();

        var score = sut.ComputeScore(
            confidence: 0.9,
            createdAt: _now.AddDays(-3650),
            lastAccessedAt: _now.AddDays(-3650),
            accessCount: 10_000);

        score.Should().BeLessThanOrEqualTo(2.0,
            "the retention score is blended against a [0,1] cosine score, so an unbounded access " +
            "term makes every other signal irrelevant");
    }

    /// <summary>
    /// The prune predicate is score &lt; MinRetentionScore (default 0.1) and the boost is 0.2 per
    /// access, so ONE recall permanently exempts an item from pruning however stale it becomes.
    /// </summary>
    [Fact]
    public void ComputeScore_SingleAccessLongAgo_RemainsPrunable()
    {
        var options = new MemoryDecayOptions();
        var sut = CreateSut(options);

        var score = sut.ComputeScore(
            confidence: 0.5,
            createdAt: _now.AddDays(-3650),
            lastAccessedAt: _now.AddDays(-3650),
            accessCount: 1);

        score.Should().BeLessThan(options.MinRetentionScore,
            "a memory touched once a decade ago must not be permanently unprunable");
    }

    /// <summary>
    /// Damping must stay strictly monotonic: more accesses still mean more retention, just with
    /// diminishing returns rather than a linear ramp.
    /// </summary>
    [Fact]
    public void ComputeScore_AccessBoost_RemainsMonotonicButDamped()
    {
        var sut = CreateSut();
        DateTimeOffset stamp = _now.AddDays(-30);

        double At(int accessCount) =>
            sut.ComputeScore(0.5, stamp, stamp, accessCount);

        At(10).Should().BeGreaterThan(At(1));
        At(100).Should().BeGreaterThan(At(10));
        (At(100) - At(10)).Should().BeLessThan(At(10) - At(1),
            "diminishing returns are the point of damping");
    }
}
