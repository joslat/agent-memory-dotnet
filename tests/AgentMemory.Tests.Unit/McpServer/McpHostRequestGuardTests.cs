using System.Net;
using AgentMemory.McpHost;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>
/// RVW-MCP-ORIGIN: the plain HTTP host answers only requests addressed to it. It has no authentication and is meant
/// for localhost, so without a Host and Origin check a web page could reach it through DNS rebinding: the page's own
/// domain resolves to 127.0.0.1 and the browser sends the page's requests, with the page's Host and Origin, to the
/// server. The MCP specification asks HTTP servers to validate Origin for this reason.
/// </summary>
public sealed class McpHostRequestGuardTests
{
    private static McpHostOptions Options(params string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AZURE_OPENAI_ENDPOINT"] = "https://example.openai.azure.com/",
            ["AZURE_OPENAI_API_KEY"] = "key",
            ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-4o-mini",
            ["NEO4J_PASSWORD"] = "password",
        };
        return McpHostOptions.Parse(["--transport", "http", "--url", "http://127.0.0.1:0", .. args],
            name => values.TryGetValue(name, out var value) ? value : null);
    }

    /// <summary>The host's own HTTP pipeline, on a free loopback port: a POST to the MCP endpoint with the given headers.</summary>
    private static async Task<HttpStatusCode> PostAsync(McpHostOptions options, string host, string? origin = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddMcpServer().WithHttpTransport();
        await using var app = builder.Build();
        McpHostProgram.MapHttp(app, options);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, address + "/")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Host = host;
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (origin is not null) request.Headers.Add("Origin", origin);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task A_request_addressed_to_another_host_is_refused()
    {
        (await PostAsync(Options(), "attacker.example:5233")).Should().Be(HttpStatusCode.Forbidden,
            "a rebound domain is not this server's name");
    }

    [Fact]
    public async Task A_request_from_another_origin_is_refused_even_with_a_local_host()
    {
        (await PostAsync(Options(), "localhost:5233", origin: "http://attacker.example:5233")).Should().Be(HttpStatusCode.Forbidden);
        (await PostAsync(Options(), "localhost:5233", origin: "null")).Should().Be(HttpStatusCode.Forbidden,
            "an opaque origin (a sandboxed frame, a file) is no local page either");
    }

    [Theory]
    [InlineData("localhost:5233", null)]
    [InlineData("127.0.0.1:5233", null)]
    [InlineData("[::1]:5233", null)]
    [InlineData("localhost:8080", "http://localhost:6274")]
    public async Task A_local_request_reaches_the_endpoint(string host, string? origin)
    {
        (await PostAsync(Options(), host, origin)).Should().NotBe(HttpStatusCode.Forbidden,
            "a local client (any port: Docker may map one to another) and a local page are this server's");
    }

    [Fact]
    public async Task An_operator_can_allow_more_host_names()
    {
        var options = Options("--allowed-hosts", "memory.internal, Proxy.Example");
        (await PostAsync(options, "memory.internal")).Should().NotBe(HttpStatusCode.Forbidden);
        (await PostAsync(options, "localhost:5233", origin: "https://proxy.example")).Should().NotBe(HttpStatusCode.Forbidden);
        (await PostAsync(options, "attacker.example")).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void The_allowed_hosts_come_from_the_flag_or_the_environment()
    {
        Options().AllowedHosts.Should().BeEmpty();
        Options("--allowed-hosts", "a.example,b.example").AllowedHosts.Should().Equal("a.example", "b.example");
        var fromEnvironment = McpHostOptions.Parse(["--transport", "http"], name => name switch
        {
            "AZURE_OPENAI_ENDPOINT" => "https://example.openai.azure.com/",
            "AZURE_OPENAI_API_KEY" => "key",
            "AZURE_OPENAI_DEPLOYMENT" => "gpt-4o-mini",
            "NEO4J_PASSWORD" => "password",
            "AGENT_MEMORY_MCP_ALLOWED_HOSTS" => " c.example ",
            _ => null,
        });
        fromEnvironment.AllowedHosts.Should().Equal("c.example");
    }
}
