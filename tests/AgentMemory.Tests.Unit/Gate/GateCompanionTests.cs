using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

/// <summary>
/// Extensibility 0.12, R1: companions (module items) are judged in the core gate's one call; core memories are kept exactly
/// as without them, and a companion task that fails or is late costs the companions only.
/// </summary>
public sealed class GateCompanionTests
{
    private static Fact F(string id, string o) => new()
    {
        FactId = id, Subject = "Marta", Predicate = "likes", Object = o, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static MemoryContext Context(params Fact[] facts) => new()
    {
        SessionId = "s",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        RelevantFacts = new MemoryContextSection<Fact>
        {
            Items = facts,
            RankedItems = [.. facts.Select((f, i) => new MemoryContextRankedItem(f.FactId, 0.9 - i * 0.1, i + 1, i + 1))],
        },
    };

    private static readonly MemoryContext Wide = Context(F("f1", "jazz"), F("f2", "tea"), F("f3", "hiking"));
    private static readonly MemoryContext Today = Context(F("f1", "jazz"));

    private sealed class Inner(MemoryContext wide, MemoryContext today) : IMemoryContextAssembler
    {
        public Task<MemoryContext> AssembleContextAsync(RecallRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Options.MinSimilarityScore == 0 ? wide : today);

        public Task<MemoryContext> AssembleContextAsOfAsync(RecallRequest request, DateTimeOffset asOf, DateTimeOffset? systemAsOf = null,
            CancellationToken cancellationToken = default) => Task.FromResult(today);
    }

    /// <summary>Scores core memories by their text and companions by key; counts its calls.</summary>
    private sealed class Judge(bool fail = false) : IMemoryGate
    {
        public List<MemoryGateRequest> Seen { get; } = [];

        public Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default)
        {
            Seen.Add(request);
            if (fail) throw new HttpRequestException("503");
            return Task.FromResult(new MemoryGateDecision(request.Candidates.ToDictionary(c => c.Key, c => c.Text switch
            {
                var t when t.Contains("jazz", StringComparison.Ordinal) => 0.9,
                var t when t.Contains("tea", StringComparison.Ordinal) => 0.4,
                var t when t.Contains("hiking", StringComparison.Ordinal) => 0.1,
                "Friday appointment" => 0.8,
                _ => 0.05,
            }), "jev"));
        }
    }

    private static GatedMemoryContextAssembler Sut(IMemoryGate judge, MemoryGateMode mode = MemoryGateMode.Judge,
        MemoryContext? wide = null, TimeSpan? timeout = null) =>
        new(new Inner(wide ?? Wide, Today), judge,
            Options.Create(new MemoryGateOptions { Mode = mode, Threshold = 0.23, Timeout = timeout ?? TimeSpan.FromSeconds(3) }),
            NullLogger<GatedMemoryContextAssembler>.Instance);

    private static readonly MemoryGateCandidate[] Modules =
    [
        new("module:commitments:c1", "com.example.commitments", "Friday appointment"),
        new("module:commitments:c2", "com.example.commitments", "an old note"),
    ];

    private static RecallRequest Request(Task<IReadOnlyList<MemoryGateCandidate>>? companions = null) => new()
    {
        SessionId = "s", Query = "what is on this week?", Companions = companions is null ? null : new RecallCompanions(companions),
    };

    [Fact]
    public async Task CompanionsAreJudgedInTheSameCallAndCoreIsKeptExactlyAsWithoutThem()
    {
        var plainJudge = new Judge();
        var plain = await Sut(plainJudge).AssembleContextAsync(Request());
        var judge = new Judge();
        var pooled = await Sut(judge).AssembleContextAsync(Request(Task.FromResult<IReadOnlyList<MemoryGateCandidate>>(Modules)));

        judge.Seen.Should().ContainSingle("one call for core and module memories");
        judge.Seen[0].Candidates.Select(c => c.MemoryType).Should().Equal("semantic", "semantic", "semantic",
            "com.example.commitments", "com.example.commitments");
        pooled.RelevantFacts.Items.Select(f => f.FactId).Should().Equal(plain.RelevantFacts.Items.Select(f => f.FactId));
        pooled.CompanionDecisions.Should().Equal(new Dictionary<string, double>
        { ["module:commitments:c1"] = 0.8, ["module:commitments:c2"] = 0.05 });
        pooled.Metadata[RecallCompanions.OfferedMetadataKey].Should().Be(2);
        pooled.Metadata[RecallCompanions.JudgedByMetadataKey].Should().Be("jev");
        plain.CompanionDecisions.Should().BeNull();
        plain.Metadata.Should().NotContainKey(RecallCompanions.OfferedMetadataKey, "without companions the context is as before");
    }

