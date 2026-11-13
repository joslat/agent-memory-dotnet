using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class IdentityBackendRegistrationTests
{
    private static (ServiceProvider Services, INeo4jTransactionRunner Runner) Services(bool enabled)
    {
        var runner = Substitute.For<INeo4jTransactionRunner>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<MemoryOptions>(options => options.WorkingMemory.Enabled = false);
        services.AddSingleton(runner);
        services.AddNeo4jAgentMemory(options =>
        {
            options.Uri = "bolt://unused.test:7687"; options.Username = "fixture"; options.Password = "unused";
            if (enabled) options.Extensions.Add("identity-evidence");
        });
        return (services.BuildServiceProvider(), runner);
    }

    [Fact]
    public void Neo4j_exposes_an_explicit_bounded_candidate_capability_when_its_schema_is_enabled()
    {
        var (services, runner) = Services(enabled: true);
        using (services)
        {
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider.GetService<IIdentityCandidateProvider>();
            provider.Should().NotBeNull();
            provider!.Capabilities.BoundedReads.Should().BeTrue();
            provider.Capabilities.VerifiedKeyLookup.Should().BeTrue();
            provider.Capabilities.CalendarValueLookup.Should().BeTrue();
            runner.ReceivedCalls().Should().BeEmpty("registration and capability inspection perform no DB work");
        }
    }

    [Fact]
    public void Evidence_registration_is_resolvable_but_inert_without_explicit_schema_activation()
    {
        var (services, runner) = Services(enabled: false);
        using (services)
        {
            using var scope = services.CreateScope();
            var registry = scope.ServiceProvider.GetService<IIdentityEvidenceRepository>();
            registry.Should().NotBeNull();
            registry!.IsEnabled.Should().BeFalse();
            runner.ReceivedCalls().Should().BeEmpty();
        }
    }
}
