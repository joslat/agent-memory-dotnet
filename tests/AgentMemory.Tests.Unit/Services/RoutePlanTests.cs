using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Abstractions.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// PLAN 40.20: one route plan per recall. The four decisions (how much, how time was read, whether it split, how it was
/// fitted) land side by side on the context and as one <c>memory.route.plan</c> event on <c>memory.recall.total</c>.
/// </summary>
[Collection("Observability")]
public sealed class RoutePlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly IMemoryContextAssembler _assembler = Substitute.For<IMemoryContextAssembler>();

    private MemoryService Sut(MemoryOptions options)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        return new MemoryService(
            Substitute.For<IShortTermMemoryService>(), _assembler, Substitute.For<IMemoryExtractionPipeline>(),
            Substitute.For<IEntityRepository>(), Substitute.For<IFactRepository>(), Substitute.For<IPreferenceRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(), Options.Create(options), clock, Substitute.For<IIdGenerator>(),
            NullLogger<MemoryService>.Instance);
    }

    private static SubQueryYield Leg(string text) => new()
    {
        Affinity = MemoryTypeAffinity.Semantic, QueryText = text, ItemsRetrieved = 1, UniqueContributions = 1, SurvivedBudget = 1,
    };

    private static MemoryContext Context(RecallFanOutReport? fanOut = null, bool truncated = false) =>
        new() { SessionId = "s", AssembledAtUtc = Now, FanOutReport = fanOut, Truncated = truncated };

    [Fact]
    public async Task A_recall_now_records_how_much_was_asked_and_that_time_was_now()
    {
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(Context(truncated: true));
        var request = new RecallRequest
        {
            SessionId = "s", Query = "where do I live?",
            Options = RecallOptions.Default with { MaxFacts = 7, MaxEntities = 0, MaxRelationships = 5 },
        };

        var result = await Sut(new MemoryOptions { ContextBudget = new ContextBudget { MaxTokens = 2000 } }).RecallAsync(request);

        var route = result.Context.Route!;
        route.Time.Should().Be(MemoryRoutePlan.TimeNow);
        route.ValidAsOf.Should().BeNull();
        route.Recall["facts"].Should().Be(7);
        route.Recall["entities"].Should().Be(0);
        route.Recall["relationships"].Should().Be(5);
        route.Split.Should().BeFalse();
        route.BudgetMaxTokens.Should().Be(2000);
        route.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_host_that_configured_recall_depth_is_reported_with_the_caps_the_recall_used()
    {
        // The caller leaves Options at the default singleton, as the SK plugin, the M.E.AI facade and most direct
        // callers do; the assembler then runs with the host's MemoryOptions.Recall (25.2), and so must the plan.
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(Context());
        var configured = new MemoryOptions { Recall = RecallOptions.Default with { MaxFacts = 25, MaxTraces = 0 } };

        var result = await Sut(configured).RecallAsync(new RecallRequest { SessionId = "s", Query = "where do I live?" });

        result.Context.Route!.Recall["facts"].Should().Be(25);
        result.Context.Route.Recall["traces"].Should().Be(0);
    }

    [Theory]
    [InlineData(TemporalQueryClocks.ValidTimeOnly, false)]
    [InlineData(TemporalQueryClocks.ValidAndTransactionTime, true)]
    public async Task A_question_routed_in_time_reports_the_known_as_clock_only_when_it_was_not_now(
        TemporalQueryClocks clocks, bool expectKnownAsOf)
    {
        _assembler.AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Context());

        var result = await Sut(new MemoryOptions { ResolveTemporalQueries = true, TemporalQueryClocks = clocks })
            .RecallAsync(new RecallRequest { SessionId = "s", Query = "what did we decide in March 2024" });

        var route = result.Context.Route!;
        if (expectKnownAsOf) route.KnownAsOf.Should().Be(route.ValidAsOf);
        else route.KnownAsOf.Should().BeNull();
    }

    [Fact]
    public async Task A_question_that_names_a_time_is_routed_as_of_it_and_says_so()
    {
        _assembler.AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Context());

        var result = await Sut(new MemoryOptions { ResolveTemporalQueries = true })
            .RecallAsync(new RecallRequest { SessionId = "s", Query = "what did we decide in March 2024" });

        result.Context.Route!.Time.Should().Be(MemoryRoutePlan.TimeFromQuestion);
        result.Context.Route.ValidAsOf.Should().NotBeNull();
        result.Context.Route.ValidAsOf!.Value.Year.Should().Be(2024);
    }

    [Fact]
    public async Task An_as_of_request_records_both_clocks()
    {
        _assembler.AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Context());
        var valid = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var known = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await Sut(new MemoryOptions()).RecallAsOfAsync(new RecallRequest { SessionId = "s", Query = "q" }, valid, known);

        result.Context.Route!.Time.Should().Be(MemoryRoutePlan.TimeRequested);
        result.Context.Route.ValidAsOf.Should().Be(valid);
        result.Context.Route.KnownAsOf.Should().Be(known);
    }

    [Theory]
    [InlineData("now")]
    [InlineData("question")]
    [InlineData("requested")]
    public async Task Every_path_puts_exactly_one_route_event_on_its_own_recall_total_span(string path)
    {
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(Context());
        _assembler.AssembleContextAsOfAsync(Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Context());
        // A parent of our own, so spans from tests running beside this one are told apart by trace id; and an
        // ambient span that is NOT memory.recall.total, which the event must never land on.
        using var parent = new Activity("route-plan-test").Start();
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (activity.TraceId == parent.TraceId) lock (stopped) stopped.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        var sut = Sut(new MemoryOptions { ResolveTemporalQueries = true });

        var result = path switch
        {
            "now" => await sut.RecallAsync(new RecallRequest { SessionId = "s", Query = "where do I live?" }),
            "question" => await sut.RecallAsync(new RecallRequest { SessionId = "s", Query = "what did we decide in March 2024" }),
            _ => await sut.RecallAsOfAsync(new RecallRequest { SessionId = "s", Query = "q" }, Now.AddYears(-1)),
        };

        result.Context.Route!.Time.Should().Be(path);
        List<(string Span, ActivityEvent Event)> routeEvents;
        lock (stopped)
            routeEvents = stopped.SelectMany(a => a.Events.Where(e => e.Name == MemoryTelemetry.RoutePlanEvent).Select(e => (a.OperationName, e))).ToList();
        routeEvents.Should().ContainSingle().Which.Span.Should().Be("memory.recall.total");
        parent.Events.Should().NotContain(e => e.Name == MemoryTelemetry.RoutePlanEvent);
    }

    [Fact]
    public async Task A_split_question_records_its_rules_and_sub_queries_and_the_trace_reads_one_event()
    {
        var fanOut = new RecallFanOutReport
        {
            GateFired = true,
            FiredRules = ["E1", "E3"],
            SubQueries = [Leg("a"), Leg("b")],
        };
        _assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(Context(fanOut));
        var events = new List<ActivityEvent>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "memory.recall.total") lock (events) events.AddRange(activity.Events);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var result = await Sut(new MemoryOptions()).RecallAsync(new RecallRequest { SessionId = "s", Query = "who and where?" });

        result.Context.Route!.Split.Should().BeTrue();
        result.Context.Route.SplitRules.Should().Equal("E1", "E3");
        result.Context.Route.SubQueries.Should().Be(2);
        ActivityEvent route;
        lock (events) route = events.Last(e => e.Name == MemoryTelemetry.RoutePlanEvent);
        var tags = route.Tags.ToDictionary(t => t.Key, t => t.Value);
        tags[MemoryTelemetry.RouteTime].Should().Be(MemoryRoutePlan.TimeNow);
        tags[MemoryTelemetry.RouteFanOutFired].Should().Be(true);
        tags[MemoryTelemetry.RouteFanOutRules].Should().Be("E1,E3");
        tags[MemoryTelemetry.RouteFanOutLegs].Should().Be(2);
        ((string)tags[MemoryTelemetry.RouteRecall]!).Should().Contain("facts:10");
    }
}
