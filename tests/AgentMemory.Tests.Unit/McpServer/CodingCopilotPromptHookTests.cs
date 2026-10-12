using System.Net;
using System.Text.Json;
using AgentMemory.McpHost.Coding;
using AgentMemory.McpHost.Coding.Client;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingCopilotPromptHookTests
{
    [Fact]
    public async Task Recalls_only_the_user_query_and_preserves_the_existing_model_prompt()
    {
        using var fixture = new Fixture();
        var reply = await fixture.Hook.HandleAsync(fixture.Input());
        reply.ModifiedTransformedPrompt.Should().StartWith("expanded-file-canary\nUser question?")
            .And.Contain("untrusted").And.Contain("source1").And.NotContain("message-window-canary");
        var call = fixture.Calls.Should().ContainSingle().Subject;
        call.Path.Should().Be("/projects/alpha/recall");
        call.Body.GetProperty("query").GetString().Should().Be("User question?");
        call.Body.GetProperty("session").GetString().Should().StartWith("copilot:").And.NotBe("session");
        call.Body.GetRawText().Should().NotContain("expanded-file-canary");
        reply.AdditionalContext.Should().BeNull();
        reply.HookSpecificOutput.Should().BeNull();
        reply.SystemMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("hook_event_name", "Stop")]
    [InlineData("agent_id", "subagent")]
    [InlineData("agentId", "subagent")]
    public async Task Does_not_reinterpret_other_events_or_subagents(string key, string value)
    {
        using var fixture = new Fixture();
        var reply = await fixture.Hook.HandleAsync(fixture.Input(key, value));
        JsonSerializer.Serialize(reply, CodingApi.Json).Should().Be("{}");
        fixture.Calls.Should().BeEmpty();
        fixture.CommitReads.Should().Be(0);
    }

    [Theory]
    [InlineData("prompt", "private-test-secret")]
    [InlineData("sessionId", "private-test-secret")]
    [InlineData("prompt", "")]
    [InlineData("sessionId", "")]
    [InlineData("transformedPrompt", "")]
    [InlineData("cwd", "relative")]
    public async Task Invalid_or_secret_input_stops_before_git_or_HTTP(string key, string value)
    {
        using var fixture = new Fixture();
        var act = () => fixture.Hook.HandleAsync(fixture.Input(key, value));
        await act.Should().ThrowAsync<ArgumentException>();
        fixture.Calls.Should().BeEmpty();
        fixture.CommitReads.Should().Be(0);
    }

    [Theory]
    [InlineData("prompt", 8001)]
    [InlineData("sessionId", 257)]
    [InlineData("transformedPrompt", 32769)]
    public async Task Input_budgets_precede_HTTP(string key, int length)
    {
        using var fixture = new Fixture();
        var act = () => fixture.Hook.HandleAsync(fixture.Input(key, new string('x', length)));
        await act.Should().ThrowAsync<ArgumentException>();
        fixture.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_a_different_git_root_before_recall()
    {
        using var fixture = new Fixture { WrongRepository = true };
        var act = () => fixture.Hook.HandleAsync(fixture.Input());
        await act.Should().ThrowAsync<ArgumentException>();
        fixture.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_recall_returns_no_override()
    {
        using var fixture = new Fixture { Empty = true };
        JsonSerializer.Serialize(await fixture.Hook.HandleAsync(fixture.Input()), CodingApi.Json).Should().Be("{}");
        fixture.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Secret_in_recalled_evidence_is_not_injected()
    {
        using var fixture = new Fixture { SecretResponse = true };
        var act = () => fixture.Hook.HandleAsync(fixture.Input());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Counts_serialized_bytes_when_bounding_the_output()
    {
        using var fixture = new Fixture();
        var act = () => fixture.Hook.HandleAsync(fixture.Input("transformedPrompt", new string('\u4e00', 12000)));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(CodingHookHarness.Claude)]
    [InlineData(CodingHookHarness.Codex)]
    [InlineData(CodingHookHarness.VsCodeLocal)]
    internal async Task Native_event_requires_explicit_Copilot_harness(CodingHookHarness harness)
    {
        using var fixture = new Fixture(harness);
        var act = () => fixture.Hook.HandleAsync(fixture.Input());
        await act.Should().ThrowAsync<ArgumentException>();
        fixture.Calls.Should().BeEmpty();
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public List<(string Path, JsonElement Body)> Calls { get; } = [];
        public int CommitReads { get; private set; }
        public bool WrongRepository { get; init; }
        public bool Empty { get; init; }
        public bool SecretResponse { get; init; }
        public CodingCopilotPromptHook Hook { get; }
        private readonly HttpClient _client;
        private readonly string _root = Path.GetFullPath(".");
        public Fixture(CodingHookHarness harness = CodingHookHarness.CopilotCli)
        {
            var options = new CodingHookOptions { Harness = harness, Project = "alpha", TokenVariable = "TOKEN",
                RepositoryRoot = _root, StateDirectory = Path.GetTempPath() };
            _client = new(this, disposeHandler: false) { BaseAddress = new Uri(options.ServiceUrl) };
            Hook = new(options, new(["private-test-secret"]), _client, (_, _) =>
            {
                CommitReads++;
                if (WrongRepository) throw new ArgumentException("Wrong repository.");
                return Task.FromResult(new string('a', 40));
            });
        }
        public JsonElement Input(string? key = null, string? value = null)
        {
            var fields = new Dictionary<string, object?> { ["sessionId"] = "session", ["timestamp"] = 1791792000000,
                ["cwd"] = _root, ["prompt"] = "User question?", ["transformedPrompt"] = "expanded-file-canary\nUser question?",
                ["transcriptPath"] = "must-not-read-this-file" };
            if (key is not null) fields[key] = value;
            return JsonSerializer.SerializeToElement(fields);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsolutePath,
                JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken))));
            var json = Empty ? "{\"context\":{}}" : JsonSerializer.Serialize(new { context = new
            {
                relevantFacts = new[] { new { factId = "f1", @object = SecretResponse ? "private-test-secret" : "Neo4j", sourceMessageIds = new[] { "source1" } } },
                recentMessages = new[] { new { content = "message-window-canary" } },
            }});
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }
}
