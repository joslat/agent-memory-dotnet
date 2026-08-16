using FluentAssertions;
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
/// 30.5 step 8. The option-to-call wiring: this is the sixteenth shipped-but-unreachable feature not
/// happening.
/// </summary>
/// <remarks>
/// <para>
/// A repository method nobody calls and an option nobody reads are the same bug wearing different
/// clothes, and this project has now found fifteen instances of it. The off-state test below is not
/// ceremony either: <b>off must be byte-identical</b>, which for an adapter means the provider does not
/// query, does not touch the state bag, and hands back the same <c>Messages</c> reference it always did.
/// </para>
/// </remarks>
public sealed class DeltaRecallProviderWiringTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _ids = Substitute.For<IIdGenerator>();

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    public DeltaRecallProviderWiringTests()
    {
        _clock.UtcNow.Returns(Now);
        _ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult
            {
                Context = new MemoryContext { SessionId = "s1", AssembledAtUtc = Now },
            });
    }

    private Neo4jMemoryContextProvider Provider(Action<AgentFrameworkOptions>? configure = null)
    {
        var options = new AgentFrameworkOptions();
        configure?.Invoke(options);
        return new Neo4jMemoryContextProvider(
            _memoryService, _embeddings, _clock, _ids,
            Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()),
            Options.Create(options),
            NullLogger<Neo4jMemoryContextProvider>.Instance);
    }

    private static List<ChatMessage> Turn() => [new(ChatRole.User, "where were we?")];

    private void ReturnsDelta(params Fact[] newFacts) =>
        _memoryService.RecallChangedSinceAsync(Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new MemoryDelta
            {
                Since = call.Arg<MemoryDeltaRequest>().Since,
                TakenAtUtc = Now,
                NewFacts = newFacts,
            });

    private static Fact Fact(string @object) => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "Ada", Predicate = "works_at", Object = @object,
        Confidence = 0.9, CreatedAtUtc = Now,
    };

    // ── (i) off is off ────────────────────────────────────────────────

    [Fact]
    public async Task WithTheFlagOffNoDeltaIsEverFetchedEvenWithAStaleCheckpointPresent()
    {
        ReturnsDelta(Fact("Initech"));  // armed, so only the flag can be what stops it
        var sut = Provider();  // default: InjectDeltaOnSessionResume = false

        var context = await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddDays(-1));

        await _memoryService.DidNotReceive().RecallChangedSinceAsync(
            Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>());
        context.Messages.Should().NotContain(m => m.Text.Contains("What changed since we last spoke"));
    }

    [Fact]
    public async Task WithTheFlagOffTheEmittedMessagesAreByteIdenticalToTheNoCheckpointControl()
    {
        // Discipline #1, at the adapter. Not "equivalent" -- identical text, in identical order. A new
        // feature that shifts one byte of an existing prompt has changed every host's behaviour whether
        // or not anyone enabled it.
        ReturnsDelta(Fact("Initech"));
        var sut = Provider();

        var control = await sut.BuildContextAsync(Turn(), "s1", "c1", CancellationToken.None, "alice");
        var withCheckpoint = await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddDays(-1));

        Render(withCheckpoint).Should().Be(Render(control));
    }

    private static string Render(Microsoft.Agents.AI.AIContext context) =>
        context.Messages is null
            ? "<null>"
            : string.Join("␞", context.Messages.Select(m => $"{m.Role}␟{m.Text}"));

    [Fact]
    public async Task TheDefaultIsOff()
    {
        // Stated as its own assertion: an upgrade must not start injecting a new block into every
        // resuming agent's prompt.
        new AgentFrameworkOptions().InjectDeltaOnSessionResume.Should().BeFalse();
    }

    // ── (ii) on + stale checkpoint ⇒ exactly one call, block prepended ──

    [Fact]
    public async Task WithTheFlagOnAndAStaleCheckpointTheDeltaIsFetchedOnceAndPrepended()
    {
        ReturnsDelta(Fact("Initech"));
        var sut = Provider(o => o.InjectDeltaOnSessionResume = true);

        var context = await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddHours(-2));

        await _memoryService.Received(1).RecallChangedSinceAsync(
            Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>());
        context.Messages.Should().NotBeNull();
        var first = context.Messages!.First();
        first.Text.Should().Contain("What changed since we last spoke");
        first.Text.Should().Contain("Ada works_at Initech");
    }

    [Fact]
    public async Task TheDeltaReadCarriesTheInvocationOwnerAndTheConfiguredCap()
    {
        // Owner isolation is resolved from THIS invocation's user, exactly as recall's is. A delta that
        // read unscoped would tell one tenant what changed in another's memory.
        ReturnsDelta(Fact("Initech"));
        var sut = Provider(o =>
        {
            o.InjectDeltaOnSessionResume = true;
            o.MaxDeltaItemsPerSection = 7;
        });

        await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddHours(-2));

        await _memoryService.Received(1).RecallChangedSinceAsync(
            Arg.Is<MemoryDeltaRequest>(r => r.UserId == "alice" && r.MaxItemsPerSection == 7),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheDeltaComplementsRecallRatherThanReplacingIt()
    {
        ReturnsDelta(Fact("Initech"));
        var sut = Provider(o => o.InjectDeltaOnSessionResume = true);

        await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddHours(-2));

        await _memoryService.Received(1).RecallAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEmptyDeltaAddsNothingToThePrompt()
    {
        ReturnsDelta();  // nothing changed
        var sut = Provider(o => o.InjectDeltaOnSessionResume = true);

        var context = await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddHours(-2));

        context.Messages.Should().NotContain(
            m => m.Text.Contains("What changed since we last spoke"),
            "an empty window is not worth a heading");
    }

    // ── (iii) on + young checkpoint ⇒ zero calls ──────────────────────

    [Fact]
    public async Task WithTheFlagOnAndAYoungCheckpointNoDeltaIsFetched()
    {
        var sut = Provider(o => o.InjectDeltaOnSessionResume = true);

        await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddMinutes(-5));  // default gap is 30 minutes

        await _memoryService.DidNotReceive().RecallChangedSinceAsync(
            Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoCheckpointAtAllNoDeltaIsFetched()
    {
        // A brand-new session has nothing to be caught up on.
        var sut = Provider(o => o.InjectDeltaOnSessionResume = true);

        await sut.BuildContextAsync(Turn(), "s1", "c1", CancellationToken.None, "alice");

        await _memoryService.DidNotReceive().RecallChangedSinceAsync(
            Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheGapThresholdIsTheConfiguredOne()
    {
        ReturnsDelta(Fact("Initech"));
        var sut = Provider(o =>
        {
            o.InjectDeltaOnSessionResume = true;
            o.MinimumDeltaGap = TimeSpan.FromMinutes(1);
        });

        await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddMinutes(-5));

        await _memoryService.Received(1).RecallChangedSinceAsync(
            Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>());
    }

    // ── degradation ───────────────────────────────────────────────────

    [Fact]
    public async Task AFailingDeltaDegradesToNormalRecallInsteadOfFailingTheTurn()
    {
        _memoryService.RecallChangedSinceAsync(Arg.Any<MemoryDeltaRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<MemoryDelta>>(_ => throw new InvalidOperationException("graph is down"));
        var sut = Provider(o => o.InjectDeltaOnSessionResume = true);

        var act = async () => await sut.BuildContextAsync(
            Turn(), "s1", "c1", CancellationToken.None, "alice",
            deltaCheckpoint: Now.AddHours(-2));

        await act.Should().NotThrowAsync();
        await _memoryService.Received(1).RecallAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }
}
