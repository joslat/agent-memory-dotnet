using System.Net;
using System.Text.Json;
using AgentMemory.McpHost.Coding;
using AgentMemory.McpHost.Coding.Client;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingSessionStartHookTests
{
    [Theory]
    [InlineData(CodingHookHarness.VsCodeLocal)]
    [InlineData(CodingHookHarness.CopilotCli)]
    internal async Task Session_start_uses_the_harness_specific_context_envelope(CodingHookHarness harness)
    {
        using var fixture = new Fixture(harness);
        var reply = await fixture.Hook.HandleAsync(fixture.Event("SessionStart"));
        fixture.Calls.Should().ContainSingle().Which.Should().Be("/projects/alpha/profile");
        fixture.CommitReads.Should().Be(1);
        reply.SystemMessage.Should().BeNull();
        if (harness == CodingHookHarness.CopilotCli)
        {
            reply.AdditionalContext.Should().Contain("Neo4j").And.Contain("untrusted");
            reply.HookSpecificOutput.Should().BeNull();
        }
        else
        {
            reply.AdditionalContext.Should().BeNull();
            var output = JsonSerializer.SerializeToElement(reply.HookSpecificOutput);
            output.GetProperty("hookEventName").GetString().Should().Be("SessionStart");
            output.GetProperty("additionalContext").GetString().Should().Contain("Neo4j");
        }
    }

    [Theory]
    [InlineData("UserPromptSubmit")]
    [InlineData("Stop")]
    [InlineData("PreCompact")]
    [InlineData("SubagentStart")]
    [InlineData("sessionStart")]
    public async Task Other_events_never_guess_a_turn_or_read_transcripts(string name)
    {
        using var fixture = new Fixture(CodingHookHarness.CopilotCli);
        var reply = await fixture.Hook.HandleAsync(fixture.Event(name));
        JsonSerializer.Serialize(reply, CodingApi.Json).Should().Be("{}");
        fixture.Calls.Should().BeEmpty();
        fixture.CommitReads.Should().Be(0);
    }

    [Theory]
    [InlineData("missing-session")]
    [InlineData("secret-session")]
    [InlineData("outside-repo")]
    [InlineData("wrong-git-root")]
    [InlineData("secret-response")]
    [InlineData("oversized-response")]
    [InlineData("unexpected-status")]
    public async Task Rejects_invalid_context_before_injection(string problem)
    {
        using var fixture = new Fixture(CodingHookHarness.VsCodeLocal) { Problem = problem };
        var run = () => fixture.Hook.HandleAsync(fixture.Event("SessionStart"));
        await run.Should().ThrowAsync<Exception>();
        if (problem is "missing-session" or "secret-session" or "outside-repo" or "wrong-git-root")
            fixture.Calls.Should().BeEmpty();
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public int CommitReads { get; private set; }
        public string? Problem { get; set; }
        public CodingSessionStartHook Hook { get; }
        private readonly HttpClient _client;
        private readonly string _root = Path.GetFullPath(".");
        private const string Secret = "private-test-credential";
        public Fixture(CodingHookHarness harness)
        {
            var options = new CodingHookOptions { Harness = harness, Project = "alpha", TokenVariable = "TOKEN",
                RepositoryRoot = _root, StateDirectory = Path.GetTempPath() };
            _client = new(this, disposeHandler: false) { BaseAddress = new Uri(options.ServiceUrl) };
            Hook = new(options, new([Secret]), _client, (_, _) =>
            {
                CommitReads++;
                if (Problem == "wrong-git-root") throw new ArgumentException("Wrong repository.");
                return Task.FromResult(new string('a', 40));
            });
        }
        public JsonElement Event(string name) => JsonSerializer.SerializeToElement(new
        {
            hook_event_name = name, session_id = Problem == "missing-session" ? null : Problem == "secret-session" ? Secret : "session",
            cwd = Problem == "outside-repo" ? Path.GetTempPath() : _root,
            prompt_id = "not-a-supported-correlation-field", prompt = "Do not capture me.",
            transcript_path = "never-open-this-file", last_assistant_message = "Do not store me.",
        });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!.AbsolutePath);
            var text = Problem == "secret-response" ? Secret : Problem == "oversized-response" ? new string('x', 70000) : "Project uses Neo4j.";
            return Task.FromResult(new HttpResponseMessage(Problem == "unexpected-status" ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { profile = new { text } })),
            });
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }
}
