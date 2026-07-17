using Microsoft.Extensions.DependencyInjection;
using AgentMemory.Nams;

namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Wires a real DI container against the live NAMS SaaS dev workspace, exactly as a consuming application
/// would via <see cref="NamsServiceCollectionExtensions.AddNamsAgentMemory"/>. Never builds a provider when
/// <see cref="NamsLiveCredentials.IsAvailable"/> is <see langword="false"/> -- individual tests skip
/// themselves via <see cref="LiveNamsFactAttribute"/>, so construction here must never throw just because
/// credentials are absent.
/// </summary>
public sealed class NamsLiveFixture : IAsyncLifetime
{
    private ServiceProvider? _provider;

    /// <summary><see langword="null"/> when <see cref="NamsLiveCredentials.IsAvailable"/> is <see langword="false"/>.</summary>
    public IServiceProvider? Services => _provider;

    public Task InitializeAsync()
    {
        if (!NamsLiveCredentials.IsAvailable)
            return Task.CompletedTask;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNamsAgentMemory(o =>
        {
            o.Endpoint = NamsWellKnown.Endpoint;
            o.ApiKey = NamsLiveCredentials.ApiKey;
            o.WorkspaceId = NamsLiveCredentials.WorkspaceId;
        });

        _provider = services.BuildServiceProvider(validateScopes: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
    }
}
