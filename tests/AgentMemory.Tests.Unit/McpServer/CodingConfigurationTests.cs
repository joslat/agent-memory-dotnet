using AgentMemory.McpHost.Coding;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingConfigurationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    public void Closing_judge_must_name_a_configured_endpoint(string name)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            ownerId = "owner", projects = new { alpha = "ALPHA_TOKEN" }, closingJudgeName = name,
        });
        FluentActions.Invoking(() => CodingConfiguration.Parse(json, EnvironmentValues().GetValueOrDefault))
            .Should().Throw<ArgumentException>();
    }

    internal const string Json = """{"ownerId":"owner","projects":{"alpha":"ALPHA_TOKEN"}}""";

    [Fact]
    public void Explicit_remote_configuration_needs_no_local_project_token()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            ownerId = "owner", projects = new { alpha = "" }, url = "https://memory.example",
            remote = new { issuer = "https://identity.example", subject = "owner-subject",
                listenAddress = "127.0.0.1", certificatePath = Path.GetFullPath("test-only.pfx"),
                certificatePasswordVariable = "TLS_PASSWORD" },
        });
        var env = EnvironmentValues();
        env.Remove("ALPHA_TOKEN");
        env["TLS_PASSWORD"] = "certificate-password";
        var config = CodingConfiguration.Parse(json, env.GetValueOrDefault);
        config.Options.Url.Should().Be("https://memory.example");
        config.Access.Authorize("alpha", "Bearer " + new string('x', 40)).Should().BeNull();
        config.Content.Invoking(c => c.CheckText("value certificate-password")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("AI_INFERENCE_PROVIDER", null)]
    [InlineData("BITDEER_ENDPOINT", "https://api.openai.com/v1")]
    [InlineData("BITDEER_ENDPOINT", "https://api-inference.bitdeer.ai/v1?token=secret")]
    [InlineData("BITDEER_ENDPOINT", "http://api-inference.bitdeer.ai/v1")]
    [InlineData("AI_EMBEDDING_ENDPOINT", "https://other.example/v1")]
    [InlineData("AGENTMEMORY_INFERENCE_SHOW_RAW", "true")]
    [InlineData("AGENT_MEMORY_MCP_READ_ONLY", "true")]
    [InlineData("AGENT_MEMORY_MCP_READ_ONLY", "treu")]
    [InlineData("AGENT_MEMORY_MCP_ENABLE_GRAPH_QUERY", "true")]
    [InlineData("AGENT_MEMORY_MCP_NO_BOOTSTRAP", "true")]
    public void Refuses_implicit_or_incompatible_runtime_configuration(string key, string? value)
    {
        var env = EnvironmentValues();
        env[key] = value;
        if (key == "AI_EMBEDDING_ENDPOINT")
        {
            env["AI_EMBEDDING_PROVIDER"] = "bitdeer";
            env["AI_EMBEDDING_API_KEY"] = "embedding-credential";
        }
        var run = () => CodingConfiguration.Parse(Json, env.GetValueOrDefault);
        run.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("{\"ownerId\":\"owner\",\"ownerId\":\"other\",\"projects\":{\"alpha\":\"ALPHA_TOKEN\"}}")]
    [InlineData("{\"ownerId\":\"owner\",\"projects\":{\"alpha\":\"ALPHA_TOKEN\",\"alpha\":\"ALPHA_TOKEN\"}}")]
    public void Rejects_duplicate_configuration_fields(string json)
    {
        var run = () => CodingConfiguration.Parse(json, EnvironmentValues().GetValueOrDefault);
        run.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_oversized_configuration_before_deserialization()
    {
        var run = () => CodingConfiguration.Parse(new string(' ', 16385) + Json, EnvironmentValues().GetValueOrDefault);
        run.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Binds_actual_credentials_into_content_checks_without_printing_them()
    {
        var env = EnvironmentValues();
        var config = CodingConfiguration.Parse(Json, env.GetValueOrDefault);
        config.Options.OwnerId.Should().Be("owner");
        foreach (var key in new[] { "ALPHA_TOKEN", "BITDEER_API_KEY", "NEO4J_PASSWORD" })
        {
            var secret = env[key]!;
            config.Content.Invoking(c => c.CheckText("value " + secret)).Should().Throw<ArgumentException>();
            config.ToString().Should().NotContain(secret);
        }
    }

    internal static Dictionary<string, string?> EnvironmentValues() => new()
    {
        ["AI_INFERENCE_PROVIDER"] = "bitdeer", ["BITDEER_API_KEY"] = "chat-credential-value",
        ["NEO4J_PASSWORD"] = "database-credential-value", ["ALPHA_TOKEN"] = new string('x', 40),
    };

    [Theory]
    [InlineData("https://other.example/v1/systemone", "JUDGE_KEY", 1)]
    [InlineData("http://api.typesafe.ai/v1/systemone", "JUDGE_KEY", 1)]
    [InlineData("https://api.typesafe.ai/v1/systemone", null, 1)]
    [InlineData("https://api.typesafe.ai/v1/systemone", "MISSING_KEY", 1)]
    [InlineData("http://127.0.0.1:8765/v1/systemone", null, 0)]
    public void Refuses_invalid_or_unauthenticated_judges(string endpoint, string? keyVariable, double weight)
    {
        var env = EnvironmentValues();
        env["JUDGE_KEY"] = "judge-credential-value";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            ownerId = "owner", projects = new { alpha = "ALPHA_TOKEN" },
            judges = new[] { new { name = "jev", endpoint, model = "jev-latest", keyVariable, weight } },
        });
        var run = () => CodingConfiguration.Parse(json, env.GetValueOrDefault);
        run.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Binds_judge_credentials_into_content_checks()
    {
        var env = EnvironmentValues();
        env["JUDGE_KEY"] = "judge-credential-value";
        var json = """{"ownerId":"owner","projects":{"alpha":"ALPHA_TOKEN"},"judges":[{"name":"jev","endpoint":"https://api.typesafe.ai/v1/systemone","model":"jev-latest","keyVariable":"JUDGE_KEY"}]}""";
        var config = CodingConfiguration.Parse(json, env.GetValueOrDefault);
        config.Content.Invoking(c => c.CheckText("value " + env["JUDGE_KEY"])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Bounded_file_reader_accepts_UTF8_and_rejects_an_oversized_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Json, new System.Text.UTF8Encoding(true));
            (await CodingConfiguration.ReadAsync(path, EnvironmentValues().GetValueOrDefault)).Options.OwnerId.Should().Be("owner");
            await File.WriteAllTextAsync(path, new string(' ', CodingConfiguration.MaximumBytes + 1) + Json);
            var read = () => CodingConfiguration.ReadAsync(path, EnvironmentValues().GetValueOrDefault);
            await read.Should().ThrowAsync<ArgumentException>();
        }
        finally { File.Delete(path); }
    }
}