    [Fact]
    public async Task AFailingCompanionTaskCostsTheCompanionsOnly()
    {
        var judge = new Judge();
        var context = await Sut(judge).AssembleContextAsync(
            Request(Task.FromException<IReadOnlyList<MemoryGateCandidate>>(new InvalidOperationException("module view failed"))));
        judge.Seen.Should().ContainSingle().Which.Candidates.Should().HaveCount(3);
        context.Metadata["gate"].Should().Be("judge");
        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("f1", "f2");
        context.CompanionDecisions.Should().BeNull();
    }

    [Fact]
    public async Task ALateCompanionTaskDoesNotSendCoreToTheFloor()
    {
        var never = new TaskCompletionSource<IReadOnlyList<MemoryGateCandidate>>();
        var judge = new Judge();
        var context = await Sut(judge, timeout: TimeSpan.FromMilliseconds(100)).AssembleContextAsync(Request(never.Task));
        context.Metadata["gate"].Should().Be("judge", "the judge gets its own full timeout after the companion wait");
        judge.Seen.Should().ContainSingle().Which.Candidates.Should().HaveCount(3);
        context.CompanionDecisions.Should().BeNull();
    }

    [Fact]
    public async Task CompanionsWithAnEmptyRepeatedOrCoreKeyAreNotJudged()
    {
        var judge = new Judge();
        var core = Sut(judge);
        var plain = await Sut(new Judge()).AssembleContextAsync(Request());
        var coreKey = new Judge();
        await Sut(coreKey).AssembleContextAsync(Request());
        var taken = coreKey.Seen[0].Candidates[0].Key;
        var context = await core.AssembleContextAsync(Request(Task.FromResult<IReadOnlyList<MemoryGateCandidate>>(
        [
            Modules[0], Modules[0] with { Text = "repeated" }, new("", "x", "no key"), new(taken, "x", "collides with core"),
        ])));
        judge.Seen.Single().Candidates.Should().HaveCount(4);
        context.CompanionDecisions!.Keys.Should().Equal("module:commitments:c1");
        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal(plain.RelevantFacts.Items.Select(f => f.FactId));
    }

    [Fact]
    public async Task AFailingJudgeLeavesCompanionsUndecidedButOffered()
    {
        var context = await Sut(new Judge(fail: true)).AssembleContextAsync(
            Request(Task.FromResult<IReadOnlyList<MemoryGateCandidate>>(Modules)));
        context.Metadata["gate"].Should().Be("floor (fallback)");
        context.CompanionDecisions.Should().BeNull();
        context.Metadata[RecallCompanions.OfferedMetadataKey].Should().Be(2, "the failed call carried them");
    }

    [Theory]
    [InlineData(MemoryGateMode.Floor)]
    [InlineData(MemoryGateMode.Everything)]
    public async Task OnlyAJudgingGateDecidesCompanions(MemoryGateMode mode)
    {
        var judge = new Judge();
        var context = await Sut(judge, mode).AssembleContextAsync(Request(Task.FromResult<IReadOnlyList<MemoryGateCandidate>>(Modules)));
        judge.Seen.Should().BeEmpty();
        context.CompanionDecisions.Should().BeNull();
    }

    [Fact]
    public async Task CompanionsAreJudgedEvenWhenCoreFoundNothing()
    {
        var judge = new Judge();
        var context = await Sut(judge, wide: Context()).AssembleContextAsync(
            Request(Task.FromResult<IReadOnlyList<MemoryGateCandidate>>(Modules)));
        judge.Seen.Single().Candidates.Should().HaveCount(2);
        context.CompanionDecisions!["module:commitments:c1"].Should().Be(0.8);
    }

