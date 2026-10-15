using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.McpHost.Coding;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingRemoteTransportTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Authenticated_stateless_MCP_methods_preserve_remote_authorization(string method)
    {
        await using var fixture = await Fixture.Start();
        using var request = fixture.Request(fixture.Token());
        request.Method = new HttpMethod(method);
        request.Content = null;
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().Equal("POST");
        response.Headers.WwwAuthenticate.Should().BeEmpty();
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET", null, null, 401)]
    [InlineData("DELETE", null, null, 401)]
    [InlineData("GET", "other-project", null, 401)]
    [InlineData("DELETE", "other-project", null, 401)]
    [InlineData("GET", "read", "https://attacker.example", 403)]
    [InlineData("DELETE", "read", "https://attacker.example", 403)]
    public async Task Unsupported_MCP_methods_still_enforce_remote_credentials_and_origin(
        string method, string? variant, string? origin, int status)
    {
        await using var fixture = await Fixture.Start();
        using var request = fixture.Request(variant is null ? null : fixture.Token(variant));
        request.Method = new HttpMethod(method);
        request.Content = null;
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await fixture.Client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(status);
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Network_discovery_fetches_public_keys_caches_them_and_refreshes_for_rotation()
    {
        await using var fixture = await Fixture.Start(networkDiscovery: "online");
        using var first = await fixture.Send(fixture.Token());
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Issuer!.MetadataRequests.Should().Be(1);
        fixture.Issuer.KeyRequests.Should().Be(1);
        fixture.Issuer.SawAuthorization.Should().BeFalse();
        fixture.Issuer.Unavailable = true;
        using var cached = await fixture.Send(fixture.Token());
        cached.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Issuer.MetadataRequests.Should().Be(1);
        fixture.Issuer.Unavailable = false;
        fixture.RotateKey();
        // Unknown-key validation requests a background refresh. Poll only until that refresh completes.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var rotated = await fixture.Send(fixture.Token("rotated"));
            if (rotated.StatusCode == HttpStatusCode.OK) break;
            rotated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            await Task.Delay(50, deadline.Token);
        }
        fixture.Issuer.MetadataRequests.Should().BeGreaterThan(1);
        fixture.Issuer.KeyRequests.Should().BeGreaterThan(1);
        fixture.Issuer.SawAuthorization.Should().BeFalse();
    }

    [Theory]
    [InlineData("outage")]
    [InlineData("insecure-keys")]
    [InlineData("untrusted-certificate")]
    public async Task Discovery_failure_never_authorizes_or_reaches_memory(string mode)
    {
        await using var fixture = await Fixture.Start(networkDiscovery: mode);
        using var denied = await fixture.Send(fixture.Token());
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
        fixture.Issuer!.KeyRequests.Should().Be(0);
        fixture.Issuer.SawAuthorization.Should().BeFalse();
    }

    [Fact]
    public async Task Remote_writes_require_both_server_opt_in_and_token_scope()
    {
        await using var fixture = await Fixture.Start(allowWrites: false);
        var listed = await fixture.Rpc(fixture.Token("write"), "tools/list", new { });
        listed.GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(4);
        var denied = await fixture.Rpc(fixture.Token("write"), "tools/call", new { name = "forget", arguments = new { } });
        denied.ToString().Should().Contain("insufficient_scope");
    }

    [Fact]
    public async Task Consented_write_returns_a_queue_receipt_readable_with_read_scope()
    {
        await using var fixture = await Fixture.Start();
        var accepted = await fixture.Rpc(fixture.Token("write"), "tools/call", new
        {
            name = "remember", arguments = new { session = "synthetic", turnId = "one", userText = "Use port 5234.",
                commit = new string('a', 40), source = "remote-test" },
        });
        var result = accepted.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var receipt = JsonSerializer.Deserialize<JsonElement>(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        var status = await fixture.Rpc(fixture.Token(), "tools/call", new
        {
            name = "write_status", arguments = new { jobId = receipt.GetProperty("jobId").GetString() },
        });
        status.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse();
        status.ToString().Should().Contain("queued");
    }

    [Fact]
    public async Task Discovery_and_challenge_identify_the_exact_project_resource()
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Client.GetAsync("/.well-known/oauth-protected-resource/projects/alpha/mcp");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadata = await response.Content.ReadFromJsonAsync<JsonElement>();
        metadata.GetProperty("resource").GetString().Should().Be("https://memory.example/projects/alpha/mcp");
        metadata.GetProperty("authorization_servers")[0].GetString().Should().Be("https://identity.example");
        using var denied = await fixture.Send(null);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        denied.Headers.WwwAuthenticate.ToString().Should().Contain("https://memory.example/.well-known/oauth-protected-resource/projects/alpha/mcp");
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-subject")]
    [InlineData("other-project")]
    [InlineData("multiple-audiences")]
    [InlineData("trailing-slash")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("wrong-key")]
    [InlineData("id-token")]
    [InlineData("unsigned")]
    [InlineData("static-token")]
    [InlineData("wrong-algorithm")]
    public async Task Invalid_tokens_never_reach_memory(string variant)
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Send(fixture.Token(variant));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("owner-subject");
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing-scope")]
    [InlineData("write-only")]
    public async Task A_valid_signature_does_not_grant_read_permission(string variant)
    {
        await using var fixture = await Fixture.Start();
        using var response = await fixture.Send(fixture.Token(variant));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("insufficient_scope");
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Scoped_MCP_reads_bind_owner_and_hide_and_reject_write_tools()
    {
        await using var fixture = await Fixture.Start();
        var token = fixture.Token();
        var listed = await fixture.Rpc(token, "tools/list", new { });
        listed.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().BeEquivalentTo("recall", "profile", "history", "write_status");
        var read = await fixture.Rpc(token, "tools/call", new { name = "profile", arguments = new { } });
        read.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse();
        await fixture.Profile.Received(1).GetAsync("owner", Arg.Any<CancellationToken>());
        var denied = await fixture.Rpc(token, "tools/call", new { name = "remember", arguments = new { } });
        denied.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        denied.ToString().Should().Contain("insufficient_scope");
        var writable = await fixture.Rpc(fixture.Token("write"), "tools/list", new { });
        writable.GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(6);
    }

    [Theory]
    [InlineData("/projects/alpha/profile")]
    [InlineData("/projects/alpha/ui")]
    [InlineData("/projects/bogus/mcp")]
    [InlineData("/.well-known/oauth-protected-resource/projects/bogus/mcp")]
    public async Task Remote_mode_maps_only_known_project_MCP_and_metadata(string path)
    {
        await using var fixture = await Fixture.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", fixture.Token());
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Origin", "https://memory.example", 403)]
    [InlineData("Host", "attacker.example", 403)]
    [InlineData("Authorization", "oversized", 400)]
    public async Task Transport_rejects_untrusted_headers_before_memory(string header, string value, int expected)
    {
        await using var fixture = await Fixture.Start();
        using var request = fixture.Request(fixture.Token());
        request.Headers.Remove(header);
        if (header == "Host") request.Headers.Host = value;
        else request.Headers.TryAddWithoutValidation(header, value == "oversized" ? new string('x', 8200) : value);
        using var response = await fixture.Client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(expected);
        fixture.Profile.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Rate_limit_bounds_requests_without_attacker_created_partitions()
    {
        await using var fixture = await Fixture.Start();
        for (var i = 0; i < 120; i++)
        {
            using var response = await fixture.Client.GetAsync("/.well-known/oauth-protected-resource/projects/alpha/mcp");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var limited = await fixture.Client.GetAsync("/.well-known/oauth-protected-resource/projects/alpha/mcp");
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        using var other = await fixture.Client.GetAsync("/.well-known/oauth-protected-resource/projects/beta/mcp");
        other.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RSA _other = RSA.Create(2048);
        private readonly X509Certificate2 _certificate;
        private readonly RsaSecurityKey _key;
        private WebApplication _app = null!;
        private HttpClient? _backchannel;
        public TestIssuer? Issuer { get; private set; }
        public HttpClient Client { get; }
        public IWorkingMemoryService Profile { get; } = Substitute.For<IWorkingMemoryService>();

        private Fixture()
        {
            _key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
            var certificate = new CertificateRequest("CN=localhost", _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            // Schannel requires an imported key rather than the ephemeral CreateSelfSigned key.
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            Client = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, supplied, _, _) => supplied?.Thumbprint == _certificate.Thumbprint,
                AllowAutoRedirect = false, UseProxy = false,
            });
            Client.DefaultRequestHeaders.Host = "memory.example";
        }

        public static async Task<Fixture> Start(bool allowWrites = true, string? networkDiscovery = null)
        {
            var fixture = new Fixture();
            if (networkDiscovery is not null)
                fixture.Issuer = await TestIssuer.Start(fixture._certificate, fixture._key, networkDiscovery);
            var options = new CodingServiceOptions
            {
                OwnerId = "owner", Url = "https://memory.example", Projects = new Dictionary<string, string> { ["alpha"] = "", ["beta"] = "" },
                Remote = new CodingRemoteOptions { Issuer = fixture.Issuer?.Url ?? "https://identity.example", Subject = "owner-subject",
                    ListenAddress = "127.0.0.1", CertificatePath = Path.GetFullPath("unused-test.pfx"), CertificatePasswordVariable = "TEST_TLS_PASSWORD",
                    AllowWrites = allowWrites },
            };
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(fixture._certificate)));
            var services = builder.Services;
            services.AddSingleton(options).AddSingleton(fixture.Profile);
            services.AddSingleton<IWritableMemoryOwnerContext, DefaultMemoryOwnerContext>();
            services.AddSingleton<IWritableMemoryStoreContext, DefaultMemoryStoreContext>();
            services.AddSingleton(Options.Create(new MemoryStoreOptions
            {
                Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = false,
            }));
            services.AddSingleton(Options.Create(new MemoryOptions()));
            services.AddSingleton(new CodingContentPolicy([]));
            services.AddSingleton<CodingOperationScope>().AddSingleton<CodingMemoryOperations>().AddSingleton<CodingApi>();
            services.AddSingleton<ICodingWriter, CodingWriter>().AddSingleton<CodingWriteQueue>();
            CodingTransport.AddServices(services);
            CodingRemoteAuthentication.AddServices(services, options);
            services.Configure<JwtBearerOptions>(CodingRemoteAuthentication.Scheme, jwt =>
            {
                if (fixture.Issuer is null)
                {
                    var discovery = new OpenIdConnectConfiguration { Issuer = options.Remote.Issuer };
                    discovery.SigningKeys.Add(fixture._key);
                    jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
                }
                else
                {
                    var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
                    if (networkDiscovery != "untrusted-certificate")
                        handler.ServerCertificateCustomValidationCallback = (_, supplied, _, _) => supplied?.Thumbprint == fixture._certificate.Thumbprint;
                    fixture._backchannel = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                    jwt.Backchannel = fixture._backchannel;
                }
            });
            fixture._app = builder.Build();
            CodingTransport.Map(fixture._app);
            await fixture._app.StartAsync();
            fixture.Client.BaseAddress = new Uri(fixture._app.Urls.Single());
            return fixture;
        }

        public string Token(string variant = "read")
        {
            if (variant == "static-token") return new string('a', 40);
            var now = DateTime.UtcNow;
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = variant == "wrong-issuer" ? "https://other.example" : Issuer?.Url ?? "https://identity.example",
                Audience = "https://memory.example/projects/" + (variant == "other-project" ? "beta" : "alpha") + "/mcp" + (variant == "trailing-slash" ? "/" : ""),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = variant == "wrong-subject" ? "other-subject" : "owner-subject",
                    ["scope"] = variant switch { "write" => "memory:read memory:write", "write-only" => "memory:write", "missing-scope" => "", _ => "memory:read" },
                },
                TokenType = variant == "id-token" ? "JWT" : "at+jwt",
                IssuedAt = now.AddHours(-1), NotBefore = variant == "future" ? now.AddMinutes(3) : now.AddHours(-1),
                Expires = variant == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5),
                SigningCredentials = variant == "unsigned" ? null : new SigningCredentials(
                    variant is "wrong-key" or "rotated" ? new RsaSecurityKey(_other) { KeyId = variant == "rotated" ? "rotated-key" : "test-key" } : _key,
                    variant == "wrong-algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256),
            };
            if (variant == "multiple-audiences") descriptor.Audiences.Add("https://memory.example/projects/beta/mcp");
            return new JsonWebTokenHandler().CreateToken(descriptor);
        }

        public HttpRequestMessage Request(string? token, string method = "tools/call", object? parameters = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/projects/alpha/mcp")
            {
                Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method,
                    @params = parameters ?? new { name = "profile", arguments = new { } } }),
            };
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
            return request;
        }

        public async Task<HttpResponseMessage> Send(string? token)
        {
            using var request = Request(token);
            return await Client.SendAsync(request);
        }

        public async Task<JsonElement> Rpc(string token, string method, object parameters)
        {
            using var request = Request(token, method, parameters);
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
            if (Issuer is not null) await Issuer.DisposeAsync();
            _backchannel?.Dispose();
            _certificate.Dispose(); _rsa.Dispose(); _other.Dispose();
        }

        public void RotateKey() => Issuer!.Key = new RsaSecurityKey(_other) { KeyId = "rotated-key" };
    }

    private sealed class TestIssuer : IAsyncDisposable
    {
        private WebApplication _app = null!;
        public string Url => _app.Urls.Single();
        public volatile bool Unavailable;
        public volatile RsaSecurityKey Key = null!;
        public int MetadataRequests;
        public int KeyRequests;
        public bool SawAuthorization;

        public static async Task<TestIssuer> Start(X509Certificate2 certificate, RsaSecurityKey key, string mode)
        {
            var issuer = new TestIssuer { Key = key, Unavailable = mode == "outage" };
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            issuer._app = builder.Build();
            issuer._app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("Authorization")) issuer.SawAuthorization = true;
                await next(context);
            });
            issuer._app.MapGet("/.well-known/openid-configuration", () =>
            {
                Interlocked.Increment(ref issuer.MetadataRequests);
                if (issuer.Unavailable) return Results.StatusCode(503);
                return Results.Json(new
                {
                    issuer = issuer.Url,
                    jwks_uri = (mode == "insecure-keys" ? issuer.Url.Replace("https://", "http://", StringComparison.Ordinal) : issuer.Url) + "/keys",
                });
            });
            issuer._app.MapGet("/keys", () =>
            {
                Interlocked.Increment(ref issuer.KeyRequests);
                if (issuer.Unavailable) return Results.StatusCode(503);
                var current = issuer.Key;
                var parameters = current.Rsa.ExportParameters(false);
                return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = current.KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) } } });
            });
            await issuer._app.StartAsync();
            return issuer;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
