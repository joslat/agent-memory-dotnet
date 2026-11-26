using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

/// <summary>
/// GATE-DEADLINE-WHOEVER (owner, 26 Nov 2026): the judges are asked at once and, at the deadline, the gate keeps whoever has
/// answered. Both in time: the blend. One in time: that judge alone, at its own bar when it has one. A memory type no judge
/// answered in time: the similarity floor for that type. Nothing in time: the floor. A late judge is not a failing judge.
/// </summary>
public sealed class GateDeadlineTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Never = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Two System One judges on one fake server: the judge is told by the host (jev.test or laya.test), the memory type by the
    /// state. Each answer can wait (honouring cancellation, as a real request does) and scores each memory by its text.
    /// </summary>
    private sealed class Judges(Func<string, string, TimeSpan?> delay, Func<string, string, double> score) : HttpMessageHandler
    {
        public List<(string Judge, string Type)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var judge = request.RequestUri!.Host.StartsWith("jev", StringComparison.Ordinal) ? "jev" : "laya";
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var state = doc.RootElement.GetProperty("state");
            var type = state.GetProperty("memoryType").GetString()!;
            lock (Calls) Calls.Add((judge, type));
            if (delay(judge, type) is { } wait)
                await Task.Delay(wait, cancellationToken);
            var memories = state.GetProperty("memories");
            var answers = doc.RootElement.GetProperty("questions").EnumerateObject()
                .ToDictionary(q => q.Name, q => new { noul = score(judge, memories.GetProperty(q.Name).GetString()!) });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { answers }), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static SystemOneEndpoint Jev(double? threshold = null) =>
        new() { Name = "jev", Endpoint = new("https://jev.test/v1/systemone"), Weight = 0.8, Threshold = threshold };

    private static SystemOneEndpoint Laya(double? threshold = 0.01) =>
        new() { Name = "laya", Endpoint = new("http://laya.test/v1/systemone"), Weight = 0.2, Threshold = threshold };

    private static MemoryGateOptions Options(params SystemOneEndpoint[] judges) =>
        new() { Judges = [.. judges], Timeout = Deadline, Threshold = 0.23 };

    private static SystemOneMemoryGate Gate(Judges server, MemoryGateOptions options) => new(
        new SystemOneClient(new HttpClient(server)), Microsoft.Extensions.Options.Options.Create(options),
        NullLogger<SystemOneMemoryGate>.Instance);

    private static MemoryGateRequest TwoTypes() => new(
        "any film recs?", [new MemoryGateTurn("user", "I have a free evening")], new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.Zero),
        [new MemoryGateCandidate("c1", "preference", "Studio Ghibli films are a comfort watch"),
         new MemoryGateCandidate("c2", "semantic", "Marta | lives in | Lyon")]);

    // ---- the gate ----

    [Fact]
    public async Task Both_judges_in_time_are_blended_as_before()
    {
        var server = new Judges((_, _) => null, (judge, _) => judge == "jev" ? 0.9 : 0.4);

        var decision = await Gate(server, Options(Jev(), Laya())).DecideAsync(TwoTypes());

        decision.AnsweredBy.Should().Be("jev+laya");
        decision.Probabilities.Should().ContainKeys("c1", "c2");
        decision.Probabilities["c1"].Should().BeApproximately(0.8 * 0.9 + 0.2 * 0.4, 1e-9);
        decision.Late.Should().BeEmpty();
        decision.Thresholds.Should().BeNull("both judges answered every type: the caller's own bar");
    }

    [Fact]
    public async Task Jev_late_keeps_layas_answer_at_layas_own_bar_and_does_not_wait_for_jev()
    {
        var server = new Judges((judge, _) => judge == "jev" ? Never : null, (_, _) => 0.05);
        var watch = Stopwatch.StartNew();

        var decision = await Gate(server, Options(Jev(), Laya(threshold: 0.01))).DecideAsync(TwoTypes());

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the deadline is 300 ms, not JEV's 30 s");
        decision.AnsweredBy.Should().Be("laya");
        decision.Late.Should().Equal("jev");
        decision.Probabilities.Should().ContainKeys("c1", "c2");
        decision.Probabilities.Values.Should().AllSatisfy(p => p.Should().BeApproximately(0.05, 1e-9));
        decision.Thresholds.Should().NotBeNull().And.Subject.Should().ContainKeys("c1", "c2");
        decision.Thresholds!["c1"].Should().Be(0.01);
    }

    [Fact]
    public async Task Laya_late_keeps_jevs_answer_at_the_callers_bar()
    {
        var server = new Judges((judge, _) => judge == "laya" ? Never : null, (_, _) => 0.7);

        var decision = await Gate(server, Options(Jev(), Laya())).DecideAsync(TwoTypes());

        decision.AnsweredBy.Should().Be("jev");
        decision.Late.Should().Equal("laya");
        decision.Probabilities["c2"].Should().BeApproximately(0.7, 1e-9);
        decision.Thresholds.Should().BeNull("JEV has no bar of its own: the caller's threshold applies");
    }

    [Fact]
    public async Task A_type_one_judge_was_late_on_is_judged_by_the_other_alone()
    {
        // JEV answers facts in time but not preferences; Laya answers both.
        var server = new Judges((judge, type) => judge == "jev" && type == "preference" ? Never : null,
            (judge, _) => judge == "jev" ? 0.9 : 0.3);

        var decision = await Gate(server, Options(Jev(), Laya(threshold: 0.01))).DecideAsync(TwoTypes());

        decision.AnsweredBy.Should().Be("jev+laya");
        decision.Late.Should().Equal("jev");
        decision.Probabilities["c1"].Should().BeApproximately(0.3, 1e-9, "preferences: Laya alone");
        decision.Probabilities["c2"].Should().BeApproximately(0.8 * 0.9 + 0.2 * 0.3, 1e-9, "facts: both, blended");
        decision.Thresholds.Should().ContainKey("c1").And.NotContainKey("c2");
    }

    [Fact]
    public async Task A_type_no_judge_answered_in_time_is_left_unanswered_as_a_whole()
    {
        var server = new Judges((_, type) => type == "preference" ? Never : null, (_, _) => 0.6);

        var decision = await Gate(server, Options(Jev(), Laya())).DecideAsync(TwoTypes());

        decision.Probabilities.Should().ContainKey("c2").And.NotContainKey("c1");
        decision.Late.Should().Equal("jev", "laya");
    }

    [Fact]
    public async Task Nothing_in_time_is_a_timeout()
    {
        var server = new Judges((_, _) => Never, (_, _) => 0.6);

        var act = () => Gate(server, Options(Jev(), Laya())).DecideAsync(TwoTypes());

        (await act.Should().ThrowAsync<TimeoutException>()).WithMessage("*did not answer within 300 ms*");
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_lateness()
    {
        var server = new Judges((_, _) => Never, (_, _) => 0.6);
        var options = Options(Jev(), Laya());
        options.Timeout = TimeSpan.FromSeconds(10);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => Gate(server, options).DecideAsync(TwoTypes(), caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_late_judge_is_not_counted_as_a_failing_judge()
    {
        var server = new Judges((judge, _) => judge == "jev" ? Never : null, (_, _) => 0.5);
        var options = Options(Jev(), Laya());
        options.JudgeFailuresBeforeCooldown = 1;          // one failure would leave JEV out for the cool-down
        var gate = Gate(server, options);

        for (var round = 0; round < 3; round++)
            (await gate.DecideAsync(TwoTypes())).Late.Should().Equal("jev");

        server.Calls.Count(c => c.Judge == "jev").Should().Be(6, "late three times, JEV was still asked every time (two types a round)");
    }

    [Fact]
    public void A_judges_own_bar_outside_0_to_1_is_refused()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryContextAssembler>(new Inner(Wide, Floor));
        services.AddAgentMemoryGate(o => o.Judges = [Laya(threshold: 1.5)]);

        var act = () => services.BuildServiceProvider().GetRequiredService<IOptions<MemoryGateOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*SystemOneEndpoint.Threshold*");
    }

    // ---- the assembler: a type that took the floor, and the judge's own bar ----

    private static Fact F(string id, string s, string p, string o) => new()
    {
        FactId = id, Subject = s, Predicate = p, Object = o, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static MemoryContext Context(Fact[] relevant, Fact[] due) => new()
    {
        SessionId = "s",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        RelevantFacts = new MemoryContextSection<Fact>
        {
            Items = relevant,
            RankedItems = [.. relevant.Select((f, i) => new MemoryContextRankedItem(f.FactId, 0.9 - i * 0.1, i + 1, i + 1))],
        },
        DueFacts = new MemoryContextSection<Fact>
        {
            Items = due,
            RankedItems = [.. due.Select((f, i) => new MemoryContextRankedItem(f.FactId, 0.5, i + 1, i + 1))],
        },
    };

    // The wide search finds three facts and two due ones; the floor (the recall without the gate) keeps one of each.
    private static readonly MemoryContext Wide = Context(
        [F("f1", "Marta", "is running", "the Lyon 10k"), F("f2", "Marta", "likes", "jazz"), F("f3", "Marta", "lives in", "Lyon")],
        [F("d1", "Marta", "has a dentist appointment on", "27 November"), F("d2", "Marta", "renews her passport on", "1 December")]);

    private static readonly MemoryContext Floor = Context([F("f3", "Marta", "lives in", "Lyon")],
        [F("d1", "Marta", "has a dentist appointment on", "27 November")]);

    private sealed class Inner(MemoryContext wide, MemoryContext floor) : IMemoryContextAssembler
    {
        public List<RecallRequest> Requests { get; } = [];

        public Task<MemoryContext> AssembleContextAsync(RecallRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(request.Options.MinSimilarityScore == 0 ? wide : floor);
        }

        public Task<MemoryContext> AssembleContextAsOfAsync(RecallRequest request, DateTimeOffset asOf, DateTimeOffset? systemAsOf = null,
            CancellationToken cancellationToken = default) => Task.FromResult(floor);
    }

    private static (GatedMemoryContextAssembler Gate, Inner Inner) Assembler(Judges server, MemoryGateOptions options)
    {
        var inner = new Inner(Wide, Floor);
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var gate = new SystemOneMemoryGate(new SystemOneClient(new HttpClient(server)), wrapped, NullLogger<SystemOneMemoryGate>.Instance);
        return (new GatedMemoryContextAssembler(inner, gate, wrapped, NullLogger<GatedMemoryContextAssembler>.Instance), inner);
    }

    private static RecallRequest Request() => new() { SessionId = "s", Query = "should I still run?" };

    [Fact]
    public async Task A_memory_type_no_judge_answered_in_time_takes_the_floors_memories_and_the_rest_is_judged()
    {
        // Facts are judged in time (only the 10k is needed); the due facts are answered by no judge before the deadline.
        var server = new Judges((_, type) => type == "prospective" ? Never : null,
            (_, text) => text.Contains("10k", StringComparison.Ordinal) ? 0.9 : 0.05);
        var (gate, inner) = Assembler(server, Options(Jev(), Laya(threshold: null)));

        var context = await gate.AssembleContextAsync(Request());

        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal(["f1"], "facts: what the judges kept");
        context.DueFacts.Items.Select(f => f.FactId).Should().Equal(["d1"], "due facts: the floor's, not everything found");
        context.Metadata["gate"].Should().Be("judge");
        context.Metadata["gate.floorTypes"].Should().Be("prospective");
        context.Metadata["gate.late"].Should().Be("jev+laya");
        var trace = (MemoryGateTrace)context.Metadata[MemoryGateTrace.MetadataKey];
        trace.FloorTypes.Should().Equal("prospective");
        trace.LateJudges.Should().Equal("jev", "laya");
        inner.Requests.Should().HaveCount(2, "the wide search, then the floor once, for the type no judge answered");
    }

    [Fact]
    public async Task One_judge_alone_holds_its_memories_to_its_own_bar()
    {
        // JEV late everywhere; Laya scores the 10k 0.05: under the caller's 0.23, over Laya's own 0.01.
        var server = new Judges((judge, _) => judge == "jev" ? Never : null,
            (_, text) => text.Contains("10k", StringComparison.Ordinal) ? 0.05 : 0.001);

        var withOwnBar = await Assembler(server, Options(Jev(), Laya(threshold: 0.01))).Gate.AssembleContextAsync(Request());
        var withoutOwnBar = await Assembler(server, Options(Jev(), Laya(threshold: null))).Gate.AssembleContextAsync(Request());

        withOwnBar.RelevantFacts.Items.Select(f => f.FactId).Should().Equal(["f1"]);
        withOwnBar.Metadata["gate.judgedBy"].Should().Be("laya");
        withoutOwnBar.RelevantFacts.Items.Should().BeEmpty("at the caller's 0.23 nothing Laya scored goes in");
    }

    [Fact]
    public async Task Nothing_in_time_is_the_floor_with_the_timeout_reason()
    {
        var server = new Judges((_, _) => Never, (_, _) => 0.9);
        var (gate, _) = Assembler(server, Options(Jev(), Laya()));

        var context = await gate.AssembleContextAsync(Request());

        context.Metadata["gate"].Should().Be("floor (fallback)");
        ((string)context.Metadata["gate.reason"]).Should().Contain("did not answer within 300 ms");
        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal(["f3"]);
    }
}
