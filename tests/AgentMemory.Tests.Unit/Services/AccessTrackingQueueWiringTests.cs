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
/// 30.12. The option reaches the queue, and the queue reaches the same nodes the inline path would.
/// </summary>
/// <remarks>
/// <para>
/// Two ways this could ship broken and look fine. The flag could bind and never be read — the defect
/// this project has now found fifteen times, and here it would present as "the latency did not improve".
/// Or the queued path could collect a different set of nodes than the inline path, which would make the
/// decay curve depend on which flag a host set — a difference nothing would surface until retention
/// behaved differently between two deployments of the same version.
/// </para>
/// </remarks>
public sealed class AccessTrackingQueueWiringTests
{
    private readonly IMemoryContextAssembler _assembler = Substitute.For<IMemoryContextAssembler>();
    private readonly IMemoryDecayService _decay = Substitute.For<IMemoryDecayService>();
    private readonly IMemoryAccessTracker _tracker = Substitute.For<IMemoryAccessTracker>();

    private static readonly DateTimeOffset Stamp = DateTimeOffset.UnixEpoch;

    private static MemoryContext ContextWithThreeItems() => new()
    {
        SessionId = "s1",
        AssembledAtUtc = Stamp,
        RelevantEntities = new MemoryContextSection<Entity>
        {
            Items = [new Entity { EntityId = "e1", Name = "Acme", Type = "ORG", Confidence = 0.9, CreatedAtUtc = Stamp }],
        },
        RelevantFacts = new MemoryContextSection<Fact>
        {
            Items = [new Fact { FactId = "f1", Subject = "u", Predicate = "p", Object = "o", Confidence = 0.9, CreatedAtUtc = Stamp }],
        },
        RelevantPreferences = new MemoryContextSection<Preference>
        {
            Items = [new Preference { PreferenceId = "p1", Category = "c", PreferenceText = "t", Confidence = 0.9, CreatedAtUtc = Stamp }],
        },
    };

    private MemoryService Sut(Action<MemoryOptions>? configure = null)
    {
        var options = new MemoryOptions();
        configure?.Invoke(options);
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(ContextWithThreeItems());

        return new MemoryService(
            Substitute.For<IShortTermMemoryService>(),
            _assembler,
            Substitute.For<IMemoryExtractionPipeline>(),
            Substitute.For<IEntityRepository>(),
            Substitute.For<IFactRepository>(),
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Microsoft.Extensions.Options.Options.Create(options),
            Substitute.For<IClock>(),
            Substitute.For<IIdGenerator>(),
            NullLogger<MemoryService>.Instance,
            decayService: _decay,
            accessTracker: _tracker);
    }

    private static RecallRequest Request() => new() { SessionId = "s1", Query = "q" };

    [Fact]
    public async Task WithTheQueueOffTheDecayServiceIsWrittenInline()
    {
        // The historical path, unchanged: awaited before the caller gets its context.
        await Sut().RecallAsync(Request());

        await _decay.Received(1).UpdateAccessTimestampsAsync(
            Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>());
        _tracker.DidNotReceive().Track(Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>());
    }

    [Fact]
    public async Task WithTheQueueOnTheRecallPathOnlyEnqueues()
    {
        await Sut(o => o.UseAccessTrackingQueue = true).RecallAsync(Request());

        _tracker.Received(1).Track(Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>());
        await _decay.DidNotReceive().UpdateAccessTimestampsAsync(
            Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheQueuedPathCollectsExactlyTheNodesTheInlinePathWould()
    {
        // Same three sections, same ids. Two copies of "which nodes did this recall touch" is how one
        // path quietly stops counting a section the other still counts, and the symptom would be a
        // decay curve that differs by which flag a host set.
        await Sut(o => o.UseAccessTrackingQueue = true).RecallAsync(Request());

        _tracker.Received(1).Track(Arg.Is<IReadOnlyList<(string NodeId, MemoryNodeKind Kind)>>(nodes =>
            nodes.Count == 3
            && nodes.Any(n => n.NodeId == "e1" && n.Kind == MemoryNodeKind.Entity)
            && nodes.Any(n => n.NodeId == "f1" && n.Kind == MemoryNodeKind.Fact)
            && nodes.Any(n => n.NodeId == "p1" && n.Kind == MemoryNodeKind.Preference)));
    }

    [Fact]
    public async Task TheQueueWinsOverTheOlderDeferralWhenBothAreSet()
    {
        // Deferral starts the write inside the request scope, which its own documentation calls unsafe
        // for a request-scoped host. A host that adopted the safe option should get the safe path, not
        // two writers racing over the same stamps.
        await Sut(o =>
        {
            o.UseAccessTrackingQueue = true;
            o.DeferAccessTracking = true;
        }).RecallAsync(Request());

        _tracker.Received(1).Track(Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>());
    }

    [Fact]
    public async Task WithNoTrackerAvailableTheFlagFallsBackRatherThanLosingTheWrite()
    {
        // A container built before 30.12 supplies no tracker. Silently skipping the write would make
        // the decay curve depend on which version assembled the container.
        var options = new MemoryOptions { UseAccessTrackingQueue = true };
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(ContextWithThreeItems());

        var sut = new MemoryService(
            Substitute.For<IShortTermMemoryService>(),
            _assembler,
            Substitute.For<IMemoryExtractionPipeline>(),
            Substitute.For<IEntityRepository>(),
            Substitute.For<IFactRepository>(),
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Microsoft.Extensions.Options.Options.Create(options),
            Substitute.For<IClock>(),
            Substitute.For<IIdGenerator>(),
            NullLogger<MemoryService>.Instance,
            decayService: _decay);

        await sut.RecallAsync(Request());

        await _decay.Received(1).UpdateAccessTimestampsAsync(
            Arg.Any<IReadOnlyList<(string, MemoryNodeKind)>>(), Arg.Any<CancellationToken>());
    }
}