    [Fact]
    public void ARequestWithCompanionsIsNeverSerializedWithThem()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Request(Task.FromResult<IReadOnlyList<MemoryGateCandidate>>(Modules)));
        json.Should().NotContain("Companions").And.NotContain("Friday appointment");
    }

    // ---- the shipped System One gate (EXT-measure M2): per-type calls, so companions never change a core call ----

    private sealed class JudgeServer(Func<string, bool> fails) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Bodies) Bodies.Add(body);
            if (fails(body)) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var answers = doc.RootElement.GetProperty("questions").EnumerateObject().ToDictionary(q => q.Name, _ => new { noul = 0.7 });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { answers }), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static SystemOneMemoryGate SystemOne(JudgeServer server) => new(new SystemOneClient(new HttpClient(server)),
        Options.Create(new MemoryGateOptions { Judges = [new() { Name = "jev", Endpoint = new("https://jev.test/v1/systemone"), Weight = 1 }] }),
        NullLogger<SystemOneMemoryGate>.Instance);

    private static readonly MemoryGateCandidate[] Core =
    [
        new("semantic|f1", "semantic", "Marta | likes | jazz"), new("preference|p1", "preference", "Prefers trains to planes"),
    ];

    private static MemoryGateRequest Turn(params MemoryGateCandidate[] candidates) =>
        new("what is on this week?", [], DateTimeOffset.UnixEpoch, candidates);

    [Fact]
    public async Task CompanionsAddTheirOwnCallsAndLeaveEveryCoreCallByteForByte()
    {
        var alone = new JudgeServer(_ => false);
        await SystemOne(alone).DecideAsync(Turn(Core));
        var pooled = new JudgeServer(_ => false);
        var decision = await SystemOne(pooled).DecideAsync(Turn([.. Core, .. Modules.Select(m => m with { Companion = true })]));

        pooled.Bodies.Should().HaveCount(alone.Bodies.Count + 1, "one more call, for the module's own type");
        pooled.Bodies.Should().Contain(alone.Bodies, "core calls are the same requests as without companions");
        decision.Probabilities.Keys.Should().Contain(["semantic|f1", "preference|p1", "module:commitments:c1", "module:commitments:c2"]);
    }

    [Fact]
    public async Task AModuleTypeNoJudgeAnswersLeavesCoreJudgedAndTheCompanionsUnanswered()
    {
        var server = new JudgeServer(body => body.Contains("com.example.commitments", StringComparison.Ordinal));
        var decision = await SystemOne(server).DecideAsync(Turn([.. Core, .. Modules.Select(m => m with { Companion = true })]));

        decision.Probabilities.Keys.Should().BeEquivalentTo(["semantic|f1", "preference|p1"]);
    }

    [Fact]
    public async Task ACoreTypeNoJudgeAnswersStillFailsTheWholeDecision()
    {
        var server = new JudgeServer(body => body.Contains("\"memoryType\":\"semantic\"", StringComparison.Ordinal));
        var act = () => SystemOne(server).DecideAsync(Turn([.. Core, .. Modules.Select(m => m with { Companion = true })]));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*semantic*");
    }

    [Fact]
    public async Task CompanionsAloneThatNoJudgeAnswersAreNoDecision()
    {
        var server = new JudgeServer(_ => true);
        var act = () => SystemOne(server).DecideAsync(Turn([.. Modules.Select(m => m with { Companion = true })]));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*any memory type*");
    }

    [Fact]
    public async Task TheGateMarksTheCompanionsItSends()
    {
        var judge = new Judge();
        await Sut(judge).AssembleContextAsync(Request(Task.FromResult<IReadOnlyList<MemoryGateCandidate>>(Modules)));
        judge.Seen.Single().Candidates.Where(c => c.MemoryType == "com.example.commitments").Should().OnlyContain(c => c.Companion);
        judge.Seen.Single().Candidates.Where(c => c.MemoryType == "semantic").Should().OnlyContain(c => !c.Companion);
    }
}
