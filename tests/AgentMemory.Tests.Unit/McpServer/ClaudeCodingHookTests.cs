using System.Net;
using System.Text.Json;
using AgentMemory.McpHost.Coding;
using AgentMemory.McpHost.Coding.Client;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class ClaudeCodingHookTests
{
    [Fact]
    public async Task Captures_then_recalls_and_submits_only_the_matching_finished_turn()
    {
        using var fixture = new Fixture();
        var recalled = await fixture.Hook.HandleAsync(fixture.Event("UserPromptSubmit", prompt: "Use Neo4j."));
        JsonSerializer.Serialize(recalled).Should().Contain("Neo4j").And.Contain("untrusted");
        fixture.Handler.Calls.Should().ContainSingle().Which.Path.Should().EndWith("/alpha/recall");
        await fixture.Hook.HandleAsync(fixture.Event("Stop", promptId: "other", reply: "Wrong reply"));
        fixture.Handler.Calls.Should().ContainSingle();
        var stopped = await fixture.Hook.HandleAsync(fixture.Event("Stop", reply: "Done."));
        stopped.HookSpecificOutput.Should().BeNull("a Stop acknowledgement must not restart the agent");
        stopped.SystemMessage.Should().Contain("queued");
        var write = fixture.Handler.Calls.Last();
        write.Path.Should().EndWith("/alpha/remember");
        write.Body.GetProperty("userText").GetString().Should().Be("Use Neo4j.");
        write.Body.GetProperty("assistantText").GetString().Should().Be("Done.");
        write.Body.GetProperty("commit").GetString().Should().Be(new string('a', 40));
        write.Body.GetProperty("source").GetString().Should().Contain("unverified");
        await fixture.Hook.HandleAsync(fixture.Event("Stop", reply: "Done."));
        fixture.Handler.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Known_secret_never_reaches_cache_or_HTTP()
    {
        using var fixture = new Fixture();
        var run = () => fixture.Hook.HandleAsync(fixture.Event("UserPromptSubmit", prompt: "value " + fixture.Token));
        await run.Should().ThrowAsync<ArgumentException>();
        fixture.Handler.Calls.Should().BeEmpty();
        Directory.GetFiles(fixture.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_failure_does_not_store_an_API_error_as_a_reply()
    {
        using var fixture = new Fixture();
        await fixture.Hook.HandleAsync(fixture.Event("UserPromptSubmit", prompt: "Use Neo4j."));
        await fixture.Hook.HandleAsync(fixture.Event("StopFailure", reply: "API Error: private-canary"));
        await fixture.Hook.HandleAsync(fixture.Event("Stop", reply: "later"));
        fixture.Handler.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Missing_prompt_ID_recalls_but_does_not_guess_a_write_pair()
    {
        using var fixture = new Fixture();
        var recalled = await fixture.Hook.HandleAsync(fixture.Event("UserPromptSubmit", promptId: null, prompt: "Use Neo4j."));
        recalled.SystemMessage.Should().Contain("prompt_id");
        await fixture.Hook.HandleAsync(fixture.Event("Stop", promptId: null, reply: "Done."));
        fixture.Handler.Calls.Should().ContainSingle().Which.Path.Should().EndWith("/recall");
    }

    [Fact]
    public async Task Session_start_uses_profile_and_precompact_does_not_invent_a_digest()
    {
        using var fixture = new Fixture();
        var started = await fixture.Hook.HandleAsync(fixture.Event("SessionStart"));
        JsonSerializer.Serialize(started).Should().Contain("profile-choice");
        await fixture.Hook.HandleAsync(fixture.Event("PreCompact"));
        fixture.Handler.Calls.Should().ContainSingle().Which.Path.Should().EndWith("/profile");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "claude-hook-test-" + Guid.NewGuid().ToString("N"));
        public string Token { get; } = new('x', 40);
        public Handler Handler { get; } = new();
        public CodingTurnHook Hook { get; }
        private readonly HttpClient _client;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var options = new CodingHookOptions { Project = "alpha", TokenVariable = "TOKEN", RepositoryRoot = Path.GetFullPath("."), StateDirectory = Root };
            _client = new(Handler) { BaseAddress = new Uri(options.ServiceUrl) };
            _client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
            Hook = new(options, new([Token]), new(options), _client, (_, _) => Task.FromResult(new string('a', 40)));
        }
        public JsonElement Event(string name, string? promptId = "prompt", string? prompt = null, string? reply = null) =>
            JsonSerializer.SerializeToElement(new { hook_event_name = name, session_id = "session", prompt_id = promptId,
                cwd = Path.GetFullPath("."), prompt, last_assistant_message = reply, transcript_path = "never-read-this-file" });
        public void Dispose()
        {
            _client.Dispose();
            foreach (var file in Directory.GetFiles(Root)) File.Delete(file);
            Directory.Delete(Root);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Path, JsonElement Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((path, body));
            var json = path.EndsWith("/remember", StringComparison.Ordinal)
                ? """{"jobId":"0123456789abcdef0123456789abcdef","state":"queued"}"""
                : path.EndsWith("/profile", StringComparison.Ordinal) ? """{"profile":{"text":"profile-choice"}}"""
                : """{"context":{"relevantFacts":[{"factId":"f1","object":"Neo4j"}]}}""";
            return new(path.EndsWith("/remember", StringComparison.Ordinal) ? HttpStatusCode.Accepted : HttpStatusCode.OK)
                { Content = new StringContent(json) };
        }
    }
}
