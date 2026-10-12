using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.McpHost.Coding;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingTransportTests
{
    [Theory]
    [InlineData("alpha", false, null, 401)]
    [InlineData("beta", true, null, 401)]
    [InlineData("alpha", true, "https://attacker.example", 403)]
    public async Task Unauthorized_HTTP_and_MCP_never_reach_memory(string project, bool authenticate, string? origin, int status)
    {
        await using var fixture = await Fixture.Start();
        foreach (var path in new[] { "profile", "mcp" })
        {
            using var request = fixture.Request($"/projects/{project}/{path}", new { }, authenticate);
            if (origin is not null) request.Headers.Add("Origin", origin);
            using var response = await fixture.Client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(status);
        }
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_body_is_rejected_before_dispatch(bool chunked)
    {
        await using var fixture = await Fixture.Start();
        using var request = fixture.Request("/projects/alpha/profile", new { extra = new string('x', 70000) }, true);
        request.Content = new StringContent(JsonSerializer.Serialize(new { extra = new string('x', 70000) }),
            System.Text.Encoding.UTF8, "application/json");
        request.Headers.TransferEncodingChunked = chunked;
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task HTTP_uses_strict_arguments_and_the_bound_owner()
    {
        await using var fixture = await Fixture.Start();
        using var valid = fixture.Request("/projects/alpha/profile", new { }, true);
        using var response = await fixture.Client.SendAsync(valid);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.Profile.Received(1).GetAsync("owner", Arg.Any<CancellationToken>());
        using var invalid = fixture.Request("/projects/alpha/profile", new { ownerId = "other" }, true);
        using var rejected = await fixture.Client.SendAsync(invalid);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Profile.ReceivedCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task MCP_exposes_only_coding_tools_and_uses_the_same_strict_dispatcher()
    {
        await using var fixture = await Fixture.Start();
        var initialized = await fixture.Rpc("initialize", new
        {
            protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "coding-test", version = "1" },
        });
        initialized.GetProperty("result").GetProperty("protocolVersion").GetString().Should().Be("2025-06-18");
        var tools = await fixture.Rpc("tools/list", new { });
        tools.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().BeEquivalentTo("recall", "remember", "forget", "history", "profile", "write_status");
        var called = await fixture.Rpc("tools/call", new { name = "profile", arguments = new { } });
        called.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse();
        await fixture.Profile.Received(1).GetAsync("owner", Arg.Any<CancellationToken>());
        var rejected = await fixture.Rpc("tools/call", new { name = "profile", arguments = new { ownerId = "other" } });
        rejected.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        fixture.Profile.ReceivedCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task Valid_project_credentials_select_different_stores_over_the_transport()
    {
        await using var fixture = await Fixture.Start();
        fixture.Profile.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => new WorkingMemoryBlock
        {
            OwnerId = "owner", Text = fixture.Store.ApplicationId!, BuiltAtUtc = DateTimeOffset.UnixEpoch, ContentHash = "hash",
        });
        foreach (var (project, token) in new[] { ("alpha", new string('a', 40)), ("beta", new string('b', 40)) })
        {
            using var request = fixture.Request($"/projects/{project}/profile", new { }, true);
            request.Headers.Authorization = new("Bearer", token);
            using var response = await fixture.Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("profile").GetProperty("text").GetString().Should().Be(project);
            var called = await fixture.Rpc("tools/call", new { name = "profile", arguments = new { } }, project, token);
            var text = called.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            JsonSerializer.Deserialize<JsonElement>(text).GetProperty("profile").GetProperty("text").GetString().Should().Be(project);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public IWorkingMemoryService Profile { get; } = Substitute.For<IWorkingMemoryService>();
        public IWritableMemoryStoreContext Store { get; } = new DefaultMemoryStoreContext();
        public HttpClient Client { get; } = new();
        private WebApplication _app = null!;
        private static readonly string Token = new('a', 40);

        public static async Task<Fixture> Start()
        {
            var fixture = new Fixture();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var services = builder.Services;
            var options = new CodingServiceOptions { OwnerId = "owner", Projects = new Dictionary<string, string> { ["alpha"] = "ALPHA_KEY", ["beta"] = "BETA_KEY" } };
            services.AddSingleton(options).AddSingleton(new CodingProjectAccess(options, name => name == "ALPHA_KEY" ? Token : new string('b', 40)));
            services.AddSingleton(fixture.Profile);
            services.AddSingleton<IWritableMemoryOwnerContext, DefaultMemoryOwnerContext>();
            services.AddSingleton(fixture.Store);
            services.AddSingleton(Options.Create(new MemoryStoreOptions
            {
                Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = false,
            }));
            services.AddSingleton(Options.Create(new MemoryOptions()));
            services.AddSingleton(new CodingContentPolicy([Token]));
            services.AddSingleton<CodingOperationScope>().AddSingleton<CodingMemoryOperations>().AddSingleton<CodingApi>();
            services.AddSingleton<ICodingWriter, CodingWriter>().AddSingleton<CodingWriteQueue>();
            CodingTransport.AddServices(services);
            fixture._app = builder.Build();
            CodingTransport.Map(fixture._app);
            await fixture._app.StartAsync();
            fixture.Client.BaseAddress = new Uri(fixture._app.Urls.Single());
            // Dynamic test listener; the checked Host is the configured coding URL's authority.
            fixture.Client.DefaultRequestHeaders.Host = "127.0.0.1:5234";
            return fixture;
        }

        public HttpRequestMessage Request(string path, object body, bool authenticate)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            if (authenticate) request.Headers.Authorization = new("Bearer", Token);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
            return request;
        }

        public async Task<JsonElement> Rpc(string method, object parameters, string project = "alpha", string? token = null)
        {
            using var request = Request($"/projects/{project}/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters }, true);
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync();
            var data = text.Split('\n').FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
            return JsonSerializer.Deserialize<JsonElement>(data is null ? text : data[5..].Trim());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
