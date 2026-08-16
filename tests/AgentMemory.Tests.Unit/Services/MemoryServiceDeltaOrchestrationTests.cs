using AgentMemory.Abstractions.Domain;
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
/// 30.5 step 6. The orchestration invariants that make "exactly once" true rather than merely intended.
/// </summary>
/// <remarks>
/// <para>
/// The Cypher is verified against a live graph; what cannot be verified there is the <b>shape of the
/// call</b>. Reading the clock once and handing the same <c>until</c> to all three repositories is what
/// closes the gap between them — read it per repository and a write landing mid-read falls between two
/// windows, unrecoverably, and no query-level test would ever show it.
/// </para>
/// </remarks>
public sealed class MemoryServiceDeltaOrchestrationTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _preferences = Substitute.For<IPreferenceRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IMemoryIsolationPolicy _isolation = Substitute.For<IMemoryIsolationPolicy>();

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    public MemoryServiceDeltaOrchestrationTests()
    {
        _facts.ListChangedInWindowAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FactDeltaRows());
        _preferences.ListChangedInWindowAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PreferenceDeltaRows());
        _entities.ListCreatedInWindowAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _isolation.ResolveReadScope(
                Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<MemoryOperationAccess>())
            .Returns(MemoryScope.Global);
    }

    private MemoryService CreateSut()
    {
        // A DRIFTING clock, deliberately. A constant one cannot distinguish "read once" from "read
        // three times" -- every read would agree and the invariant these tests exist for would be
        // untestable while looking tested. Each successive read moves a second, so a second read
        // anywhere shows up as a window nobody agrees on.
        _clock.UtcNow.Returns(Now, Now.AddSeconds(1), Now.AddSeconds(2), Now.AddSeconds(3));
        return new MemoryService(
            Substitute.For<IShortTermMemoryService>(),
            Substitute.For<IMemoryContextAssembler>(),
            Substitute.For<IMemoryExtractionPipeline>(),
            _entities, _facts, _preferences,
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new MemoryOptions()),
            _clock,
            Substitute.For<IIdGenerator>(),
            NullLogger<MemoryService>.Instance,
            isolationPolicy: _isolation);
    }

    private static MemoryDeltaRequest Request(DateTimeOffset? since = null, int cap = 20) => new()
    {
        Since = since ?? Now.AddHours(-1),
        UserId = "alice",
        MaxItemsPerSection = cap,
    };

    [Fact]
    public async Task TheClockIsReadExactlyOnce()
    {
        // THE invariant. Three repositories, one `until`.
        var sut = CreateSut();

        await sut.RecallChangedSinceAsync(Request());

        _ = _clock.Received(1).UtcNow;
    }

    [Fact]
    public async Task AllThreeRepositoriesGetTheIdenticalWindow()
    {
        var since = Now.AddHours(-3);
        var sut = CreateSut();

        await sut.RecallChangedSinceAsync(Request(since));

        await _facts.Received(1).ListChangedInWindowAsync(
            since, Now, Arg.Any<MemoryScope?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _preferences.Received(1).ListChangedInWindowAsync(
            since, Now, Arg.Any<MemoryScope?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _entities.Received(1).ListCreatedInWindowAsync(
            since, Now, Arg.Any<MemoryScope?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheUpperBoundIsHandedBackAsTheNextCheckpoint()
    {
        var delta = await CreateSut().RecallChangedSinceAsync(Request());

        delta.TakenAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task AFutureCheckpointThrowsRatherThanReturningAnEmptyDelta()
    {
        // "Nothing changed" for a nonsensical window is a confident, actionable, wrong answer.
        var sut = CreateSut();

        var act = async () => await sut.RecallChangedSinceAsync(Request(Now.AddHours(1)));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ACheckpointExactlyAtNowThrowsToo()
    {
        // The window is (since, until]. since == until is empty by definition, not "nothing changed" --
        // and a caller passing it has a bug worth surfacing.
        var sut = CreateSut();

        var act = async () => await sut.RecallChangedSinceAsync(Request(Now));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task TheOwnerScopeIsResolvedThroughTheIsolationPolicyAndNotPassedThrough()
    {
        // A delta reads the repositories DIRECTLY; the assembler, which resolves scope for every other
        // read, is not in this path. Passing a caller's scope through would hand a caller who supplied
        // only a UserId an unfiltered, cross-owner answer.
        var scoped = MemoryScope.For("alice");
        _isolation.ResolveReadScope(
                Arg.Any<MemoryScope?>(), "alice", Arg.Any<string>(), MemoryOperationAccess.Tenant)
            .Returns(scoped);
        var sut = CreateSut();

        await sut.RecallChangedSinceAsync(Request());

        _isolation.Received(1).ResolveReadScope(
            Arg.Any<MemoryScope?>(), "alice", Arg.Any<string>(), MemoryOperationAccess.Tenant);
        await _facts.Received(1).ListChangedInWindowAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), scoped,
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ABucketThatHitsItsCapIsReportedAsTruncated()
    {
        // Capping is necessary; capping QUIETLY would let a caller believe they had seen everything.
        _facts.ListChangedInWindowAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FactDeltaRows { NewFacts = [Fact("a"), Fact("b")] });

        var delta = await CreateSut().RecallChangedSinceAsync(Request(cap: 2));

        delta.TruncatedSections.Should().Contain(nameof(MemoryDelta.NewFacts));
    }

    [Fact]
    public async Task ABucketBelowItsCapIsNotReportedAsTruncated()
    {
        _facts.ListChangedInWindowAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FactDeltaRows { NewFacts = [Fact("a")] });

        var delta = await CreateSut().RecallChangedSinceAsync(Request(cap: 2));

        delta.TruncatedSections.Should().BeEmpty();
    }

    [Fact]
    public async Task TheCapIsPassedToEveryRepository()
    {
        await CreateSut().RecallChangedSinceAsync(Request(cap: 7));

        await _facts.Received(1).ListChangedInWindowAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(),
            7, Arg.Any<CancellationToken>());
        await _preferences.Received(1).ListChangedInWindowAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(),
            7, Arg.Any<CancellationToken>());
        await _entities.Received(1).ListCreatedInWindowAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(),
            7, Arg.Any<CancellationToken>());
    }

    private static Fact Fact(string @object) => new()
    {
        FactId = @object, Subject = "Ada", Predicate = "works_at", Object = @object,
        Confidence = 0.9, CreatedAtUtc = Now,
    };
}
