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
/// fitted) land side by side on the context and as one <c>memory.route</c> event on the recall's span.
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
