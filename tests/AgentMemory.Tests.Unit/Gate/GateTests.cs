using System.Net;
using System.Text;
using System.Text.Json;
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
/// 41.06: the gate. Judge mode keeps what the judges score at the threshold; the similarity floor is the mode for "off" and the
/// fallback when the judges fail or are late; everything delivers what the wide search found. The request the judge reads
/// is the one measured.
/// </summary>
public sealed class GateTests
{
    private static Fact F(string id, string s, string p, string o, DateTimeOffset? since = null) => new()
    {
        FactId = id, Subject = s, Predicate = p, Object = o, Confidence = 0.9, ValidFrom = since, CreatedAtUtc = DateTimeOffset.UnixEpoch,
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
        RecentMessages = new MemoryContextSection<Message>
        {
            Items =
            [
                new Message { MessageId = "m1", ConversationId = "s", SessionId = "s", Role = "user", Content = "my knee is worse", TimestampUtc = DateTimeOffset.UnixEpoch },
                new Message { MessageId = "m2", ConversationId = "s", SessionId = "s", Role = "user", Content = "should I still run?", TimestampUtc = DateTimeOffset.UnixEpoch.AddMinutes(1) },
            ],
        },
    };

    private sealed class Inner(MemoryContext wide, MemoryContext today) : IMemoryContextAssembler
    {
        public List<RecallRequest> Requests { get; } = [];

        public Task<MemoryContext> AssembleContextAsync(RecallRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(request.Options.MinSimilarityScore == 0 ? wide : today);
        }

        public Task<MemoryContext> AssembleContextAsOfAsync(RecallRequest request, DateTimeOffset asOf, DateTimeOffset? systemAsOf = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(today);
    }

    private sealed class Judge(Func<MemoryGateRequest, CancellationToken, Task<MemoryGateDecision>> decide) : IMemoryGate
    {
        public MemoryGateRequest? Seen { get; private set; }

        public Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default)
        {
            Seen = request;
            return decide(request, cancellationToken);
        }
    }

    private static readonly MemoryContext Wide = Context(
        F("f1", "Marta", "is running", "the Lyon 10k", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
        F("f2", "Marta", "has knee surgery on", "16 November 2026"),
        F("f3", "Marta", "likes", "jazz"));

    private static readonly MemoryContext Today = Context(F("f3", "Marta", "likes", "jazz"));

    private static RecallRequest Request() => new() { SessionId = "s", Query = "should I still run?" };

    private static (GatedMemoryContextAssembler Gate, Inner Inner) Sut(MemoryGateMode mode, IMemoryGate judge, TimeSpan? timeout = null)
    {
        var inner = new Inner(Wide, Today);
        var options = Options.Create(new MemoryGateOptions { Mode = mode, Threshold = 0.23, Timeout = timeout ?? TimeSpan.FromSeconds(3) });
        return (new GatedMemoryContextAssembler(inner, judge, options, NullLogger<GatedMemoryContextAssembler>.Instance), inner);
    }

    private static Judge Scores(params double[] p) => new((r, _) => Task.FromResult(new MemoryGateDecision(
        r.Candidates.Select((c, i) => (c.Key, P: p[i])).ToDictionary(x => x.Key, x => x.P), "jev")));

    [Fact]
    public async Task The_judge_keeps_what_reaches_the_threshold_from_everything_the_wide_search_found()
    {
        var judge = Scores(0.9, 0.4, 0.1);
        var (gate, inner) = Sut(MemoryGateMode.Judge, judge);

        var context = await gate.AssembleContextAsync(Request());

        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("f1", "f2");
        context.RelevantFacts.RankedItems.Select(r => r.ItemId).Should().Equal("f1", "f2");
        context.RecentMessages.Items.Should().HaveCount(2, "the working memory is always in");
        context.Metadata["gate"].Should().Be("judge");
        context.Metadata["gate.offered"].Should().Be(3);
        context.Metadata["gate.kept"].Should().Be(2);
        inner.Requests.Should().ContainSingle().Which.Options.Should().Match<RecallOptions>(o => o.MinSimilarityScore == 0 && o.MaxFacts >= 50);
        judge.Seen!.Candidates.Select(c => c.Text).Should().Equal(
            "Marta | is running | the Lyon 10k (since 1 Sep 2026)", "Marta | has knee surgery on | 16 November 2026", "Marta | likes | jazz");
        judge.Seen.Candidates.Should().OnlyContain(c => c.MemoryType == "semantic");
        judge.Seen.Conversation.Should().ContainSingle().Which.Text.Should().Be("my knee is worse", "the turn itself is not its own conversation");
    }

    [Fact]
    public async Task A_failing_judge_falls_back_to_the_floor_and_says_why()
    {
        var (gate, inner) = Sut(MemoryGateMode.Judge, new Judge((_, _) => throw new HttpRequestException("503")));

        var context = await gate.AssembleContextAsync(Request());

        context.RelevantFacts.Items.Select(f => f.FactId).Should().Equal("f3");
        context.Metadata["gate"].Should().Be("floor (fallback)");
        ((string)context.Metadata["gate.reason"]).Should().Contain("503");
        inner.Requests.Should().HaveCount(2);
        inner.Requests[1].Should().BeSameAs(inner.Requests[1]).And.Match<RecallRequest>(r => ReferenceEquals(r.Options, RecallOptions.Default),
            "the fallback is the floor exactly: the caller's own request");
    }

    [Fact]
    public async Task A_late_judge_falls_back_to_the_floor()
    {
        var (gate, _) = Sut(MemoryGateMode.Judge, new Judge(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new MemoryGateDecision(new Dictionary<string, double>(), "jev");
        }), timeout: TimeSpan.FromMilliseconds(50));

        var context = await gate.AssembleContextAsync(Request());

        context.Metadata["gate"].Should().Be("floor (fallback)");
        ((string)context.Metadata["gate.reason"]).Should().Contain("did not answer within 50 ms");
    }

