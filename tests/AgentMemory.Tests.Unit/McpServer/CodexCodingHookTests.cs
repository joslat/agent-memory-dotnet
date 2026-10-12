using System.Net;
using System.Text.Json;
using AgentMemory.McpHost.Coding;
using AgentMemory.McpHost.Coding.Client;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodexCodingHookTests
{
    [Fact]
    public async Task Pairs_by_turn_id_and_keeps_the_queued_receipt_out_of_context()
    {
        using var fixture = new Fixture();
        var recall = await fixture.Hook.HandleAsync(fixture.Event("UserPromptSubmit"));
        JsonSerializer.Serialize(recall).Should().Contain("Neo4j");
        await fixture.Hook.HandleAsync(fixture.Event("Stop", "other"));
        fixture.Calls.Should().ContainSingle();
        var completed = await fixture.Hook.HandleAsync(fixture.Event("Stop"));
        completed.SystemMessage.Should().Contain("queued");
        completed.HookSpecificOutput.Should().BeNull();
        completed.AdditionalContext.Should().BeNull();
        var write = fixture.Calls.Last().Body;
        write.GetProperty("turnId").GetString().Should().Be("turn");
        write.GetProperty("userText").GetString().Should().Be("Use Neo4j.");
        write.GetProperty("assistantText").GetString().Should().Be("Done.");
        write.GetProperty("source").GetString().Should().Be("codex/UserPromptSubmit; origin unverified");
        await fixture.Hook.HandleAsync(fixture.Event("Stop"));
        fixture.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interrupt_discards_capture_and_never_learns_an_error()
    {
        using var fixture = new Fixture();
        await fixture.Hook.HandleAsync(fixture.Event("UserPromptSubmit"));
        await fixture.Hook.HandleAsync(fixture.Event("Interrupt"));
        await fixture.Hook.HandleAsync(fixture.Event("Stop"));
        fixture.Calls.Should().ContainSingle();
        string.Join("", Directory.GetFiles(fixture.Root, "turn-*.json").Select(File.ReadAllText))
            .Should().NotContain("Use Neo4j.");
    }

    [Fact]
    public async Task Missing_turn_id_does_not_use_a_Claude_prompt_id()
    {
        using var fixture = new Fixture();
        var recall = await fixture.Hook.HandleAsync(fixture.Event("UserPromptSubmit", turn: null));
        recall.SystemMessage.Should().Contain("turn_id");
        await fixture.Hook.HandleAsync(fixture.Event("Stop", turn: null));
        fixture.Calls.Should().ContainSingle().Which.Path.Should().EndWith("/recall");
    }

    [Fact]
    public async Task Same_external_session_and_turn_are_distinct_from_Claude_storage()
    {
        using var fixture = new Fixture();
        await fixture.Hook.HandleAsync(fixture.Event("UserPromptSubmit"));
        await fixture.Hook.HandleAsync(fixture.Event("Stop"));
        fixture.Calls.Last().Body.GetProperty("session").GetString().Should().NotBe("session");
    }

    [Fact]
    public async Task Local_pending_state_cannot_be_consumed_by_another_harness()
    {
        using var fixture = new Fixture();
        var request = new CodingRememberRequest { Session = "identical", TurnId = "turn", UserText = "Use Neo4j.",
            Commit = new string('a', 40), Source = "synthetic" };
        await new CodingHookState(fixture.Options).CaptureAsync(request);
        var consumed = false;
        await new CodingHookState(fixture.Options with { Harness = CodingHookHarness.Claude })
            .ConsumeAsync("identical", "turn", (_, _) => { consumed = true; return Task.CompletedTask; });
        consumed.Should().BeFalse();
    }

    [Fact]
    public async Task Existing_Claude_pending_filename_remains_readable_after_upgrade()
    {
        using var fixture = new Fixture();
        var options = fixture.Options with { Harness = CodingHookHarness.Claude };
        var request = new CodingRememberRequest { Session = "session", TurnId = "turn", UserText = "Use Neo4j.",
            Commit = new string('a', 40), Source = "claude/UserPromptSubmit; origin unverified" };
        // The pre-Codex on-disk format: five key elements, with no harness field.
        var legacyKey = JsonSerializer.SerializeToUtf8Bytes(new[] { options.ServiceUrl, options.Project,
            Path.GetFullPath(options.RepositoryRoot), "session", "turn" });
        var legacyPath = Path.Combine(fixture.Root, "turn-" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(legacyKey)) + ".json");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new { request, capturedAtUtc = DateTimeOffset.UtcNow }, CodingApi.Json));
        var consumed = await new CodingHookState(options).ConsumeAsync("session", "turn", (saved, _) =>
        {
            saved.Should().Be(request);
            return Task.CompletedTask;
        });
        consumed.Should().BeTrue();
    }

    [Theory]
    [InlineData("SubagentStop")]
    [InlineData("PreCompact")]
    [InlineData("PostCompact")]
    [InlineData("StopFailure")]
    public async Task Unsupported_events_do_not_read_or_write(string name)
    {
        using var fixture = new Fixture();
        var reply = await fixture.Hook.HandleAsync(fixture.Event(name));
        JsonSerializer.Serialize(reply, CodingApi.Json).Should().Be("{}");
        fixture.Calls.Should().BeEmpty();
        Directory.GetFiles(fixture.Root).Should().BeEmpty();
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codex-hook-test-" + Guid.NewGuid().ToString("N"));
        public CodingHookOptions Options { get; }
        public CodingTurnHook Hook { get; }
        public List<(string Path, JsonElement Body)> Calls { get; } = [];
        private readonly HttpClient _client;
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new() { Harness = CodingHookHarness.Codex, Project = "alpha", TokenVariable = "TOKEN",
                RepositoryRoot = Path.GetFullPath("."), StateDirectory = Root };
            _client = new(this, disposeHandler: false) { BaseAddress = new Uri(Options.ServiceUrl) };
            Hook = new(Options, new([]), new(Options), _client, (_, _) => Task.FromResult(new string('a', 40)));
        }
        public JsonElement Event(string name, string? turn = "turn") => JsonSerializer.SerializeToElement(new
        {
            hook_event_name = name, session_id = "session", turn_id = turn, prompt_id = "never-use-this-alias",
            cwd = Path.GetFullPath("."), prompt = "Use Neo4j.", last_assistant_message = "Done.",
            transcript_path = "never-open.jsonl", model = "synthetic-model", stop_hook_active = false,
        });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsolutePath, JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken))));
            var write = request.RequestUri.AbsolutePath.EndsWith("/remember", StringComparison.Ordinal);
            return new(write ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            {
                Content = new StringContent(write ? """{"jobId":"0123456789abcdef0123456789abcdef"}""" :
                    """{"context":{"relevantFacts":[{"object":"Neo4j"}]},"profile":{"text":"Use Neo4j."}}"""),
            };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client.Dispose();
                foreach (var path in Directory.GetFiles(Root)) File.Delete(path);
                Directory.Delete(Root);
            }
            base.Dispose(disposing);
        }
    }
}
