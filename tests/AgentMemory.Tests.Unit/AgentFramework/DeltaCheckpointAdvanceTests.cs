using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// 30.5. Advancing the checkpoint is an <b>acknowledgement</b>, not a read receipt.
/// </summary>
/// <remarks>
/// <para>
/// This distinction is the entire reason the checkpoint is not derived from the <c>:MemoryReadAudit</c>
/// trail, which advances the moment something is read. If the checkpoint moved on read, a turn that
/// crashed before the model ever saw the delta would still mark that window as seen, and those changes
/// would never be reported again. Replaying a delta costs tokens; losing one loses knowledge.
/// </para>
/// <para>
/// It advances to the delta's own <c>TakenAtUtc</c> rather than to "now", because the interval between
/// reading the delta and finishing the turn was never reported to anyone — and that interval contains a
/// model call, which is long enough for another writer to land inside it.
/// </para>
/// </remarks>
public sealed class DeltaCheckpointAdvanceTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _ids = Substitute.For<IIdGenerator>();

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DeltaTakenAt = Now.AddSeconds(-9);

    private sealed class TestAgentSession : AgentSession;

    // Exists only to satisfy InvokingContext/InvokedContext's non-null agent parameter; the turn itself
    // is driven directly through the provider's public hooks.
    private sealed class StubAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public DeltaCheckpointAdvanceTests()
    {
        _clock.UtcNow.Returns(Now);
        _ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult
            {
                Context = new MemoryContext { SessionId = "s1", AssembledAtUtc = Now },
            });
        _memoryService.RecallChangedSinceAsync(Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new MemoryDelta
            {
                Since = call.Arg<MemoryDeltaRequest>().Since,
                TakenAtUtc = DeltaTakenAt,
                NewFacts =
                [
                    new Fact
                    {
                        FactId = "f1", Subject = "Ada", Predicate = "works_at", Object = "Initech",
                        Confidence = 0.9, CreatedAtUtc = DeltaTakenAt,
                    },
                ],
            });
    }

    private Neo4jMemoryContextProvider Provider(bool enabled = true) =>
        new(
            _memoryService, _embeddings, _clock, _ids,
            Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions { InjectDeltaOnSessionResume = enabled }),
            NullLogger<Neo4jMemoryContextProvider>.Instance);

    private static AgentSession SessionWithCheckpoint(DateTimeOffset? checkpoint)
    {
        var session = new TestAgentSession();
        session.WithMemoryIdentity(userId: "alice", sessionId: "s1", conversationId: "c1");
        if (checkpoint is not null) session.SetDeltaCheckpoint(checkpoint.Value);
        return session;
    }

    private static async Task RunTurnAsync(
        Neo4jMemoryContextProvider provider, AgentSession session, Exception? invokeException = null)
    {
        var agent = new StubAgent();
        var request = new List<ChatMessage> { new(ChatRole.User, "where were we?") };
        var response = new List<ChatMessage> { new(ChatRole.Assistant, "here.") };

        // MAF marks the two hook context types [Experimental]. Driving the provider through its real
        // public entry points is the only way to exercise the provide→store handover this feature lives
        // in, so the diagnostic is suppressed here exactly as the existing provider tests do.
#pragma warning disable MAAI001
        await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, session, new AIContext { Messages = request }),
            CancellationToken.None);

        // MAF models the two outcomes as two constructors rather than a nullable field: a failed
        // invocation has no response messages to hand over at all.
        var invoked = invokeException is null
            ? new AIContextProvider.InvokedContext(agent, session, request, response)
            : new AIContextProvider.InvokedContext(agent, session, request, invokeException);
        await provider.InvokedAsync(invoked, CancellationToken.None);
#pragma warning restore MAAI001
    }

    [Fact]
    public async Task ASuccessfulResumeTurnAdvancesTheCheckpointToTheDeltasOwnInstant()
    {
        var session = SessionWithCheckpoint(Now.AddHours(-2));

        await RunTurnAsync(Provider(), session);

        session.GetDeltaCheckpoint().Should().Be(
            DeltaTakenAt,
            "the window between reading the delta and finishing the turn was never reported");
    }

    [Fact]
    public async Task AFailedTurnLeavesTheCheckpointExactlyWhereItWas()
    {
        // The delta was fetched, but the model never answered. Nothing was acknowledged.
        var original = Now.AddHours(-2);
        var session = SessionWithCheckpoint(original);

        await RunTurnAsync(Provider(), session, invokeException: new InvalidOperationException("model down"));

        session.GetDeltaCheckpoint().Should().Be(original);
    }

    [Fact]
    public async Task AMidSessionTurnStillAdvancesTheCheckpointSoTheNextResumeIsAccurate()
    {
        // No delta this turn (the checkpoint is young), but the agent WAS present. If the checkpoint
        // stood still here, the next resume would re-report everything the agent sat through live.
        var session = SessionWithCheckpoint(Now.AddMinutes(-5));

        await RunTurnAsync(Provider(), session);

        session.GetDeltaCheckpoint().Should().Be(Now);
        await _memoryService.DidNotReceive().RecallChangedSinceAsync(
            Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ABrandNewSessionGetsItsFirstCheckpointStampedAfterTheTurn()
    {
        var session = SessionWithCheckpoint(null);

        await RunTurnAsync(Provider(), session);

        session.GetDeltaCheckpoint().Should().Be(Now);
    }

    [Fact]
    public async Task TheStagedInstantIsNotPromotedTwiceSoTheCheckpointNeverMovesBackwards()
    {
        // The staging slot is cleared by the fact that a promoted value is no longer NEWER than the
        // checkpoint. Without that guard the second turn would re-promote the first turn's instant and
        // the same window would replay forever.
        var session = SessionWithCheckpoint(Now.AddHours(-2));
        var provider = Provider();

        await RunTurnAsync(provider, session);
        session.GetDeltaCheckpoint().Should().Be(DeltaTakenAt);

        await RunTurnAsync(provider, session);

        session.GetDeltaCheckpoint().Should().Be(Now, "the second turn acknowledged up to now");
    }

    [Fact]
    public async Task WithTheFeatureOffNoCheckpointIsEverWritten()
    {
        var session = SessionWithCheckpoint(null);

        await RunTurnAsync(Provider(enabled: false), session);

        session.GetDeltaCheckpoint().Should().BeNull();
    }
}
