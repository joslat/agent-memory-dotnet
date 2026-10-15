using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Gate;
using AgentMemory.RouterArena.Ask;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class PairedGateCommandTests
{
    [Fact]
    public async Task Preparation_never_constructs_a_transport_and_existing_outputs_are_preserved()
    {
        using var files = new Files();
        var calls = 0;
        Func<HttpMessageHandler> factory = () => { calls++; throw new InvalidOperationException(); };
        (await PairedGateCommand.RunAsync(files.Input, files.Config, files.Output, false, TextWriter.Null,
            transportFactory: factory)).Should().Be(0);
        calls.Should().Be(0);
        Directory.Exists(files.Output).Should().BeFalse();
        Directory.CreateDirectory(files.Output);
        (await PairedGateCommand.RunAsync(files.Input, files.Config, files.Output, true, TextWriter.Null,
            transportFactory: factory)).Should().Be(1);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Real_blended_gate_is_used_in_both_arms_and_physical_usage_is_recorded()
    {
        using var files = new Files();
        var bodies = new List<string>();
        var code = await PairedGateCommand.RunAsync(files.Input, files.Config, files.Output, true, TextWriter.Null,
            transportFactory: () => new Handler(async request =>
            {
                var raw = await request.Content!.ReadAsStringAsync();
                bodies.Add(raw);
                var questions = JsonNode.Parse(raw)!["questions"]!.AsObject();
                return JsonSerializer.Serialize(new { answers = questions.ToDictionary(p => p.Key,
                    _ => new { noul = request.RequestUri!.Port == 8765 ? .6 : .1 }), usage = new { input_tokens = 10, output_tokens = 2 } });
            }));
        code.Should().Be(0);
        bodies.Should().HaveCount(4);
        var result = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(files.Output, "result.json")))!;
        result["pair"]!["baseline"]!["scores"]!["a"]!.GetValue<double>().Should().BeApproximately(.5, .00001);
        result["pair"]!["overlay"]!["scores"]!["dream"]!.GetValue<double>().Should().BeApproximately(.5, .00001);
        var events = File.ReadAllLines(Path.Combine(files.Output, "events.jsonl")).Select(s => JsonNode.Parse(s)!).ToArray();
        var finished = events.Where(e => e["event"]!.GetValue<string>() == "decision-finished").ToArray();
        finished.Should().HaveCount(2);
        finished.Sum(e => e["counts"]!["attempts"]!.GetValue<int>()).Should().Be(4);
        finished.Sum(e => e["counts"]!["inputTokens"]!.GetValue<int>()).Should().Be(40);
        result["scope"]!.GetValue<string>().Should().Contain("recorded candidate pools");
    }

    [Theory]
    [InlineData("holdout")]
    [InlineData("gold")]
    [InlineData("foreign-endpoint")]
    [InlineData("changed-original")]
    public async Task Invalid_or_unapproved_input_shape_is_refused_before_transport_creation(string defect)
    {
        using var files = new Files();
        var input = JsonNode.Parse(File.ReadAllText(files.Input))!;
        var config = JsonNode.Parse(File.ReadAllText(files.Config))!;
        if (defect == "holdout") input["role"] = "holdout";
        if (defect == "gold") input["gold"] = "Expected answer";
        if (defect == "changed-original") input["overlay"]!["candidates"]![1]!["text"] = "Changed";
        if (defect == "foreign-endpoint") config["judges"]![0]!["endpoint"] = "https://example.com/judge";
        File.WriteAllText(files.Input, input.ToJsonString());
        File.WriteAllText(files.Config, config.ToJsonString());
        (await PairedGateCommand.RunAsync(files.Input, files.Config, files.Output, true, TextWriter.Null,
            transportFactory: () => throw new InvalidOperationException("No transport"))).Should().Be(1);
        Directory.Exists(files.Output).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_blend_member_is_not_reported_as_the_winning_blend()
    {
        using var files = new Files();
        var code = await PairedGateCommand.RunAsync(files.Input, files.Config, files.Output, true, TextWriter.Null,
            transportFactory: () => new Handler(async request =>
            {
                if (request.RequestUri!.Port == 8766) return "{}";
                var questions = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["questions"]!.AsObject();
                return JsonSerializer.Serialize(new { answers = questions.ToDictionary(p => p.Key, _ => new { noul = .9 }) });
            }));
        code.Should().Be(2);
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(files.Output, "result.json")))!;
        result["completed"]!.GetValue<bool>().Should().BeFalse();
        result["pair"]!["displacedOriginals"].Should().BeNull();
        result["pair"]!["baseline"]!["context"].Should().BeNull();
        var events = File.ReadAllLines(Path.Combine(files.Output, "events.jsonl")).Select(s => JsonNode.Parse(s)!);
        events.Where(e => e["event"]!.GetValue<string>() == "decision-failed")
            .Should().OnlyContain(e => e["counts"]!["judgeFailures"]!.GetValue<int>() == 1);
    }

    [Fact]
    public async Task Cancellation_retains_a_durable_started_record_and_does_not_claim_completion()
    {
        using var files = new Files();
        using var cancellation = new CancellationTokenSource();
        var act = () => PairedGateCommand.RunAsync(files.Input, files.Config, files.Output, true, TextWriter.Null,
            cancellation.Token, () => new Handler(_ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }));
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(files.Output, "result.json")).Should().BeFalse();
        var evidence = File.ReadAllText(Path.Combine(files.Output, "events.jsonl"));
        evidence.Should().Contain("decision-started").And.Contain("run-cancelled").And.NotContain("run-finished");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await reply(request), System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "paired-gate-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(_root, "input.json");
        public string Config => Path.Combine(_root, "config.json");
        public string Output => Path.Combine(_root, "run");
        public Files()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Input, """
                {"schemaVersion":1,"role":"synthetic-development","maxContextCharacters":1000,
                 "baseline":{"turn":"What changed?","conversation":[],"now":"2026-01-01T00:00:00Z",
                 "candidates":[{"key":"a","memoryType":"semantic","text":"The task was planned."}]},
                 "overlay":{"turn":"What changed?","conversation":[],"now":"2026-01-01T00:00:00Z",
                 "candidates":[{"key":"dream","memoryType":"semantic","text":"Model-derived: cancelled."},
                               {"key":"a","memoryType":"semantic","text":"The task was planned."}]}}
                """);
            File.WriteAllText(Config, JsonSerializer.Serialize(new MemoryGateOptions
            {
                Judges = [new() { Name = "one", Model = "fake", Endpoint = new("http://127.0.0.1:8765/v1/systemone"), Weight = .8 },
                          new() { Name = "two", Model = "fake", Endpoint = new("http://127.0.0.1:8766/v1/systemone"), Weight = .2 }],
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
