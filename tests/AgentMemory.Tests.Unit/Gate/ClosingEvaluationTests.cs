using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentMemory.RouterArena.Ask;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class ClosingEvaluationTests
{
    [Fact]
    public async Task Cancellation_checkpoints_the_attempt_before_propagating()
    {
        using var files = new Files();
        using var cancellation = new CancellationTokenSource();
        var act = () => ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null,
            cancellation.Token, () => new Handler((_, _) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }));
        await act.Should().ThrowAsync<OperationCanceledException>();
        var result = JsonNode.Parse(await File.ReadAllTextAsync(files.Prefix + ".json"))!;
        result["stopReason"]!.GetValue<string>().Should().Be("cancelled");
        var responses = result["responses"]!.AsArray();
        responses.Should().ContainSingle();
        responses[0]!["counts"]!["attempts"]!.GetValue<int>().Should().Be(1);
        responses[0]!["error"]!.GetValue<string>().Should().Be("cancelled");
    }

    [Fact]
    public async Task Dry_run_reuses_identical_named_requests_without_credentials_or_output()
    {
        using var files = new Files();
        using var output = new StringWriter();
        var result = await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, true, null, output);
        result.Should().Be(0);
        output.ToString().Should().Contain("2 source rows").And.Contain("1 distinct proposals").And.Contain("2 logical requests");
        File.Exists(files.Prefix + ".json").Should().BeFalse();
        File.Exists(files.Prefix + ".lock").Should().BeFalse();
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".json.tmp")]
    [InlineData(".lock")]
    public async Task Existing_evidence_is_not_overwritten(string suffix)
    {
        using var files = new Files();
        await File.WriteAllTextAsync(files.Prefix + suffix, "original");
        var result = await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null);
        result.Should().Be(1);
        (await File.ReadAllTextAsync(files.Prefix + suffix)).Should().Be("original");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task Real_protocol_is_shared_between_arms_and_excludes_labels()
    {
        using var files = new Files();
        var bodies = new List<string>();
        var code = await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null,
            transportFactory: () => new Handler(async (request, token) =>
            {
                bodies.Add(await request.Content!.ReadAsStringAsync(token));
                return Reply("""{"answers":{"p1":{"noul":0.62}},"usage":{"input_tokens":8,"output_tokens":2}}""");
            }));
        code.Should().Be(0);
        bodies.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        foreach (var body in bodies)
        {
            body.Should().NotContain("shouldClose").And.NotContain("rationale").And.NotContain("writer-named");
            var question = JsonNode.Parse(body)!["questions"]!.AsObject();
            question.Count.Should().Be(1);
            question["p1"]!["instructions"]!.GetValue<string>().Should().Be("Does the new memory \"A lives in Lille\" replace this stored one: \"A lives in Lyon\"?");
        }
        var result = JsonNode.Parse(await File.ReadAllTextAsync(files.Prefix + ".json"))!;
        result["completed"]!.GetValue<bool>().Should().BeTrue();
        var bindings = result["cases"]!.AsArray();
        bindings[0]!["candidateRequestId"]!.GetValue<string>().Should().Be(bindings[1]!["candidateRequestId"]!.GetValue<string>());
        bindings[0]!["baselineRequestId"]!.GetValue<string>().Should().Be(bindings[0]!["candidateRequestId"]!.GetValue<string>());
        bindings[1]!["baselineRequestId"]!.GetValue<string>().Should().NotBe(bindings[1]!["candidateRequestId"]!.GetValue<string>());
        var responses = result["responses"]!.AsArray();
        responses.Should().HaveCount(2);
        foreach (var response in responses)
        {
            response!["counts"]!["attempts"]!.GetValue<int>().Should().Be(1);
            response["counts"]!["inputTokens"]!.GetValue<int>().Should().Be(8);
            response["id"]!.GetValue<string>().Should().Be(ClosingEvaluationPlan.Hash(Encoding.UTF8.GetBytes(bodies[responses.IndexOf(response)])));
        }
    }

    [Theory]
    [InlineData("{\"answers\":{}}")]
    [InlineData("{\"answers\":{\"p1\":{\"noul\":1.2}}}")]
    [InlineData("{\"answers\":{\"p1\":{\"noul\":-0.1}}}")]
    [InlineData("{\"answers\":{\"p1\":{\"noul\":\"NaN\"}}}")]
    public async Task Invalid_answers_are_failed_observations_not_zero_scores(string body)
    {
        using var files = new Files();
        var code = await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null,
            transportFactory: () => new Handler((_, _) => Task.FromResult(Reply(body))));
        code.Should().Be(2);
        var result = JsonNode.Parse(await File.ReadAllTextAsync(files.Prefix + ".json"))!;
        foreach (var response in result["responses"]!.AsArray())
        {
            response!["score"].Should().BeNull();
            response["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            response["counts"]!["inputTokens"].Should().BeNull();
        }
    }

    [Fact]
    public async Task Actual_retry_attempts_are_counted_once_per_shared_request()
    {
        using var files = new Files();
        var code = await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null,
            transportFactory: () =>
            {
                var attempts = 0;
                return new Handler((_, _) => Task.FromResult(++attempts == 1
                    ? Reply("{}", HttpStatusCode.ServiceUnavailable) : Reply("{\"answers\":{\"p1\":{\"noul\":0.8}}}")));
            });
        code.Should().Be(0);
        var result = JsonNode.Parse(await File.ReadAllTextAsync(files.Prefix + ".json"))!;
        foreach (var response in result["responses"]!.AsArray())
        {
            response!["counts"]!["attempts"]!.GetValue<int>().Should().Be(2);
            response["counts"]!["failed"]!.GetValue<int>().Should().Be(1);
        }
    }

    [Fact]
    public async Task Persistent_failure_stops_after_three_requests_and_keeps_the_unrun_plan()
    {
        using var files = new Files();
        var data = JsonNode.Parse(await File.ReadAllTextAsync(files.Data))!;
        for (var i = 2; i <= 3; i++)
        {
            var item = data["cases"]![0]!.DeepClone();
            item["id"] = $"extra-{i}"; item["newMemory"] = $"A moved to place {i}";
            data["cases"]!.AsArray().Add(item);
        }
        await File.WriteAllTextAsync(files.Data, data.ToJsonString());
        var code = await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null,
            transportFactory: () => new Handler((_, _) => Task.FromResult(Reply("private provider detail", HttpStatusCode.PaymentRequired))));
        code.Should().Be(2);
        var text = await File.ReadAllTextAsync(files.Prefix + ".json");
        text.Should().NotContain("private provider detail");
        var result = JsonNode.Parse(text)!;
        result["completed"]!.GetValue<bool>().Should().BeFalse();
        result["stopReason"]!.GetValue<string>().Should().Be("three-consecutive-failures");
        result["responses"]!.AsArray().Should().HaveCount(3);
        result["expectedRequestIds"]!.AsArray().Should().HaveCount(4);
    }

    [Theory]
    [InlineData("source", "unknown")]
    [InlineData("kind", "unknown")]
    [InlineData("said", "")]
    public async Task Bad_input_is_rejected_before_transport_creation(string property, string value)
    {
        using var files = new Files();
        var data = JsonNode.Parse(await File.ReadAllTextAsync(files.Data))!;
        data["cases"]![1]![property] = value;
        await File.WriteAllTextAsync(files.Data, data.ToJsonString());
        var code = await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null,
            transportFactory: () => throw new InvalidOperationException("must not resolve transport"));
        code.Should().Be(1);
    }

    [Fact]
    public async Task Conflicting_labels_are_rejected_even_outside_the_selected_prefix()
    {
        using var files = new Files();
        var data = JsonNode.Parse(await File.ReadAllTextAsync(files.Data))!;
        data["cases"]![1]!["shouldClose"] = false;
        await File.WriteAllTextAsync(files.Data, data.ToJsonString());
        (await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, true, 1, TextWriter.Null)).Should().Be(1);
    }

    [Fact]
    public async Task Uncooperative_timeout_is_recorded_and_prevents_more_requests()
    {
        using var files = new Files();
        var config = JsonNode.Parse(await File.ReadAllTextAsync(files.Config))!;
        config["timeout"] = "00:00:00.030";
        await File.WriteAllTextAsync(files.Config, config.ToJsonString());
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var code = await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null,
                transportFactory: () => new Handler((_, _) => release.Task));
            code.Should().Be(2);
            var result = JsonNode.Parse(await File.ReadAllTextAsync(files.Prefix + ".json"))!;
            result["responses"]!.AsArray().Should().ContainSingle();
            result["responses"]![0]!["error"]!.GetValue<string>().Should().Be("timeout");
            result["stopReason"]!.GetValue<string>().Should().Be("pending-request");
        }
        finally { release.TrySetResult(Reply("{\"answers\":{\"p1\":{\"noul\":0.8}}}")); }
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Theory]
    [InlineData("endpoint", "ftp://judge.test/systemone")]
    [InlineData("endpoint", "https://user:secret@judge.test/systemone")]
    [InlineData("endpoint", "https://judge.test/systemone?key=secret")]
    [InlineData("keyVariable", "OPENAI_API_KEY")]
    public async Task Unsafe_or_unsupported_configuration_is_rejected_before_work(string property, string value)
    {
        using var files = new Files();
        var config = JsonNode.Parse(await File.ReadAllTextAsync(files.Config))!;
        config["judge"]![property] = value;
        await File.WriteAllTextAsync(files.Config, config.ToJsonString());
        (await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, false, null, TextWriter.Null,
            transportFactory: () => throw new InvalidOperationException("must not resolve transport"))).Should().Be(1);
    }

    [Fact]
    public async Task Dry_run_does_not_require_the_configured_key_or_resolve_a_transport()
    {
        using var files = new Files();
        var config = JsonNode.Parse(await File.ReadAllTextAsync(files.Config))!;
        config["judge"]!["keyVariable"] = "CLOSING_TEST_MISSING_" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(files.Config, config.ToJsonString());
        (await ClosingEvaluationCommand.RunAsync(files.Data, files.Config, files.Prefix, true, null, TextWriter.Null,
            transportFactory: () => throw new InvalidOperationException("must not resolve transport"))).Should().Be(0);
    }

    internal sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "closing-evaluation-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(_directory, "data.json");
        public string Config => Path.Combine(_directory, "config.json");
        public string Prefix => Path.Combine(_directory, "result");
        public Files()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Data, """
                {"schemaVersion":1,"purpose":"development","nowUtc":"2026-10-12T12:00:00Z","cases":[
                 {"id":"named","source":"writer-named","kind":"fact","said":"I moved.","storedMemory":"A lives in Lyon","newMemory":"A lives in Lille","shouldClose":true,"rationale":"moved"},
                 {"id":"similar","source":"similarity","kind":"fact","said":"I moved.","storedMemory":"A lives in Lyon","newMemory":"A lives in Lille","shouldClose":true,"rationale":"moved"}]}
                """);
            File.WriteAllText(Config, """
                {"judge":{"name":"test","model":"test-model","endpoint":"https://judge.test/systemone"},"timeout":"00:00:03"}
                """);
        }
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}
