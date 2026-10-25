using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// EXT-write (design 0.12, W3): module closings are approved by the core writer's own closing policy, so the same judges,
/// fallback and thresholds decide them; nothing is approved without a valid answer at the threshold.
/// </summary>
public sealed class MemoryClosingApproverTests
{
    private sealed class Judge(double score, bool enabled = true, bool fail = false) : IMemoryUpdateJudgeFallback
    {
        public int Calls { get; private set; }
        public bool IsEnabled => enabled;

        public Task<IReadOnlyDictionary<string, double>> JudgeAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (fail) throw new HttpRequestException("down");
            return Task.FromResult<IReadOnlyDictionary<string, double>>(request.Pairs.ToDictionary(p => p.Key, _ => score));
        }
    }

    private static MemoryUpdateRequest Named(params string[] keys) =>
        new("Use the retry helper", DateTimeOffset.UnixEpoch, [.. keys.Select(k => new MemoryUpdatePair(k, "new", "old"))]) { Named = true };

    private static MemoryClosingApprover Approver(IMemoryUpdateJudge? judge = null, IMemoryUpdateJudgeFallback? fallback = null) =>
        new(NullLogger<MemoryClosingApprover>.Instance, Options.Create(new ExtractionOptions()), judge, fallback);

    [Theory]
    [InlineData(0.60, true)]
    [InlineData(0.59, false)]
    [InlineData(double.NaN, false)]
    public async Task ANamedClosingIsApprovedAtTheCoreWritersThreshold(double score, bool approved)
    {
        var verdicts = await Approver(new Judge(score)).ApproveAsync(Named("a"));
        verdicts["a"].Approved.Should().Be(approved);
    }

    [Fact]
    public async Task TheHostChatModelDecidesWhenTheJudgeFailsAsInTheCoreWriter()
    {
        var fallback = new Judge(0.9);
        var verdicts = await Approver(new Judge(0.9, fail: true), fallback).ApproveAsync(Named("a", "b"));
        fallback.Calls.Should().Be(1);
        verdicts.Values.Should().OnlyContain(v => v.Approved && v.Probability == 0.9);
    }

    [Fact]
    public async Task WithoutAJudgeNothingIsApprovedAndNoOneIsAsked()
    {
        var off = new Judge(0.99, enabled: false);
        var approver = Approver(off, off);
        approver.CanApprove(named: true).Should().BeFalse();
        var verdicts = await approver.ApproveAsync(Named("a"));
        verdicts["a"].Should().Be(new MemoryClosingVerdict(null, false));
        off.Calls.Should().Be(0);
    }

    [Fact]
    public void CoreRegistersTheApproverOverItsPolicy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(_ => { });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMemoryClosingApprover>().Should().BeOfType<MemoryClosingApprover>();
    }
}
