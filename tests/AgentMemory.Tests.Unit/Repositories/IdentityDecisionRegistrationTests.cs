using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class IdentityDecisionRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Atomic_decision_storage_is_resolvable_without_contacting_the_database(bool enabled)
    {
        var runner = Substitute.For<INeo4jTransactionRunner>();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(runner);
        services.Configure<MemoryOptions>(options => options.WorkingMemory.Enabled = false);
        services.AddNeo4jAgentMemory(options =>
        {
            options.Uri = "bolt://unused.test:7687"; options.Username = "fixture"; options.Password = "unused";
            if (enabled) options.Extensions.Add("identity-evidence");
        });
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var repository = scope.ServiceProvider.GetService<IIdentityDecisionRepository>();
        repository.Should().NotBeNull(); repository!.IsEnabled.Should().Be(enabled);
        runner.ReceivedCalls().Should().BeEmpty();
    }
}
