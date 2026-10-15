using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

namespace AgentMemory.McpHost.Coding;

internal static class CodingRemoteAuthentication
{
    internal const string Scheme = "CodingOAuth";
    internal const string ReadScope = "memory:read";
    internal const string WriteScope = "memory:write";

    public static void AddServices(IServiceCollection services, CodingServiceOptions options)
    {
        var remote = options.Remote!;
        services.AddAuthentication(Scheme).AddJwtBearer(Scheme, jwt =>
        {
            jwt.Authority = remote.Issuer;
            jwt.RequireHttpsMetadata = true;
            jwt.MapInboundClaims = false;
            jwt.IncludeErrorDetails = false;
            jwt.SaveToken = false;
            jwt.BackchannelTimeout = TimeSpan.FromSeconds(10);
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                RequireSignedTokens = true, RequireExpirationTime = true,
                ValidateIssuerSigningKey = true, ValidateIssuer = true, ValidateAudience = true,
                ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
                ValidIssuer = remote.Issuer,
                IssuerValidator = (issuer, _, _) => issuer == remote.Issuer ? issuer : throw new SecurityTokenInvalidIssuerException(),
                ValidAudiences = options.Projects.Keys.Select(project => CodingRemoteOptions.Resource(options, project)),
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"],
            };
            jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var project = context.HttpContext.Request.RouteValues["project"] as string;
                    var subjects = context.Principal!.FindAll("sub").Select(c => c.Value).ToArray();
                    var audiences = context.Principal.FindAll("aud").Select(c => c.Value).ToArray();
                    if (subjects.Length != 1 || subjects[0] != remote.Subject || project is null ||
                        audiences.Length != 1 || audiences[0] != CodingRemoteOptions.Resource(options, project))
                        context.Fail("Token is not authorized for this owner and project.");
                    return Task.CompletedTask;
                },
            };
        });
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Partition only on the finite configured project set, never an attacker-supplied IP or token.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var project = context.Request.RouteValues["project"] as string;
                var key = project is not null && options.Projects.ContainsKey(project) ? project : "";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });
    }

    public static void Map(WebApplication app, CodingServiceOptions options)
    {
        app.UseMiddleware<CodingRemoteBoundary>();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<CodingRemoteAuthorization>();
        app.MapGet("/.well-known/oauth-protected-resource/projects/{project}/mcp", (string project) => Results.Json(new
        {
            resource = CodingRemoteOptions.Resource(options, project),
            authorization_servers = new[] { options.Remote!.Issuer },
            scopes_supported = options.Remote!.AllowWrites ? new[] { ReadScope, WriteScope } : new[] { ReadScope },
            bearer_methods_supported = new[] { "header" },
        })).WithMetadata(CodingRemoteMetadata.Instance);
    }
}

internal sealed class CodingRemoteMetadata
{
    private CodingRemoteMetadata() { }
    public static CodingRemoteMetadata Instance { get; } = new();
}
