using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Nams;
using AgentMemory.Nams.Authentication;
using AgentMemory.Nams.Client;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsServiceCollectionExtensionsTests
{
    private static readonly Uri ValidEndpoint = new("https://memory.neo4jlabs.com/v1");

    [Fact]
    public void AddNamsAgentMemory_NullServices_Throws()
    {
        IServiceCollection services = null!;
        var act = () => services.AddNamsAgentMemory(o => o.Endpoint = ValidEndpoint);
        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }

    [Fact]
    public void AddNamsAgentMemory_NullConfigure_Throws()
    {
        var services = new ServiceCollection();
        var act = () => services.AddNamsAgentMemory(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }

    [Fact]
    public void AddNamsAgentMemory_ValidConfig_PassesValidation()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o =>
        {
            o.Endpoint = ValidEndpoint;
            o.ApiKey = "nams_key";
        });
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().NotThrow();
    }

    // ── Plan's own Phase 1 test list ─────────────────────────────────────────

    [Fact]
    public void AddNamsAgentMemory_MissingEndpoint_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o => o.ApiKey = "nams_key");
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddNamsAgentMemory_MissingApiKey_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o => o.Endpoint = ValidEndpoint);
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddNamsAgentMemory_RelativeEndpoint_FailsValidation_DoesNotThrowUnrelatedException()
    {
        // Regression: a scheme-less Uri (e.g. "memory.neo4jlabs.com/v1" with no "https://" prefix -- a
        // plausible copy-paste typo) parses as a *relative* Uri. Accessing .Scheme on a relative Uri throws
        // InvalidOperationException; the validator must catch this via IsAbsoluteUri before touching
        // .Scheme, so misconfiguration surfaces as a clean OptionsValidationException, not a crash.
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o => o.Endpoint = new Uri("memory.neo4jlabs.com/v1", UriKind.RelativeOrAbsolute));
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Should().NotBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void AddNamsAgentMemory_HttpEndpoint_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o => o.Endpoint = new Uri("http://localhost:8080/v1"));
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddNamsAgentMemory_HttpEndpoint_AllowedWhenLocalDevelopmentFlagSet_PassesValidation()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o =>
        {
            o.Endpoint = new Uri("http://localhost:8080/v1");
            o.AllowInsecureEndpointForLocalDevelopment = true;
            o.ApiKey = "nams_key";
        });
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().NotThrow();
    }

    [Fact]
    public void AddNamsAgentMemory_NonPositiveRequestTimeout_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o =>
        {
            o.Endpoint = ValidEndpoint;
            o.RequestTimeout = TimeSpan.Zero;
        });
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddNamsAgentMemory_NegativeMaxRetryAttempts_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o =>
        {
            o.Endpoint = ValidEndpoint;
            o.MaxRetryAttempts = -1;
        });
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddNamsAgentMemory_NegativeInitialRetryDelay_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o =>
        {
            o.Endpoint = ValidEndpoint;
            o.InitialRetryDelay = TimeSpan.FromMilliseconds(-1);
        });
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddNamsAgentMemory_CalledTwice_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o => o.Endpoint = ValidEndpoint);
        services.AddNamsAgentMemory(o => o.ApiKey = "nams_key");
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        act.Should().NotThrow();
        // TryAddSingleton means the second AddNamsAgentMemory call didn't register a second, competing
        // NamsBackendDescriptor -- both resolutions from the same provider return the one singleton.
        provider.GetRequiredService<NamsBackendDescriptor>()
            .Should().BeSameAs(provider.GetRequiredService<NamsBackendDescriptor>());
        // Also proves the second AddHttpClient<INamsClient,...> call doesn't leave a conflicting/broken
        // registration -- not just that BuildServiceProvider() didn't throw.
        provider.GetRequiredService<INamsClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddNamsAgentMemory_DoesNotRegisterDirectNeo4jServices()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o => o.Endpoint = ValidEndpoint);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IMemoryService>().Should().BeNull();
    }

    [Fact]
    public void NamsBackendDescriptor_NotRegistered_WithoutAddNamsAgentMemory()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        provider.GetService<NamsBackendDescriptor>().Should().BeNull();
    }

    [Fact]
    public void NamsBackendDescriptor_Registered_HasExpectedIdentity()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o => o.Endpoint = ValidEndpoint);
        using var provider = services.BuildServiceProvider();

        var descriptor = provider.GetRequiredService<NamsBackendDescriptor>();
        descriptor.Name.Should().Be("nams");
        descriptor.DisplayName.Should().Contain("NAMS");
    }

    [Fact]
    public void AddNamsAgentMemory_ValidationFailureMessages_NeverContainApiKeyOrWorkspaceIdValues()
    {
        const string secretApiKey = "nams_super_secret_value_12345";
        const string secretWorkspace = "workspace-should-not-leak";
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o =>
        {
            // Deliberately invalid (missing Endpoint) so validation fails and we can inspect the message.
            o.ApiKey = secretApiKey;
            o.WorkspaceId = secretWorkspace;
        });
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<NamsOptions>>().Value;

        var exception = act.Should().Throw<OptionsValidationException>().Which;
        exception.Message.Should().NotContain(secretApiKey);
        exception.Message.Should().NotContain(secretWorkspace);
        foreach (var failure in exception.Failures)
        {
            failure.Should().NotContain(secretApiKey);
            failure.Should().NotContain(secretWorkspace);
        }
    }

    // ── Phase 2: low-level client adapter DI wiring ──────────────────────────

    [Fact]
    public void AddNamsAgentMemory_ResolvesINamsClient_WiredToConfiguredEndpoint()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o =>
        {
            o.Endpoint = ValidEndpoint;
            o.ApiKey = "nams_key";
        });
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<INamsClient>();

        client.Should().BeOfType<Neo4jNamsClientAdapter>();
    }

    [Fact]
    public void AddNamsAgentMemory_ResolvesSingleStaticApiKeyTokenProvider()
    {
        var services = new ServiceCollection();
        services.AddNamsAgentMemory(o =>
        {
            o.Endpoint = ValidEndpoint;
            o.ApiKey = "nams_key";
        });
        using var provider = services.BuildServiceProvider();

        var tokenProvider = provider.GetRequiredService<INamsAccessTokenProvider>();

        tokenProvider.Should().BeOfType<StaticApiKeyNamsAccessTokenProvider>();
        provider.GetRequiredService<INamsAccessTokenProvider>().Should().BeSameAs(tokenProvider);
    }

    [Fact]
    public void AddNamsAgentMemory_DoesNotRegisterINamsClient_WithoutOptIn()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        provider.GetService<INamsClient>().Should().BeNull();
    }
}
