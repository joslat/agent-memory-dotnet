using System.Text.Json;
using AgentMemory.McpHost.Coding;
using AgentMemory.McpHost.Coding.Client;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingHookContractTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:5234")]
    [InlineData("http://127.0.0.1:5234/other")]
    public void Rejects_noncanonical_service_destinations(string url)
    {
        var options = Options with { ServiceUrl = url };
        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_cache_in_the_repository_and_relative_roots()
    {
        (Options with { StateDirectory = Path.Combine(Options.RepositoryRoot, "state") })
            .Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
        (Options with { RepositoryRoot = "relative" }).Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Context_keeps_sourced_memories_and_excludes_verbatim_message_windows()
    {
        var response = JsonSerializer.SerializeToElement(new { context = new
        {
            relevantFacts = new[] { new { factId = "f1", subject = "Project", predicate = "uses", @object = "Neo4j", sourceMessageIds = new[] { "source1" } } },
            recentMessages = new[] { new { content = "verbatim-canary" } }, relevantMessages = new[] { new { content = "retrieved-message-canary" } },
        }});
        var text = CodingHookContext.Render(response, false, new([]));
        text.Should().Contain("Neo4j").And.Contain("source1").And.Contain("untrusted");
        text.Should().NotContain("verbatim-canary").And.NotContain("retrieved-message-canary");
    }

    [Fact]
    public void Does_not_split_or_inject_an_oversized_memory()
    {
        var response = JsonSerializer.SerializeToElement(new { context = new
        {
            relevantFacts = new[] { new { factId = "f1", @object = new string('x', 7000) } },
        }});
        CodingHookContext.Render(response, false, new([])).Should().BeEmpty();
    }

    [Fact]
    public void Checks_decoded_response_strings_before_injection()
    {
        var secret = "credential+with<encoding>";
        var response = JsonSerializer.SerializeToElement(new { profile = new { text = secret } });
        var render = () => CodingHookContext.Render(response, true, new([secret]));
        render.Should().Throw<ArgumentException>();
    }

    private static CodingHookOptions Options => new()
    {
        Project = "alpha", TokenVariable = "ALPHA_KEY", RepositoryRoot = Path.GetFullPath("repo"),
        StateDirectory = Path.Combine(Path.GetTempPath(), "coding-hook-state"),
    };
}
