using AgentMemory.McpHost.Coding;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingContentPolicyTests
{
    [Theory]
    [InlineData("-----BEGIN PRIVATE KEY-----\nencoded-key\n-----END PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("api_key = abcdefghijklmnopqrstuvwxyz")]
    [InlineData("{\"client_secret\":\"abcdefghijklmnopqrstuvwxyz\"}")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz1234567890")]
    public void Recognizable_secrets_are_rejected_without_echoing_input(string input)
    {
        var policy = new CodingContentPolicy([]);
        var run = () => policy.CheckText(input);
        run.Should().Throw<ArgumentException>().WithMessage("Content was rejected by the coding input policy.");
    }

    [Theory]
    [InlineData("UserText")]
    [InlineData("AssistantText")]
    [InlineData("Session")]
    [InlineData("TurnId")]
    [InlineData("Source")]
    [InlineData("Commit")]
    public void Configured_credentials_are_rejected_in_every_input_field(string field)
    {
        var credential = field == "Commit" ? new string('a', 40) : "canary-credential-value";
        var request = Request;
        request = field switch
        {
            "UserText" => request with { UserText = credential },
            "AssistantText" => request with { AssistantText = credential },
            "Session" => request with { Session = credential },
            "TurnId" => request with { TurnId = credential },
            "Source" => request with { Source = credential },
            _ => request with { Commit = credential },
        };
        var policy = new CodingContentPolicy([credential]);
        var run = () => policy.Check(request);
        run.Should().Throw<ArgumentException>().WithMessage("Content was rejected by the coding input policy.");
    }

    [Fact]
    public void Transcript_shaped_json_and_long_multiline_input_are_rejected()
    {
        var policy = new CodingContentPolicy([]);
        var transcript = () => policy.CheckText("{\"role\":\"user\",\"content\":\"hi\"}\n{\"role\":\"assistant\",\"content\":\"hello\"}");
        var bulk = () => policy.CheckText(string.Join('\n', Enumerable.Repeat("line", 42)));
        transcript.Should().Throw<ArgumentException>();
        bulk.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decisions_and_short_command_recipes_are_allowed()
    {
        var policy = new CodingContentPolicy(["canary-credential-value"]);
        policy.Invoking(p => p.Check(Request)).Should().NotThrow();
        policy.Invoking(p => p.CheckText("Run `dotnet test` before committing. Keep API keys in environment variables."))
            .Should().NotThrow();
    }

    private static CodingRememberRequest Request => new()
    {
        Session = "session", TurnId = "turn", UserText = "Use port 5234.",
        Commit = new string('a', 40), Source = "settings.cs",
    };
}
