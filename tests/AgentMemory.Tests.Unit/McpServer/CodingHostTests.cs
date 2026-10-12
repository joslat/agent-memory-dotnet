using System.Net;
using System.Net.Sockets;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpHost.Coding;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using AgentMemory.Gate;

#pragma warning disable AMGATE001

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Extractor_fallback_requires_explicit_host_configuration(bool allow)
    {
        var env = CodingConfigurationTests.EnvironmentValues();
        var config = CodingConfiguration.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            ownerId = "owner", projects = new { alpha = "ALPHA_TOKEN" }, allowExtractorFallback = allow,
        }), env.GetValueOrDefault);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        CodingHost.Configure(builder, config);
        await using var app = builder.Build();
        app.Services.GetRequiredService<IOptions<ExtractionOptions>>().Value.FallBackToExtractorsWhenWriterFails.Should().Be(allow);
    }

    [Fact]
    public async Task Remote_host_uses_configured_TLS_credentials_and_ignores_ambient_HTTP_listener()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(Path.GetTempPath(), "coding-tls-" + Guid.NewGuid().ToString("N") + ".pfx");
        const string password = "synthetic-test-certificate-password";
        await File.WriteAllBytesAsync(path, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password));
        try
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var env = CodingConfigurationTests.EnvironmentValues();
            env["TEST_CODING_TLS_PASSWORD"] = password;
            var config = CodingConfiguration.Parse(System.Text.Json.JsonSerializer.Serialize(new
            {
                ownerId = "owner", projects = new { alpha = "" }, url = $"https://127.0.0.1:{port}",
                remote = new { issuer = "https://identity.example", subject = "owner-subject", listenAddress = "127.0.0.1",
                    certificatePath = path, certificatePasswordVariable = "TEST_CODING_TLS_PASSWORD" },
            }), env.GetValueOrDefault);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.Configuration["Kestrel:Endpoints:Ambient:Url"] = "http://0.0.0.0:0";
            CodingHost.Configure(builder, config);
            builder.Services.RemoveAll<ICodingWriter>();
            builder.Services.AddSingleton<ICodingWriter, NoCallsWriter>();
            await using var app = builder.Build();
            CodingTransport.Map(app);
            await app.StartAsync();
            try
            {
                app.Urls.Should().Equal(config.Options.Url);
                using var client = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, supplied, _, _) => supplied?.Thumbprint == certificate.Thumbprint,
                    UseProxy = false,
                });
                using var metadata = await client.GetAsync(config.Options.Url + "/.well-known/oauth-protected-resource/projects/alpha/mcp");
                metadata.StatusCode.Should().Be(HttpStatusCode.OK);
                using var response = await client.PostAsync(config.Options.Url + "/projects/alpha/mcp", System.Net.Http.Json.JsonContent.Create(new { }));
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
            finally { await app.StopAsync(); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Uses_only_the_configured_loopback_listener_despite_ambient_endpoints()
    {
        var (builder, config) = Setup();
        builder.Configuration["urls"] = "http://0.0.0.0:0";
        builder.Configuration["Kestrel:Endpoints:Ambient:Url"] = "http://0.0.0.0:0";
        CodingHost.Configure(builder, config);
        builder.Services.RemoveAll<ICodingWriter>();
        builder.Services.AddSingleton<ICodingWriter, NoCallsWriter>();
        await using var app = builder.Build();
        CodingTransport.Map(app);
        await app.StartAsync();
        try
        {
            app.Urls.Should().Equal(config.Options.Url);
            using var client = new HttpClient();
            using var response = await client.PostAsync(config.Options.Url + "/projects/alpha/profile", new StringContent("{}"));
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var worker = app.Services.GetRequiredService<CodingWriteQueue>();
            app.Services.GetServices<IHostedService>().OfType<CodingWriteQueue>().Should().ContainSingle().Which.Should().BeSameAs(worker);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task Prepares_every_project_under_its_bound_scope_before_serving()
    {
        var (builder, config) = Setup(twoProjects: true);
        CodingHost.Configure(builder, config);
        var seen = new List<string>();
        builder.Services.RemoveAll<ISchemaBootstrapper>();
        builder.Services.AddScoped(sp =>
        {
            var bootstrap = Substitute.For<ISchemaBootstrapper>();
            bootstrap.BootstrapAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                sp.GetRequiredService<IWritableMemoryOwnerContext>().UserId.Should().Be("owner");
                seen.Add(sp.GetRequiredService<IWritableMemoryStoreContext>().ApplicationId!);
                return Task.CompletedTask;
            });
            return bootstrap;
        });
        await using var app = builder.Build();
        await CodingHost.PrepareAsync(app, config);
        seen.Should().Equal("alpha", "beta");
        app.Services.GetRequiredService<IOptions<ExtractionOptions>>().Value.FallBackToExtractorsWhenWriterFails.Should().BeFalse();
        app.Services.GetRequiredService<IOptions<MemoryStoreOptions>>().Value.AutoProvision.Should().BeFalse();
        app.Services.GetRequiredService<IWritableMemoryStoreContext>().ApplicationId.Should().BeNull();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetServices<IMemoryWriter>().Should().ContainSingle(w => w.IsEnabled);
    }

    [Fact]
    public async Task A_schema_failure_prevents_readiness()
    {
        var (builder, config) = Setup();
        CodingHost.Configure(builder, config);
        var bootstrap = Substitute.For<ISchemaBootstrapper>();
        bootstrap.BootstrapAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("unavailable")));
        builder.Services.RemoveAll<ISchemaBootstrapper>();
        builder.Services.AddSingleton(bootstrap);
        await using var app = builder.Build();
        var prepare = () => CodingHost.PrepareAsync(app, config);
        await prepare.Should().ThrowAsync<InvalidOperationException>();
    }

    private static (WebApplicationBuilder Builder, CodingConfiguration Config) Setup(bool twoProjects = false)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var env = CodingConfigurationTests.EnvironmentValues();
        env["BETA_TOKEN"] = new string('y', 40);
        var projects = twoProjects ? "\"alpha\":\"ALPHA_TOKEN\",\"beta\":\"BETA_TOKEN\"" : "\"alpha\":\"ALPHA_TOKEN\"";
        var json = $$"""{"ownerId":"owner","projects":{ {{projects}} },"url":"http://127.0.0.1:{{port}}"}""";
        return (WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" }),
            CodingConfiguration.Parse(json, env.GetValueOrDefault));
    }

    private sealed class NoCallsWriter : ICodingWriter
    {
        public Task<CodingWriteResult> WriteAsync(CodingIdentity identity, CodingRememberRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No writer call expected.");
    }

    [Fact]
    public async Task Explicit_judges_register_the_gate_with_compound_retrieval_off()
    {
        var env = CodingConfigurationTests.EnvironmentValues();
        var config = CodingConfiguration.Parse("""{"ownerId":"owner","projects":{"alpha":"ALPHA_TOKEN"},"judges":[{"name":"laya","endpoint":"http://127.0.0.1:8765/v1/systemone","model":"laya","weight":0.2}]}""", env.GetValueOrDefault);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        CodingHost.Configure(builder, config);
        await using var app = builder.Build();
        var gate = app.Services.GetRequiredService<IOptions<MemoryGateOptions>>().Value;
        gate.Judges.Should().ContainSingle().Which.Weight.Should().Be(0.2);
        gate.Mode.Should().Be(MemoryGateMode.Judge);
        gate.CompoundQuestions.Should().BeFalse();
        app.Services.GetRequiredService<IMemoryGate>().Should().NotBeNull();
    }

    [Fact]
    public async Task Entry_point_rejects_missing_or_mixed_coding_arguments_without_starting()
    {
        (await AgentMemory.McpHost.McpHostProgram.RunAsync(["--coding-config"])).Should().Be(2);
        (await AgentMemory.McpHost.McpHostProgram.RunAsync(["--coding-config", "unused.json", "--read-only"])).Should().Be(2);
    }
}
