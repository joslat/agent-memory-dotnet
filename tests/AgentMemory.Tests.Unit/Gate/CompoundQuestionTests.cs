using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class CompoundQuestionTests
{
    [Fact]
    public async Task Off_keeps_the_original_request_and_never_calls_the_planner()
    {
        var fixture = new Fixture(enabled: false);

        var result = await fixture.AssembleAsync();

        fixture.Planner.Calls.Should().Be(0);
        fixture.Judge.Requests.Should().ContainSingle();
        fixture.Judge.Requests[0].Turn.Should().Be(Fixture.Question);
        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline");
        result.Metadata.Keys.Should().NotContain("gate.compound");
    }

    [Fact]
    public async Task Parts_add_memories_without_removing_baseline_or_changing_scope_and_candidates()
    {
        var fixture = new Fixture();

        var result = await fixture.AssembleAsync();

        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline", "part-one", "part-two");
        fixture.Planner.Calls.Should().Be(1);
        fixture.Inner.Requests.Should().ContainSingle().Which.Options.Scope.Should().BeSameAs(fixture.Request.Options.Scope);
        fixture.Judge.Requests.Should().HaveCount(3);
        var baseline = fixture.Judge.Requests[0];
        foreach (var part in fixture.Judge.Requests.Skip(1))
        {
            part.Turn.Should().Contain(Fixture.Question);
            part.Conversation.Should().BeSameAs(baseline.Conversation);
            part.Candidates.Should().BeSameAs(baseline.Candidates);
            part.Now.Should().Be(baseline.Now);
        }
        result.Metadata.Should().ContainKey("gate.compound");
    }

    [Fact]
    public async Task Part_failure_preserves_baseline_and_an_already_completed_part()
    {
        var fixture = new Fixture();
        fixture.Judge.FailCall = 3;

        var result = await fixture.AssembleAsync();

        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline", "part-one");
        fixture.Inner.Requests.Should().ContainSingle("a failed optional part does not rerun floor retrieval");
    }

    [Fact]
    public async Task Optional_timeout_returns_baseline_even_when_the_planner_ignores_cancellation()
    {
        var never = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(60));
        fixture.Planner.Answer = (_, _) => never.Task;

        var result = await fixture.AssembleAsync().WaitAsync(TimeSpan.FromSeconds(3));

        fixture.Planner.Calls.Should().Be(1);
        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline");
        fixture.Inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Enabled_deadline_also_bounds_a_baseline_judge_that_ignores_cancellation()
    {
        var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(60));
        fixture.Judge.HangCall = 1;

        var result = await fixture.AssembleAsync().WaitAsync(TimeSpan.FromSeconds(3));

        result.Metadata["gate"].Should().Be("floor (fallback)");
        fixture.Planner.Calls.Should().Be(0);
        fixture.Inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_late_part_keeps_the_completed_baseline_and_first_part()
    {
        var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(100));
        fixture.Judge.HangCall = 3;

        var result = await fixture.AssembleAsync().WaitAsync(TimeSpan.FromSeconds(3));

        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline", "part-one");
        var trace = (CompoundQuestionTrace)result.Metadata[CompoundQuestionTrace.MetadataKey];
        trace.Outcome.Should().Be("partial");
        trace.Reason.Should().Be("timeout");
        trace.CompletedParts.Should().Be(1);
        trace.AdditionalGateDecisions.Should().Be(2);
        fixture.Inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task The_same_memory_admitted_by_both_parts_is_rendered_once()
    {
        var fixture = new Fixture();
        fixture.Judge.RepeatPart = true;

        var result = await fixture.AssembleAsync();

        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline", "part-one");
    }

    [Fact]
    public async Task A_planner_failure_keeps_baseline_and_has_content_free_diagnostics()
    {
        var fixture = new Fixture();
        fixture.Planner.Answer = (_, _) => throw new InvalidOperationException("private question/provider text");

        var result = await fixture.AssembleAsync();

        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline");
        var trace = (CompoundQuestionTrace)result.Metadata[CompoundQuestionTrace.MetadataKey];
        trace.Reason.Should().Be("planner-failed");
        trace.AdditionalGateDecisions.Should().Be(0);
    }

    [Fact]
    public async Task A_single_need_keeps_baseline_without_extra_gate_calls()
    {
        var fixture = new Fixture();
        fixture.Planner.Answer = (_, _) => Task.FromResult<IReadOnlyList<string>>([]);

        var result = await fixture.AssembleAsync();

        fixture.Judge.Requests.Should().ContainSingle();
        ((CompoundQuestionTrace)result.Metadata[CompoundQuestionTrace.MetadataKey]).Outcome.Should().Be("not-needed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_question_or_pool_skips_optional_work(bool longQuestion)
    {
        var fixture = new Fixture();
        fixture.Inner.CandidateCount = longQuestion ? 3 : 301;
        var request = longQuestion ? fixture.Request with { Query = new string('x', 4097) } : fixture.Request;

        var result = await fixture.Gate.AssembleContextAsync(request);

        fixture.Planner.Calls.Should().Be(0);
        fixture.Judge.Requests.Should().ContainSingle();
        ((CompoundQuestionTrace)result.Metadata[CompoundQuestionTrace.MetadataKey]).Reason.Should().Be("input-limit");
    }

    [Fact]
    public async Task Empty_pool_never_invokes_planning_or_judging()
    {
        var fixture = new Fixture();
        fixture.Inner.CandidateCount = 0;

        await fixture.AssembleAsync();

        fixture.Planner.Calls.Should().Be(0);
        fixture.Judge.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_during_optional_work_is_not_hidden_by_the_baseline()
    {
        using var cancel = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Planner.Answer = (_, _) =>
        {
            cancel.Cancel();
            return Task.FromResult<IReadOnlyList<string>>(["first", "second"]);
        };

        var action = () => fixture.AssembleAsync(cancel.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("single")]
    [InlineData("duplicate")]
    [InlineData("too-many")]
    [InlineData("too-long")]
    public async Task Invalid_plans_do_not_change_baseline(string kind)
    {
        var fixture = new Fixture();
        fixture.Planner.Answer = (_, _) => Task.FromResult<IReadOnlyList<string>>(kind switch
        {
            "single" => ["one"],
            "duplicate" => ["one", " ONE "],
            "too-many" => ["one", "two", "three", "four"],
            _ => [new string('x', 4096), "two"],
        });

        var result = await fixture.AssembleAsync();

        fixture.Planner.Calls.Should().Be(1);
        fixture.Judge.Requests.Should().ContainSingle();
        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("nan")]
    [InlineData("infinite")]
    [InlineData("out-of-range")]
    public async Task Invalid_part_decisions_are_rejected_as_a_whole(string defect)
    {
        var fixture = new Fixture();
        fixture.Judge.Defect = defect;

        var result = await fixture.AssembleAsync();

        fixture.Judge.Requests.Should().HaveCount(3);
        result.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("baseline");
    }

    [Theory]
    [InlineData(MemoryGateMode.Floor)]
    [InlineData(MemoryGateMode.Everything)]
    public async Task Other_modes_never_plan(MemoryGateMode mode)
    {
        var fixture = new Fixture(mode: mode);
        await fixture.AssembleAsync();
        fixture.Planner.Calls.Should().Be(0);
        fixture.Judge.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task As_of_bypasses_planning_and_keeps_both_clocks()
    {
        var fixture = new Fixture();
        var valid = DateTimeOffset.UnixEpoch.AddDays(1);
        var system = valid.AddDays(2);

        await fixture.Gate.AssembleContextAsOfAsync(fixture.Request, valid, system);

        fixture.Inner.AsOf.Should().NotBeNull();
        fixture.Inner.AsOf!.Value.Item1.Should().Be(valid);
        fixture.Inner.AsOf.Value.Item2.Should().Be(system);
        fixture.Planner.Calls.Should().Be(0);
        fixture.Judge.Requests.Should().BeEmpty();
    }

    private sealed class Fixture
    {
        public const string Question = "For the mobile Orion app, who tests it and when is launch?";
        public Inner Inner { get; } = new();
        public Judge Judge { get; } = new();
        public Planner Planner { get; } = new();
        public RecallRequest Request { get; } = new()
        {
            SessionId = "session", UserId = "owner", Query = Question,
            Options = new RecallOptions { Scope = MemoryScope.For("owner", includeShared: false) },
        };
        public GatedMemoryContextAssembler Gate { get; }

        public Fixture(bool enabled = true, TimeSpan? timeout = null, MemoryGateMode mode = MemoryGateMode.Judge)
        {
            Gate = new GatedMemoryContextAssembler(Inner, Judge,
                Options.Create(new MemoryGateOptions { CompoundQuestions = enabled, Mode = mode, Timeout = timeout ?? TimeSpan.FromSeconds(3) }),
                NullLogger<GatedMemoryContextAssembler>.Instance, null, null, Planner);
        }

        public Task<MemoryContext> AssembleAsync(CancellationToken cancellationToken = default) => Gate.AssembleContextAsync(Request, cancellationToken);
    }

    private sealed class Planner : ICompoundQuestionPlanner
    {
        public int Calls { get; private set; }
        public Func<CompoundQuestionRequest, CancellationToken, Task<IReadOnlyList<string>>> Answer { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["Who tests the mobile Orion app?", "When is the mobile Orion launch?"]);
        public Task<IReadOnlyList<string>> PlanAsync(CompoundQuestionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Answer(request, cancellationToken);
        }
    }

    private sealed class Judge : IMemoryGate
    {
        public List<MemoryGateRequest> Requests { get; } = [];
        public int FailCall { get; set; }
        public int HangCall { get; set; }
        public bool RepeatPart { get; set; }
        public string? Defect { get; set; }
        public Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Requests.Count == FailCall) throw new InvalidOperationException("part failed");
            if (Requests.Count == HangCall) return new TaskCompletionSource<MemoryGateDecision>().Task;
            var chosen = RepeatPart && Requests.Count > 1 ? 1 : Requests.Count - 1;
            var scores = request.Candidates.Select((c, i) => (c.Key, Score: i == chosen ? .9 : .01))
                .ToDictionary(x => x.Key, x => x.Score);
            if (Requests.Count > 1)
            {
                var first = request.Candidates[0].Key;
                if (Defect == "missing") scores.Remove(first);
                else if (Defect == "unknown") scores["invented"] = .9;
                else if (Defect == "nan") scores[first] = double.NaN;
                else if (Defect == "infinite") scores[first] = double.PositiveInfinity;
                else if (Defect == "out-of-range") scores[first] = 1.1;
            }
            return Task.FromResult(new MemoryGateDecision(scores, "fake"));
        }
    }

    private sealed class Inner : IMemoryContextAssembler
    {
        public List<RecallRequest> Requests { get; } = [];
        public (DateTimeOffset, DateTimeOffset?)? AsOf { get; private set; }
        public int CandidateCount { get; set; } = 3;
        private MemoryContext Context() => new()
        {
            SessionId = "session", AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = [.. Enumerable.Range(0, CandidateCount).Select(i => i switch
                    { 0 => "baseline", 1 => "part-one", 2 => "part-two", _ => "extra-" + i }).Select(id => new Fact
                {
                    FactId = id, Subject = "Orion", Predicate = "has", Object = id,
                    OwnerId = "owner", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch,
                })],
            },
        };
        public Task<MemoryContext> AssembleContextAsync(RecallRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Context());
        }
        public Task<MemoryContext> AssembleContextAsOfAsync(RecallRequest request, DateTimeOffset asOf,
            DateTimeOffset? systemAsOf = null, CancellationToken cancellationToken = default)
        {
            AsOf = (asOf, systemAsOf);
            return Task.FromResult(Context());
        }
    }
}
