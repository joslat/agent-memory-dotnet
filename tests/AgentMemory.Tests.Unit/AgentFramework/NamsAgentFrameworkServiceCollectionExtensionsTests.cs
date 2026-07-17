using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Nams;
using AgentMemory.Nams.Identity;
using AgentMemory.Nams.Persistence;
using AgentMemory.Nams.Recall;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

public sealed class NamsAgentFrameworkServiceCollectionExtensionsTests
{
    [Fact]
    public void AddNamsAgentMemoryFramework_WithPrerequisites_ResolvesProvider()
    {
        var services = new ServiceCollection();
        // Stand-ins for AddNamsAgentMemory()'s service registrations (avoids needing a real NAMS endpoint
        // just to prove DI wiring).
        services.AddSingleton(Substitute.For<INamsConversationResolver>());
        services.AddSingleton(Substitute.For<INamsRecallService>());
        services.AddSingleton(Substitute.For<INamsPersistenceService>());
        services.AddSingleton<ILogger<NamsMemoryContextProvider>>(NullLogger<NamsMemoryContextProvider>.Instance);
        services.AddOptions<ContextFormatOptions>();
        services.AddOptions<AgentFrameworkOptions>();

        services.AddNamsAgentMemoryFramework();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<NamsMemoryContextProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddNamsAgentMemoryFramework_NullServices_Throws()
    {
        IServiceCollection services = null!;

        var act = () => services.AddNamsAgentMemoryFramework();

        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }
}
