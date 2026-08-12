using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Reasoning traces carry a trust level (PLAN 6.3) — stamped from configuration, never from the caller.
/// </summary>
/// <remarks>
/// <para>
/// Traces were the one recall category with no trust signal at all. <c>StartTraceAsync</c> strips any
/// caller-supplied <c>trust_level</c> — correctly, since a caller must never self-assign a level that
/// bypasses the admission policy — and then nothing stamped one, so every trace read back as
/// <see cref="MemoryTrustLevel.Untrusted"/>. "The agent generated this" and "no signal was recorded"
/// were the same value, which is precisely what the enum exists to prevent.
/// </para>
/// <para>
/// Both halves have to hold at once, and they pull in opposite directions: the caller must not be able
/// to set it, and it must not stay unset.
/// </para>
/// </remarks>
public sealed class TraceTrustStampingTests
{
    private readonly IReasoningTraceRepository _traceRepo = Substitute.For<IReasoningTraceRepository>();
    private readonly IReasoningStepRepository _stepRepo = Substitute.For<IReasoningStepRepository>();
    private readonly IToolCallRepository _toolRepo = Substitute.For<IToolCallRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private readonly List<ReasoningTrace> _written = [];

    public TraceTrustStampingTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGen.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
        _traceRepo.AddAsync(Arg.Any<ReasoningTrace>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _written.Add(ci.Arg<ReasoningTrace>());
                return Task.FromResult(ci.Arg<ReasoningTrace>());
            });
    }

    private ReasoningMemoryService CreateSut(MemoryTrustLevel? level = null) =>
        new(_traceRepo, _stepRepo, _toolRepo, _embeddings, _clock, _idGen,
            Options.Create(level is { } value
                ? new ReasoningMemoryOptions { DefaultTraceTrustLevel = value }
                : new ReasoningMemoryOptions()),
            NullLogger<ReasoningMemoryService>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

    [Fact]
    public async Task ATraceIsStampedRatherThanLeftUntrusted()
    {
        await CreateSut().StartTraceAsync("s-1", "deploy the service");

        _written.Should().ContainSingle()
            .Which.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ModelGenerated,
                "a trace is the agent's own record of what it did, and Untrusted is indistinguishable "
                + "from no signal at all");
    }

    [Fact]
    public async Task ACallerStillCannotSetItsOwnTrustLevel()
    {
        // The security half, and it must survive the stamping. A caller that could self-assign
        // ApplicationTrusted would bypass the admission policy's instruction-like-content detection
        // for this trace's own Task text on recall.
        await CreateSut().StartTraceAsync("s-1", "deploy the service",
            metadata: new Dictionary<string, object>
            {
                ["trust_level"] = nameof(MemoryTrustLevel.ApplicationTrusted),
            });

        _written.Should().ContainSingle()
            .Which.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.ModelGenerated,
                "the caller's value is stripped before the configured one is stamped");
    }

    [Fact]
    public async Task TheLevelIsConfigurable()
    {
        // The escape hatch for a host that lowered MinimumTrustForAdmissionBypass to ModelGenerated
        // or below -- the one configuration where this stamp changes behaviour.
        await CreateSut(MemoryTrustLevel.Untrusted).StartTraceAsync("s-1", "deploy the service");

        _written.Should().ContainSingle()
            .Which.Metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.Untrusted);
    }

    [Fact]
    public async Task OtherCallerMetadataSurvives()
    {
        // Only the reserved key is removed. Stripping the whole dictionary would silently discard
        // application data that has nothing to do with trust.
        await CreateSut().StartTraceAsync("s-1", "deploy the service",
            metadata: new Dictionary<string, object> { ["ticket"] = "OPS-42" });

        var stored = _written.Should().ContainSingle().Subject;
        stored.Metadata.Should().ContainKey("ticket");
        stored.Metadata["ticket"].Should().Be("OPS-42");
    }

    [Fact]
    public void TheDefaultDoesNotReachTheAdmissionBypassThreshold()
    {
        // Why stamping by default is safe: the shipped bypass threshold is ApplicationTrusted, which
        // ModelGenerated does not reach. If this ever inverts, a trace would start bypassing the
        // instruction-detection it is currently subject to.
        var shipped = new ReasoningMemoryOptions().DefaultTraceTrustLevel;
        var bypass = new AgentMemory.AgentFramework.ContextFormatOptions().MinimumTrustForAdmissionBypass;

        ((int)shipped).Should().BeLessThan((int)bypass);
    }
}
