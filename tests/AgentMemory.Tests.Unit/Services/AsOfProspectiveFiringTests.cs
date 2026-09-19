using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// D2 — prospective firing on the point-in-time path, and the two-clock gate that guards it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The red-first condition, stated as tests.</b> Firing existed only in
/// <c>AssembleContextAsync</c>; <c>AssembleContextAsOfCoreAsync</c> had no firing block at all. Every
/// prospective question carries a <c>QuestionDate</c> and therefore routes to the as-of path, so the
/// one vertical firing exists to serve was the one place it could never run — and the ablation would
/// have read "indistinguishable" for a second reason after the first was fixed.
/// </para>
/// <para>
/// <b>What must fail.</b> A firing block anchored to the machine clock rather than to
/// <c>validAsOf</c> answers today's question against a past instant's evidence, and looks entirely
/// correct doing so. A block that ignores <c>systemAsOf</c> fires reminders the system had not yet
/// recorded at the instant being asked about. Both are asserted below against the clocks actually
/// passed to the service, because both are invisible in the returned shape.
/// </para>
/// </remarks>
public sealed class AsOfProspectiveFiringTests
{
    private static readonly DateTimeOffset MachineNow = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidAsOf = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SystemAsOf = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Firing off leaves the as-of path byte-identical: no call, empty sections.</summary>
    [Fact]
    public async Task FiringOffDoesNotCallTheService()
    {
        var harness = new Harness();

        var context = await harness.RecallAsOfAsync(firing: false);

        await harness.LongTerm.DidNotReceive().GetDueFactsAsOfAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        context.DueFacts.Items.Should().BeEmpty();
        context.ExpiringFacts.Items.Should().BeEmpty();
    }

