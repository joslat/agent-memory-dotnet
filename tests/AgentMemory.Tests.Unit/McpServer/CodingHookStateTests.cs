using AgentMemory.McpHost.Coding;
using AgentMemory.McpHost.Coding.Client;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingHookStateTests
{
    [Fact]
    public async Task Pairs_by_session_and_prompt_and_releases_data_after_acceptance()
    {
        using var fixture = new Fixture();
        await fixture.State.CaptureAsync(Request);
        var calls = 0;
        Task Submit(CodingRememberRequest value, CancellationToken _) { value.Should().Be(Request); calls++; return Task.CompletedTask; }
        (await fixture.State.ConsumeAsync("other", "prompt", Submit)).Should().BeFalse();
        (await fixture.State.ConsumeAsync("session", "other", Submit)).Should().BeFalse();
        (await fixture.State.ConsumeAsync("session", "prompt", Submit)).Should().BeTrue();
        (await fixture.State.ConsumeAsync("session", "prompt", Submit)).Should().BeFalse();
        calls.Should().Be(1);
        Directory.GetFiles(fixture.Root).Select(File.ReadAllText).Should().OnlyContain(text => !text.Contains(Request.UserText));
    }

    [Fact]
    public async Task Failed_submission_retains_the_same_turn_for_retry()
    {
        using var fixture = new Fixture();
        await fixture.State.CaptureAsync(Request);
        var failed = () => fixture.State.ConsumeAsync("session", "prompt", (_, _) => throw new HttpRequestException());
        await failed.Should().ThrowAsync<HttpRequestException>();
        (await fixture.State.ConsumeAsync("session", "prompt", (_, _) => Task.CompletedTask)).Should().BeTrue();
    }

    [Fact]
    public async Task Conflicting_capture_cannot_later_pair_the_old_prompt_with_a_new_reply()
    {
        using var fixture = new Fixture();
        await fixture.State.CaptureAsync(Request);
        var conflict = () => fixture.State.CaptureAsync(Request with { UserText = "different" });
        await conflict.Should().ThrowAsync<ArgumentException>();
        (await fixture.State.ConsumeAsync("session", "prompt", (_, _) => throw new Exception("Must not submit"))).Should().BeFalse();
    }

    [Fact]
    public async Task Discarded_or_expired_turns_are_not_submitted()
    {
        using var fixture = new Fixture();
        await fixture.State.CaptureAsync(Request);
        fixture.State.Discard("session", "prompt");
        (await fixture.State.ConsumeAsync("session", "prompt", (_, _) => throw new Exception("Must not submit"))).Should().BeFalse();
        await fixture.State.CaptureAsync(Request with { TurnId = "expires" });
        fixture.Clock.Now += TimeSpan.FromDays(2);
        (await fixture.State.ConsumeAsync("session", "expires", (_, _) => throw new Exception("Must not submit"))).Should().BeFalse();
    }

    [Fact]
    public async Task Shared_state_directory_does_not_cross_project_boundaries()
    {
        using var fixture = new Fixture();
        await fixture.State.CaptureAsync(Request);
        var other = new CodingHookState(fixture.Options with { Project = "beta" }, fixture.Clock);
        (await other.ConsumeAsync("session", "prompt", (_, _) => throw new Exception("Must not submit"))).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_completion_cannot_submit_the_same_turn_twice()
    {
        using var fixture = new Fixture();
        await fixture.State.CaptureAsync(Request);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.State.ConsumeAsync("session", "prompt", async (_, _) => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var second = () => fixture.State.ConsumeAsync("session", "prompt", (_, _) => throw new Exception("Must not submit twice"));
            await second.Should().ThrowAsync<IOException>();
        }
        finally { release.SetResult(); await first; }
    }

    [Fact]
    public async Task Full_cache_refuses_new_pending_data_but_can_reclaim_a_completed_entry()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 128; index++) await fixture.State.CaptureAsync(Request with { TurnId = index.ToString() });
        var capture = () => fixture.State.CaptureAsync(Request with { TurnId = "overflow" });
        await capture.Should().ThrowAsync<IOException>();
        await fixture.State.ConsumeAsync("session", "0", (_, _) => Task.CompletedTask);
        await fixture.State.CaptureAsync(Request with { TurnId = "overflow" });
        Directory.GetFiles(fixture.Root, "turn-*.json").Should().HaveCount(128);
    }

    private static CodingRememberRequest Request => new()
    {
        Session = "session", TurnId = "prompt", UserText = "Prefer the Neo4j store.", Commit = new string('a', 40), Source = "claude/UserPromptSubmit",
    };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "coding-hook-test-" + Guid.NewGuid().ToString("N"));
        public CodingHookOptions Options { get; }
        public CodingHookState State { get; }
        public Clock Clock { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new() { Project = "alpha", TokenVariable = "TOKEN", RepositoryRoot = Path.GetFullPath("."), StateDirectory = Root };
            State = new(Options, Clock);
        }
        public void Dispose()
        {
            foreach (var file in Directory.GetFiles(Root)) File.Delete(file);
            Directory.Delete(Root);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
