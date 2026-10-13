using System.Text.Json;
using AgentMemory.McpHost.Coding;
using AgentMemory.McpHost.Coding.Client;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingHookContractTests
{
    [Theory]
    [InlineData("\"typo\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Invalid_harness_configuration_never_falls_back_to_Claude(string value)
    {
        var json = JsonSerializer.Serialize(Options, CodingApi.Json);
        using var document = JsonDocument.Parse(json);
        var fields = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        fields["harness"] = JsonSerializer.Deserialize<JsonElement>(value);
        var read = () => CodingJson.ParseConfiguration<CodingHookOptions>(JsonSerializer.Serialize(fields));
        read.Should().Throw<ArgumentException>();
    }

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

    [Fact]
    public void Recall_keeps_the_working_memory_emitted_by_the_actual_MCP_projection()
    {
        var response = ProjectRecall("Always use the cobalt badge for Orchid releases.");

        var text = CodingHookContext.Render(response, false, new([]));

        text.Should().Contain("cobalt").And.Contain("untrusted");
        text.Length.Should().BeLessThanOrEqualTo(CodingHookContext.MaximumCharacters);
    }

    [Fact]
    public void Recall_checks_the_decoded_working_memory_for_secrets_before_injection()
    {
        const string secret = "credential+with<encoding>";
        var response = ProjectRecall(secret);

        var render = () => CodingHookContext.Render(response, false, new([secret]));

        render.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Recall_does_not_inject_an_empty_working_memory(string? block)
    {
        CodingHookContext.Render(ProjectRecall(block), false, new([])).Should().BeEmpty();
    }

    [Fact]
    public void Recall_does_not_split_an_oversized_working_memory()
    {
        CodingHookContext.Render(ProjectRecall(new string('x', 7000)), false, new([])).Should().BeEmpty();
    }

    [Fact]
    public void Session_profile_keeps_its_existing_object_wire_shape()
    {
        var response = JsonSerializer.SerializeToElement(new { profile = new { text = "Use the cobalt badge." } });
        CodingHookContext.Render(response, true, new([])).Should().Contain("cobalt").And.Contain("untrusted");
    }

    [Fact]
    public void V1_pending_payload_hash_remains_compatible_with_existing_source_records()
    {
        var request = new CodingRememberRequest
        {
            Session = "session", TurnId = "turn", UserText = "Use Neo4j.",
            Commit = new string('a', 40), Source = "synthetic",
        };
        request.PayloadHash().Should().Be("72fb1d55b4d5f89a7f700f26452d413e69c282a24b9d829524ad87c599ca57b2");
    }

    [Fact]
    public void V1_scoped_source_identity_remains_compatible_with_existing_messages()
    {
        var session = CodingInput.SessionId(new("owner", "alpha"), "session");
        session.Should().Be("coding-69e62735ec8e4fcdcab97d79023e11ad7ece953148b69d3d0eddbac16250375e");
        CodingInput.MessageId(session, "turn", "user").Should().Be("coding-b729016f50bca9a7ef45b7a4b23c2b40a2eae575693db63ae20abf8843df0d94");
    }

    private static JsonElement ProjectRecall(string? block)
    {
        var type = typeof(AgentMemory.McpServer.ServiceCollectionExtensions).Assembly
            .GetType("AgentMemory.McpServer.Tools.McpMemoryProjection")!;
        var projection = type.GetMethod("Context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [new AgentMemory.Abstractions.Domain.MemoryContext
            {
                SessionId = "new-session", AssembledAtUtc = DateTimeOffset.UnixEpoch, WorkingMemoryBlock = block,
            }])!;
        return JsonSerializer.SerializeToElement(new { context = projection }, CodingApi.Json);
    }

    private static CodingHookOptions Options => new()
    {
        Project = "alpha", TokenVariable = "ALPHA_KEY", RepositoryRoot = Path.GetFullPath("repo"),
        StateDirectory = Path.Combine(Path.GetTempPath(), "coding-hook-state"),
    };
}
