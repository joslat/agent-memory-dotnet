using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// cycle-4 #1 — guards the invariant the CLI's <c>await using</c> relies on: <see cref="Neo4jDriverFactory"/>
/// is an <see cref="IAsyncDisposable"/>-ONLY singleton, so a host/provider that owns it must be disposed via
/// <c>DisposeAsync</c>. A synchronous <c>ServiceProvider.Dispose()</c> over an async-only disposable throws
/// <see cref="InvalidOperationException"/> — which would otherwise turn every successful CLI command into a
/// spurious error + exit code 1. (Constructing the factory does not connect to Neo4j — the driver is lazy.)
/// </summary>
public sealed class Neo4jDriverFactoryDisposalTests
{
    private static Neo4jDriverFactory CreateFactory() =>
        new(Options.Create(new Neo4jOptions()), NullLogger<Neo4jDriverFactory>.Instance);

    private static ServiceProvider ProviderOwningFactory()
    {
        var services = new ServiceCollection();
        // Factory delegate (not a pre-built instance) so the container OWNS + tracks it for disposal.
        services.AddSingleton(_ => CreateFactory());
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<Neo4jDriverFactory>(); // force creation + tracking
        return provider;
    }

    [Fact]
    public void DriverFactory_IsAsyncDisposableOnly_NotSyncDisposable()
    {
        typeof(INeo4jDriverFactory).Should().BeAssignableTo<IAsyncDisposable>();
        typeof(Neo4jDriverFactory).Should().BeAssignableTo<IAsyncDisposable>();
        typeof(Neo4jDriverFactory).Should().NotBeAssignableTo<IDisposable>(
            "the CLI's await-using reasoning assumes async-only disposal; revisit Program.cs if this changes");
    }

    [Fact]
    public void SyncDispose_OfProviderOwningDriverFactory_Throws()
    {
        var provider = ProviderOwningFactory();

        var sync = () => provider.Dispose();

        sync.Should().Throw<InvalidOperationException>(
            "a synchronous provider Dispose() over an async-only disposable throws — which is exactly why the CLI uses await using");
    }

    [Fact]
    public async Task AsyncDispose_OfProviderOwningDriverFactory_Succeeds()
    {
        var provider = ProviderOwningFactory();

        var act = async () => await provider.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}
