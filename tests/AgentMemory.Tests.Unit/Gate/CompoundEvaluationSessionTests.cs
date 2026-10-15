using System.Net;
using System.Text.Json;
using AgentMemory.Gate;
using AgentMemory.RouterArena.Ask;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class CompoundEvaluationSessionTests
{
    [Fact]
    public async Task A_failed_blend_member_cannot_be_reported_as_the_configured_healthy_blend()
    {
        var options = new MemoryGateOptions
        {
            Judges = [
                new SystemOneEndpoint { Name = "good", Endpoint = new Uri("https://good.test/systemone") },
                new SystemOneEndpoint { Name = "bad", Endpoint = new Uri("https://bad.test/systemone") }],
            CompoundDetector = new SystemOneEndpoint { Endpoint = new Uri("https://good.test/systemone") },
        };
        using var session = new CompoundEvaluationSession(options, "https://chat.test/v1", "fake-key", "fake-model",
            new UnavailableJudge(), new Handler(_ => throw new InvalidOperationException("do not split a failed baseline")));

        var (baseline, compound) = await CompoundEvaluation.RunCaseAsync(new CompoundCase
        {
            Id = "partial-blend", Question = "Who owns the task?", Candidates = [new("a", "semantic", "A owns it")],
        }, DateTimeOffset.UnixEpoch, options, session.Gate, session.Planner, session.Snapshot);

        baseline.Error.Should().Be("judge-failure");
        baseline.Kept.Should().Equal("a");
        baseline.AnsweredBy.Should().Be("good");
        baseline.JudgeFailures.Should().Be(1);
        baseline.FailedRequests.Should().Be(2, "the unavailable judge is retried once");
        baseline.Calls.Should().Be(3);
        compound.Error.Should().Be("judge-failure");
    }

    [Fact]
    public async Task The_real_gate_planner_and_chat_SDK_use_the_counted_transports()
    {
        var options = new MemoryGateOptions
        {
            Judges = [new SystemOneEndpoint { Endpoint = new Uri("https://judge.test/v1/systemone") }],
            CompoundDetector = new SystemOneEndpoint { Endpoint = new Uri("https://judge.test/v1/systemone") },
        };
        using var session = new CompoundEvaluationSession(options, "https://chat.test/v1", "fake-key", "fake-model",
            new Handler(async request =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var answers = body.RootElement.GetProperty("questions").EnumerateObject()
                    .ToDictionary(p => p.Name, _ => new { noul = .9 });
                return JsonSerializer.Serialize(new { answers, usage = new { input_tokens = 8, output_tokens = 2 } });
            }),
            new Handler(_ => Task.FromResult("""
                {"id":"fake","object":"chat.completion","created":1700000000,"model":"fake-model",
                 "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant",
                   "content":"{\"parts\":[\"Who owns the task?\",\"When is the task due?\"]}"}}],
                 "usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120}}
                """)));
        var item = new CompoundCase
        {
            Id = "paired", Question = "Who owns the task and when is it due?",
            Candidates = [new("a", "semantic", "A owns it"), new("b", "prospective", "It is due tomorrow")],
        };

        var (baseline, compound) = await CompoundEvaluation.RunCaseAsync(item, DateTimeOffset.UnixEpoch,
            options, session.Gate, session.Planner, session.Snapshot);

        baseline.Calls.Should().Be(2, "one request for each memory type");
        compound.Error.Should().BeNull();
        compound.Calls.Should().Be(8, "two baseline, one detector, one splitter and four part requests");
        compound.TokenReportedResponses.Should().Be(8);
        compound.ReportedInputTokens.Should().Be(156);
        compound.ReportedOutputTokens.Should().Be(34);
        compound.PendingRequests.Should().Be(0);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await response(request), System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class UnavailableJudge : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "bad.test") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var answers = body.RootElement.GetProperty("questions").EnumerateObject().ToDictionary(p => p.Name, _ => new { noul = .9 });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { answers })) };
        }
    }
}
