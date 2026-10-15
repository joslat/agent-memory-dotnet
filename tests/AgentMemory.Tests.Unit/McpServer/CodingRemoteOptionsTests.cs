using AgentMemory.McpHost.Coding;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingRemoteOptionsTests
{
    private static CodingServiceOptions Options() => new()
    {
        OwnerId = "owner", Url = "https://memory.example", Projects = new Dictionary<string, string> { ["alpha"] = "" },
        Remote = new CodingRemoteOptions { Issuer = "https://identity.example", Subject = "owner-subject",
            ListenAddress = "127.0.0.1", CertificatePath = Path.GetFullPath("synthetic.pfx"), CertificatePasswordVariable = "TLS_PASSWORD" },
    };

    [Theory]
    [InlineData("http")]
    [InlineData("url-path")]
    [InlineData("url-query")]
    [InlineData("issuer-http")]
    [InlineData("issuer-credentials")]
    [InlineData("subject")]
    [InlineData("listen-hostname")]
    [InlineData("relative-certificate")]
    [InlineData("password-variable")]
    [InlineData("browser-ui")]
    [InlineData("static-project-token")]
    public void Unsafe_or_ambiguous_remote_configuration_is_rejected(string variant)
    {
        var options = Options();
        var remote = options.Remote!;
        options = variant switch
        {
            "http" => options with { Url = "http://memory.example" },
            "url-path" => options with { Url = "https://memory.example/path" },
            "url-query" => options with { Url = "https://memory.example?token=secret" },
            "issuer-http" => options with { Remote = remote with { Issuer = "http://identity.example" } },
            "issuer-credentials" => options with { Remote = remote with { Issuer = "https://secret@identity.example" } },
            "subject" => options with { Remote = remote with { Subject = "" } },
            "listen-hostname" => options with { Remote = remote with { ListenAddress = "memory.example" } },
            "relative-certificate" => options with { Remote = remote with { CertificatePath = "certificate.pfx" } },
            "password-variable" => options with { Remote = remote with { CertificatePasswordVariable = "" } },
            "browser-ui" => options with { EnableBrowserUi = true },
            _ => options with { Projects = new Dictionary<string, string> { ["alpha"] = "ALPHA_TOKEN" } },
        };
        options.Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Forwarded_headers_cannot_turn_plain_HTTP_into_trusted_TLS()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new("memory.example");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "memory.example";
        var calls = 0;
        var middleware = new CodingRemoteBoundary(_ => { calls++; return Task.CompletedTask; }, Options());
        await middleware.InvokeAsync(context);
        context.Response.StatusCode.Should().Be(403);
        calls.Should().Be(0);
    }
}
