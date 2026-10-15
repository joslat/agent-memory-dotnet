using System.Text;
using System.Text.Json;
using AgentMemory.McpHost.Coding;
using AgentMemory.McpHost.Coding.Client;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingHookCommandTests
{
    [Fact]
    public async Task Failure_is_nonblocking_and_does_not_echo_input_or_configuration()
    {
        using var output = new StringWriter();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("private-prompt-canary"));
        var result = await CodingHookCommand.RunAsync(["--coding-hook", "private-config-canary-does-not-exist.json"], input, output, _ => "private-key-canary");
        result.Should().Be(0);
        output.ToString().Should().NotContain("canary").And.NotContain("additionalContext");
        JsonSerializer.Deserialize<JsonElement>(output.ToString()).GetProperty("systemMessage").GetString().Should().Contain("skipped");
    }

    [Fact]
    public async Task Oversized_stdin_fails_before_git_state_or_HTTP()
    {
        var path = Path.GetTempFileName();
        var statePath = Path.Combine(Path.GetTempPath(), "unused-hook-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new CodingHookOptions
            {
                Project = "alpha", TokenVariable = "TOKEN", RepositoryRoot = Path.GetFullPath("."), StateDirectory = statePath,
            }, CodingApi.Json));
            using var input = new MemoryStream(new byte[CodingBodyLimit.MaximumBytes + 1]);
            using var output = new StringWriter();
            (await CodingHookCommand.RunAsync(["--coding-hook", path], input, output, _ => new string('x', 40))).Should().Be(0);
            output.ToString().Should().Contain("skipped");
            Directory.Exists(statePath).Should().BeFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Nested_repository_is_rejected_before_reading_its_commit()
    {
        var root = Path.GetFullPath(".");
        var calls = 0;
        var repository = new CodingRepository(root, (_, _, _) =>
        {
            calls++;
            return Task.FromResult(Path.Combine(root, "nested"));
        });
        var read = () => repository.ReadCommitAsync(Path.Combine(root, "nested"));
        await read.Should().ThrowAsync<ArgumentException>();
        calls.Should().Be(1);
    }
}
