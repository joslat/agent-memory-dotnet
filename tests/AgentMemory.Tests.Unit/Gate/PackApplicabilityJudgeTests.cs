using System.Net;
using System.Text;
using System.Text.Json;
using AgentMemory.Gate;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class PackApplicabilityJudgeTests
{
    static readonly IReadOnlyDictionary<string, string> Cards = new Dictionary<string, string> { ["pack.a"] = "Martial arts syllabus", ["pack.b"] = "Music theory and harmony" };
    static HttpResponseMessage Answer(double a = .1, double b = .9) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { answers = new Dictionary<string, object> { ["pack.a"] = new { noul = a }, ["pack.b"] = new { noul = b } } })) };
    static MemoryGateOptions Options(params (string Host, double Weight)[] endpoints)
    {
        var options = new MemoryGateOptions(); options.Judges.Clear();
        foreach (var endpoint in endpoints) options.Judges.Add(new() { Name = endpoint.Host, Endpoint = new("http://" + endpoint.Host + "/v1/systemone"), Weight = endpoint.Weight });
        return options;
    }

    [Fact]
    public async Task Uses_a_separate_bounded_prompt_and_renormalizes_over_complete_judges()
    {
        var handler = new Handler((r, _) => Task.FromResult(r.RequestUri!.Host == "good" ? Answer() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"answers\":{}}") }));
        using var http = new HttpClient(handler); var judge = new SystemOneApplicabilityJudge(http, Options(("good", .8), ("bad", .2)));
        var scores = await judge.DecideAsync("Explain musical intervals", "User: studying harmony", Cards);
        Assert.Equal(.1, scores["pack.a"], 10); Assert.Equal(.9, scores["pack.b"], 10); Assert.Equal(2, handler.Bodies.Count);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("Explain musical intervals", body.RootElement.GetProperty("state").GetProperty("question").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("state").GetProperty("packs").EnumerateObject().Count());
        Assert.Contains("Uncertain relevance", body.RootElement.GetProperty("questions").GetProperty("pack.a").GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task A_slow_judge_has_its_own_deadline_without_losing_a_complete_fast_answer()
    {
        var handler = new Handler(async (r, ct) => { if (r.RequestUri!.Host == "slow") await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Answer(); });
        using var http = new HttpClient(handler); var options = Options(("good", .8), ("slow", .2)); options.Timeout = TimeSpan.FromMilliseconds(80);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var scores = await new SystemOneApplicabilityJudge(http, options).DecideAsync("Intervals", "", Cards, cancel.Token);
        Assert.Equal(.9, scores["pack.b"]);
    }

    [Fact]
    public async Task Oversized_response_is_refused_even_when_its_eventual_JSON_is_valid()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string(' ', 200000) + "{\"answers\":{\"pack.a\":{\"noul\":0.1},\"pack.b\":{\"noul\":0.9}}}") }));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SystemOneApplicabilityJudge(http, Options(("good", 1))).DecideAsync("Intervals", "", Cards));
    }

    [Theory]
    [InlineData("pack\nignore")]
    [InlineData("pack invalid id")]
    public async Task Invalid_descriptor_identifiers_are_refused_before_HTTP(string id)
    {
        var handler = new Handler((_, _) => Task.FromResult(Answer())); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new SystemOneApplicabilityJudge(http, Options(("good", 1))).DecideAsync("Question", "", new Dictionary<string, string> { [id] = "Description" }));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("{\"answers\":{}}")]
    [InlineData("{\"answers\":{\"pack.a\":{\"noul\":-0.1},\"pack.b\":{\"noul\":0.9}}}")]
    [InlineData("{\"answers\":{\"pack.a\":{\"noul\":\"NaN\"},\"pack.b\":{\"noul\":0.9}}}")]
    [InlineData("{\"answers\":{\"pack.a\":{\"noul\":1.1},\"pack.b\":{\"noul\":0.9}}}")]
    public async Task Missing_or_invalid_probabilities_never_become_exclusions(string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SystemOneApplicabilityJudge(http, Options(("good", 1))).DecideAsync("Question", "", Cards));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_finite_huge_weights_do_not_overflow_the_blend()
    {
        var handler = new Handler((_, _) => Task.FromResult(Answer())); using var http = new HttpClient(handler);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SystemOneApplicabilityJudge(http, Options(("good", 1))).DecideAsync("Question", "", Cards, cancel.Token)); Assert.Empty(handler.Bodies);
        var scores = await new SystemOneApplicabilityJudge(http, Options(("one", 1e308), ("two", 1e308))).DecideAsync("Question", "", Cards);
        Assert.Equal(.9, scores["pack.b"], 10);
    }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { var body = await request.Content!.ReadAsStringAsync(cancellationToken); lock (Bodies) Bodies.Add(body); return await send(request, cancellationToken); }
    }
}