    /// <summary>Firing on reaches the point-in-time overload, not the live one.</summary>
    /// <remarks>
    /// The live overload would compile and return plausible reminders. It would also read the machine
    /// clock, which is the defect this whole feature exists to avoid on this path.
    /// </remarks>
    [Fact]
    public async Task FiringOnUsesThePointInTimeOverloadAndNeverTheLiveOne()
    {
        var harness = new Harness();

        await harness.RecallAsOfAsync(firing: true);

        await harness.LongTerm.Received(1).GetDueFactsAsOfAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await harness.LongTerm.DidNotReceive().GetDueFactsAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(),
            Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// THE RED-FIRST GATE: the window is anchored to <c>validAsOf</c>, never to the machine clock.
    /// </summary>
    /// <remarks>
    /// The machine clock here is 2026-09-15 and the as-of instant is 2026-05-04 — four months apart,
    /// so an implementation that reached for <c>_clock.UtcNow</c> cannot coincidentally pass. Both
    /// ends of the window are checked: <c>since</c> is <c>validAsOf - DueLookback</c>, and <c>now</c>
    /// is <c>validAsOf</c> itself.
    /// </remarks>
    [Fact]
    public async Task TheFiringWindowIsAnchoredToValidAsOfNotTheMachineClock()
    {
        var harness = new Harness();
        var lookback = TimeSpan.FromDays(3);

        await harness.RecallAsOfAsync(firing: true, dueLookback: lookback);

        await harness.LongTerm.Received(1).GetDueFactsAsOfAsync(
            ValidAsOf - lookback,
            ValidAsOf,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<int>(),
            Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// THE SECOND CLOCK: <c>systemAsOf</c> is forwarded, so belief is bounded as well as validity.
    /// </summary>
    /// <remarks>
    /// The two clocks are deliberately given DIFFERENT values here. A single-clock implementation —
    /// one that passed <c>validAsOf</c> for both, which is the natural shortcut — passes every other
    /// test in this class and fails this one.
    /// </remarks>
    [Fact]
    public async Task TheTransactionClockIsForwardedSeparatelyFromTheValidClock()
    {
        var harness = new Harness();
        var believedAt = ValidAsOf.AddDays(30);

        await harness.RecallAsOfAsync(firing: true, systemAsOf: believedAt);

        await harness.LongTerm.Received(1).GetDueFactsAsOfAsync(
            Arg.Any<DateTimeOffset>(),
            ValidAsOf,
            believedAt,
            Arg.Any<TimeSpan>(),
            Arg.Any<int>(),
            Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>What fires reaches the caller, in the section built for it.</summary>
    [Fact]
    public async Task FiredFactsLandInTheDueAndExpiringSections()
    {
        var harness = new Harness();
        harness.Returns(new ProspectiveDueResult
        {
            Due = [Fact("renewal")],
            Expiring = [Fact("permit")],
        });

        var context = await harness.RecallAsOfAsync(firing: true);

        context.DueFacts.Items.Should().ContainSingle().Which.Subject.Should().Be("renewal");
        context.ExpiringFacts.Items.Should().ContainSingle().Which.Subject.Should().Be("permit");
    }

    /// <summary>
    /// A fact that is BOTH relevant and due renders once, as volunteered.
    /// </summary>
    /// <remarks>
    /// The live path has always de-duplicated this; the as-of path did not, so one fact could occupy
    /// two sections. That breaks MemoryContext's contract, spends the context budget twice on one
    /// fact, and — the part that matters for a reminder — makes something the system volunteered
    /// look like a coincidence of what the user happened to ask.
    /// </remarks>
    [Fact]
    public async Task AFactThatIsBothRelevantAndDueRendersOnlyAsDue()
    {
        var harness = new Harness();
        harness.RelevantFacts = [Fact("renewal"), Fact("unrelated")];
        harness.Returns(new ProspectiveDueResult { Due = [Fact("renewal")] });

        var context = await harness.RecallAsOfAsync(firing: true, maxFacts: 10);

        context.DueFacts.Items.Should().ContainSingle().Which.FactId.Should().Be("renewal");
        context.RelevantFacts.Items.Select(f => f.FactId)
            .Should().BeEquivalentTo(["unrelated"],
                "the due fact renders in the volunteered section and nowhere else");
    }

    /// <summary>The expiring fact is de-duplicated on the same rule as the due one.</summary>
    [Fact]
    public async Task AFactThatIsBothRelevantAndExpiringRendersOnlyAsExpiring()
    {
        var harness = new Harness();
        harness.RelevantFacts = [Fact("permit")];
        harness.Returns(new ProspectiveDueResult { Expiring = [Fact("permit")] });

        var context = await harness.RecallAsOfAsync(firing: true, maxFacts: 10);

        context.ExpiringFacts.Items.Should().ContainSingle();
        context.RelevantFacts.Items.Should().BeEmpty();
    }

    /// <summary>With firing OFF, nothing is removed — de-dup must not become a silent filter.</summary>
    [Fact]
    public async Task FiringOffLeavesTheRelevantFactsUntouched()
    {
        var harness = new Harness();
        harness.RelevantFacts = [Fact("renewal"), Fact("unrelated")];

        var context = await harness.RecallAsOfAsync(firing: false, maxFacts: 10);

        context.RelevantFacts.Items.Should().HaveCount(2);
    }

    /// <summary>The expiring window travels as a span, measured from the as-of instant downstream.</summary>
    [Fact]
    public async Task TheExpiringWindowIsForwardedAsConfigured()
    {
        var harness = new Harness();
        var window = TimeSpan.FromDays(14);

        await harness.RecallAsOfAsync(firing: true, expiringWindow: window);

        await harness.LongTerm.Received(1).GetDueFactsAsOfAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
            window, Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    private static Fact Fact(string subject) => new()
    {
        FactId = subject,
        Subject = subject,
        Predicate = "is_due",
        Object = "true",
        Confidence = 1.0,
        CreatedAtUtc = ValidAsOf,
    };

    /// <summary>Builds the assembler with every collaborator stubbed except the one under test.</summary>
    private sealed class Harness
    {
        internal ILongTermMemoryService LongTerm { get; } = Substitute.For<ILongTermMemoryService>();

        private ProspectiveDueResult _result = ProspectiveDueResult.Empty;

        internal Harness() => LongTerm.GetDueFactsAsOfAsync(
                Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_result));

        internal void Returns(ProspectiveDueResult result) => _result = result;

        /// <summary>Facts the as-of semantic search will return, for the overlap test.</summary>
        internal IReadOnlyList<Fact> RelevantFacts { get; set; } = [];

        internal Task<MemoryContext> RecallAsOfAsync(
            bool firing,
            TimeSpan? dueLookback = null,
            TimeSpan? expiringWindow = null,
            DateTimeOffset? systemAsOf = null,
            int maxFacts = 0)
        {
            LongTerm.SearchFactsAsOfAsync(
                    Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                    Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(RelevantFacts));

            var shortTerm = Substitute.For<IShortTermMemoryService>();
            shortTerm.GetRecentMessagesAsOfAsync(
                    Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns([]);

            var embeddings = Substitute.For<IEmbeddingOrchestrator>();
            embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new float[8]);

            // The machine clock is deliberately FOUR MONTHS after the as-of instant, so an
            // implementation reaching for it cannot coincidentally produce the right window.
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(MachineNow);

            var options = new MemoryOptions();
            var assembler = new MemoryContextAssembler(
                shortTerm,
                LongTerm,
                Substitute.For<IReasoningMemoryService>(),
                graphRag: null,
                embeddings,
                clock,
                Options.Create(options),
                NullLogger<MemoryContextAssembler>.Instance,
                new DefaultMemoryIsolationPolicy(
                    Options.Create(options.Isolation),
                    NullLogger<DefaultMemoryIsolationPolicy>.Instance));

            var request = new RecallRequest
            {
                SessionId = "session-1",
                UserId = "owner-1",
                Query = "anything due?",
                Options = RecallOptions.Default with
                {
                    ProspectiveFiring = firing,
                    DueLookback = dueLookback ?? RecallOptions.Default.DueLookback,
                    ExpiringWindow = expiringWindow ?? RecallOptions.Default.ExpiringWindow,
                    MaxFacts = maxFacts,
                    MaxEntities = 0,
                    MaxPreferences = 0,
                    MaxRecentMessages = 0,
                    MaxTraces = 0,
                },
            };

            return assembler.AssembleContextAsOfAsync(
                request, ValidAsOf, systemAsOf ?? SystemAsOf, CancellationToken.None);
        }
    }
}
