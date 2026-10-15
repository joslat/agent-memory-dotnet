using AgentMemory.McpHost.Coding;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingRequestAuthorizationTests
{
    private static readonly string Token = new('a', 40);
    private static readonly CodingServiceOptions Options = new()
    {
        OwnerId = "owner", Projects = new Dictionary<string, string> { ["alpha"] = "ALPHA_KEY", ["beta"] = "BETA_KEY" },
    };

    [Theory]
    [InlineData("alpha", "", "127.0.0.1:5234", null, 401)]
    [InlineData("beta", "valid", "127.0.0.1:5234", null, 401)]
    [InlineData("unknown", "valid", "127.0.0.1:5234", null, 401)]
    [InlineData("Alpha", "valid", "127.0.0.1:5234", null, 401)]
    [InlineData("alpha", "valid", "attacker.example:5234", null, 403)]
    [InlineData("alpha", "valid", "127.0.0.1:5234", "https://attacker.example", 403)]
    public async Task Rejected_requests_never_reach_a_memory_handler(string project, string credential,
        string host, string? origin, int status)
    {
        var context = Request(project, credential, host, origin);
        var called = false;
        var middleware = Middleware(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);
        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(status);
        context.Features.Get<CodingIdentity>().Should().BeNull();
    }

    [Fact]
    public async Task Authorized_identity_comes_from_config_and_route_and_is_cleared_after_the_handler()
    {
        var context = Request("alpha", "valid", "127.0.0.1:5234", null);
        // Caller-controlled query values cannot override the authenticated identity.
        context.Request.QueryString = new QueryString("?owner=other&project=beta");
        var called = false;
        var middleware = Middleware(ctx =>
        {
            called = true;
            ctx.Features.Get<CodingIdentity>().Should().Be(new CodingIdentity("owner", "alpha"));
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);
        called.Should().BeTrue();
        context.Features.Get<CodingIdentity>().Should().BeNull();
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task Multiple_authorization_headers_are_rejected()
    {
        var context = Request("alpha", "valid", "127.0.0.1:5234", null);
        context.Request.Headers.Append("Authorization", "Bearer " + Token);
        var called = false;
        await Middleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);
        called.Should().BeFalse();
        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Handler_failure_clears_the_identity()
    {
        var context = Request("alpha", "valid", "127.0.0.1:5234", null);
        var middleware = Middleware(_ => throw new InvalidOperationException("expected"));
        var run = () => middleware.InvokeAsync(context);
        await run.Should().ThrowAsync<InvalidOperationException>();
        context.Features.Get<CodingIdentity>().Should().BeNull();
    }

    private static CodingRequestAuthorization Middleware(RequestDelegate next) => new(next,
        new CodingProjectAccess(Options, name => name == "ALPHA_KEY" ? Token : new string('b', 40)), Options);

    private static DefaultHttpContext Request(string project, string credential, string host, string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["project"] = project;
        context.Request.Host = new HostString(host);
        context.Request.Headers.Authorization = credential == "valid" ? "Bearer " + Token : credential;
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context;
    }
}
