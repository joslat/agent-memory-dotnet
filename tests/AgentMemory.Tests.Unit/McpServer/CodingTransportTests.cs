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
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Authenticated_stateless_MCP_methods_do_not_trigger_OAuth(string method)
    {
        await using var fixture = await Fixture.Start();
        using var request = fixture.Request("/projects/alpha/mcp", new { }, true);
        request.Method = new HttpMethod(method);
        request.Content = null;
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().Equal("POST");
        response.Headers.WwwAuthenticate.Should().BeEmpty();
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET", "alpha", false, null, 401)]
    [InlineData("DELETE", "alpha", false, null, 401)]
    [InlineData("GET", "beta", true, null, 401)]
    [InlineData("DELETE", "beta", true, null, 401)]
    [InlineData("GET", "alpha", true, "https://attacker.example", 403)]
    [InlineData("DELETE", "alpha", true, "https://attacker.example", 403)]
    public async Task Unsupported_MCP_methods_still_enforce_local_credentials_and_origin(
        string method, string project, bool authenticate, string? origin, int status)
    {
        await using var fixture = await Fixture.Start();
        using var request = fixture.Request($"/projects/{project}/mcp", new { }, authenticate);
        request.Method = new HttpMethod(method);
        request.Content = null;
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await fixture.Client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(status);
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task HTTP_and_MCP_report_the_same_completed_extraction_path()
    {
        await using var fixture = await Fixture.Start(writer: new FallbackWriter());
        var queued = await fixture.Rpc("tools/call", new { name = "remember", arguments = new
        {
            session = "session", turnId = "one", userText = "Use port 5234.",
            commit = new string('b', 40), source = "settings.cs",
        } });
        var receipt = JsonSerializer.Deserialize<JsonElement>(queued.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        var id = receipt.GetProperty("jobId").GetString();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JsonElement status;
        do
        {
            using var request = fixture.Request("/projects/alpha/write_status", new { jobId = id }, true);
            using var response = await fixture.Client.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
            status = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
            if (status.GetProperty("state").GetString() == "succeeded") break;
            await Task.Delay(10, deadline.Token);
        } while (true);
        status.GetProperty("extractionPath").GetString().Should().Be("writerFallback");
        var rpc = await fixture.Rpc("tools/call", new { name = "write_status", arguments = new { jobId = id } });
        var body = JsonSerializer.Deserialize<JsonElement>(rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        body.GetProperty("extractionPath").GetString().Should().Be("writerFallback");
        body.GetProperty("state").GetString().Should().Be("succeeded");
    }

    private sealed class FallbackWriter : ICodingWriter
    {
        public Task<CodingWriteResult> WriteAsync(CodingIdentity identity, CodingRememberRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new CodingWriteResult
            {
                SourceMessageId = "source", Status = IngestionStatus.Succeeded, ExtractionPath = ExtractionPath.WriterFallback,
            });
    }

    [Fact]
    public async Task Page_canonicalizes_trailing_slash_so_relative_assets_load()
    {
        await using var fixture = await Fixture.Start(true);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = fixture.Client.BaseAddress };
        client.DefaultRequestHeaders.Host = "127.0.0.1:5234";
        using var response = await client.GetAsync("/projects/alpha/ui/");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/projects/alpha/ui");
    }

    [Theory]
    [InlineData(false, "ui", 401)]
    [InlineData(false, "ui/app.js", 401)]
    [InlineData(true, "ui", 200)]
    [InlineData(true, "ui/app.js", 200)]
    [InlineData(true, "ui/style.css", 200)]
    [InlineData(true, "profile", 401)]
    [InlineData(true, "ui/missing.js", 401)]
    public async Task Only_enabled_static_page_assets_are_public(bool enabled, string path, int status)
    {
        await using var fixture = await Fixture.Start(enabled);
        using var response = await fixture.Client.GetAsync($"/projects/alpha/{path}");
        ((int)response.StatusCode).Should().Be(status);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        if (status == 200)
        {
            response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
            response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
            (await response.Content.ReadAsStringAsync()).Should().NotContain(new string('a', 40));
        }
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true, "http://127.0.0.1:5234", "profile", 403)]
    [InlineData(true, true, "http://127.0.0.1:5234", "profile", 200)]
    [InlineData(true, false, "http://127.0.0.1:5234", "profile", 401)]
    [InlineData(true, true, "http://127.0.0.1:5235", "profile", 403)]
    [InlineData(true, true, "https://attacker.example", "profile", 403)]
    [InlineData(true, true, "null", "profile", 403)]
    [InlineData(true, true, "http://127.0.0.1:5234/", "profile", 403)]
    [InlineData(true, true, "http://127.0.0.1:5234", "mcp", 403)]
    public async Task Browser_API_requires_opt_in_exact_origin_and_project_credential(bool enabled, bool auth,
        string origin, string operation, int status)
    {
        await using var fixture = await Fixture.Start(enabled);
        using var request = fixture.Request($"/projects/alpha/{operation}", new { }, auth);
        request.Headers.Add("Origin", origin);
        using var response = await fixture.Client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(status);
        if (status != 200) fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("attacker.example:5234", null)]
    [InlineData("127.0.0.1:5234", "https://attacker.example")]
    public async Task Page_assets_keep_host_and_origin_checks(string host, string? origin)
    {
        await using var fixture = await Fixture.Start(true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/projects/alpha/ui");
        request.Headers.Host = host;
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

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

        public static async Task<Fixture> Start(bool enableBrowserUi = false, ICodingWriter? writer = null)
        {
            var fixture = new Fixture();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var services = builder.Services;
            var options = new CodingServiceOptions { OwnerId = "owner", EnableBrowserUi = enableBrowserUi, Projects = new Dictionary<string, string> { ["alpha"] = "ALPHA_KEY", ["beta"] = "BETA_KEY" } };
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
            if (writer is not null)
            {
                services.AddSingleton(writer);
                services.AddHostedService(sp => sp.GetRequiredService<CodingWriteQueue>());
            }
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