    [Fact]
    public async Task Floor_mode_is_recall_without_the_gate_and_everything_mode_delivers_the_wide_search()
    {
        var judge = Scores(0, 0, 0);
        var (floor, inner) = Sut(MemoryGateMode.Floor, judge);
        (await floor.AssembleContextAsync(Request())).RelevantFacts.Items.Select(f => f.FactId).Should().Equal("f3");
        inner.Requests.Should().ContainSingle().Which.Options.Should().BeSameAs(RecallOptions.Default);

        var (everything, _) = Sut(MemoryGateMode.Everything, judge);
        var all = await everything.AssembleContextAsync(Request());
        all.RelevantFacts.Items.Should().HaveCount(3);
        all.Metadata["gate"].Should().Be("everything");
        judge.Seen.Should().BeNull("neither mode asks a judge");
    }

    // ---- the System One judges ----

    private sealed class Server(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Calls) Calls.Add((request.RequestUri!.ToString(), body));
            return answer(request.RequestUri!.ToString() + "\n" + body);
        }
    }

    private static HttpResponseMessage Answer(string body, double p)
    {
        using var doc = JsonDocument.Parse(body[(body.IndexOf('\n') + 1)..]);
        var answers = doc.RootElement.GetProperty("questions").EnumerateObject().ToDictionary(q => q.Name, _ => new { noul = p });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { answers }), Encoding.UTF8, "application/json") };
    }

    private static MemoryGateRequest TwoTypes() => new(
        "any film recs?", [new MemoryGateTurn("user", "I have a free evening")], new DateTimeOffset(2026, 10, 3, 11, 0, 0, TimeSpan.Zero),
        [new MemoryGateCandidate("c1", "preference", "Studio Ghibli films are a comfort watch"), new MemoryGateCandidate("c2", "semantic", "Marta | lives in | Lyon")]);

    private static SystemOneMemoryGate GateOver(Server server, params SystemOneEndpoint[] judges) => new(
        new SystemOneClient(new HttpClient(server)), Options.Create(new MemoryGateOptions { Judges = [.. judges] }), NullLogger<SystemOneMemoryGate>.Instance);

    private static readonly SystemOneEndpoint Jev = new() { Name = "jev", Endpoint = new("https://jev.test/v1/systemone"), Weight = 0.8 };
    private static readonly SystemOneEndpoint Laya = new() { Name = "laya", Endpoint = new("http://laya.test/v1/systemone"), Weight = 0.2 };

    [Fact]
    public async Task Each_type_is_asked_once_per_judge_in_the_measured_words_and_the_judges_are_blended()
    {
        var server = new Server(call => Answer(call, call.StartsWith("https://jev", StringComparison.Ordinal) ? 0.5 : 0.1));

        var decision = await GateOver(server, Jev, Laya).DecideAsync(TwoTypes());

        server.Calls.Should().HaveCount(4, "two types, two judges");
        decision.Probabilities["c1"].Should().BeApproximately(0.8 * 0.5 + 0.2 * 0.1, 1e-9);
        decision.AnsweredBy.Should().Be("jev+laya");
        var body = server.Calls.First(c => c.Body.Contains("\"memoryType\":\"preference\"", StringComparison.Ordinal)).Body;
        body.Should().Be("{\"model\":\"jev-latest\",\"state\":{\"today\":\"Saturday 3 October 2026\",\"conversation\":[{\"role\":\"user\",\"text\":\"I have a free evening\"}],"
            + "\"turn\":\"any film recs?\",\"memoryType\":\"preference\",\"meaning\":\"a taste, dislike, habit or diet of the user, or how they want answers\","
            + "\"memories\":{\"m1\":\"Studio Ghibli films are a comfort watch\"},\"examples\":null},"
            + "\"questions\":{\"m1\":{\"type\":\"noul\",\"instructions\":\"Should memory m1 be put in front of the assistant before it replies to the user\\u0027s turn?\","
            + "\"criteria\":{\"true\":\"Yes: it makes the reply right, personal or complete.\",\"false\":\"No: the reply does not need it; it would be noise.\"}}}}");
    }

    [Fact]
    public async Task One_judge_down_leaves_the_other_s_score_and_none_fails_the_decision()
    {
        var oneDown = new Server(call => call.StartsWith("http://laya", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Answer(call, 0.6));
        var decision = await GateOver(oneDown, Jev, Laya).DecideAsync(TwoTypes());
        decision.Probabilities["c2"].Should().BeApproximately(0.6, 1e-9);
        decision.AnsweredBy.Should().Be("jev");

        var allDown = new Server(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await GateOver(allDown, Jev, Laya).Invoking(g => g.DecideAsync(TwoTypes())).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void The_update_judge_asks_one_question_per_pair_in_the_measured_words()
    {
        var request = new MemoryUpdateRequest("I'm not doing the 10k anymore", new DateTimeOffset(2026, 10, 3, 11, 0, 0, TimeSpan.Zero),
            [new MemoryUpdatePair("p1", "Marta | is not doing | the 10k", "Marta | is running | the Lyon 10k")]);

        var q = SystemOneUpdateJudge.Questions(request)["p1"];

        q.Instructions.Should().Be("Does the new memory \"Marta | is not doing | the 10k\" replace this stored one: \"Marta | is running | the Lyon 10k\"?");
        q.True.Should().Be("the stored memory is no longer true once the new one is stored");
        q.False.Should().Be("both stay true, or they are about different things");
    }
}
