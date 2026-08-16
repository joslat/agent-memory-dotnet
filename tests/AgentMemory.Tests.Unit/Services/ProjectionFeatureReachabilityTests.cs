using System.Reflection;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 step 12. Every projection feature is reachable, and every flag reaches a feature.
/// </summary>
/// <remarks>
/// <para>
/// Two directions, because the failure class has appeared in both. A feature registered by nobody is
/// the reranker defect: two rerankers shipped with full unit suites, zero DI registrations and zero
/// call sites, while their options sat in the public surface documenting behaviour no consumer could
/// obtain. A flag that reaches no feature is the same defect wearing the opposite mask — the option
/// binds, validates, and does nothing, which is how <c>IncludeQuestionTypes</c> and
/// <c>AbstentionPolicy</c> were found dead only when a measurement failed to move.
/// </para>
/// <para>
/// Reflected rather than listed, so neither direction can go stale.
/// </para>
/// </remarks>
public sealed class ProjectionFeatureReachabilityTests
{
    /// <summary>
    /// Core plus the repositories an adapter supplies — the realistic shape.
    /// </summary>
    /// <remarks>
    /// Core is not resolvable standalone and was not before this feature: it registers
    /// <c>ILongTermMemoryService</c>, whose implementation already requires <c>IFactRepository</c>. So
    /// the two repository-injecting projection features introduce no new unsatisfiable dependency —
    /// worth stating, because an unconditional registration with an unsatisfiable dependency is exactly
    /// what broke <c>ValidateOnBuild</c> during the 1.0 lockdown.
    /// </remarks>
    private static ServiceProvider BuildContainer() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>())
            .AddAgentMemoryCore(_ => { })
            .BuildServiceProvider();

    private static IReadOnlyList<Type> ImplementationsInAssembly() =>
        [.. typeof(IProjectionFeature).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && type.IsAssignableTo(typeof(IProjectionFeature)))];

    [Fact]
    public void EveryImplementationInTheAssemblyIsRegistered()
    {
        var implemented = ImplementationsInAssembly();
        implemented.Should().NotBeEmpty();

        using var provider = BuildContainer();
        using var scope = provider.CreateScope();
        var registered = scope.ServiceProvider.GetServices<IProjectionFeature>()
            .Select(feature => feature.GetType()).ToHashSet();

        implemented.Should().BeSubsetOf(registered,
            "a projection feature the container cannot produce is a feature no flag can turn on");
    }

    [Fact]
    public void AllSixFeaturesResolve()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IProjectionFeature>().Should().HaveCount(6);
    }

    [Fact]
    public void EveryProjectionFlagEnablesAtLeastOneRegisteredFeature()
    {
        // THE other direction. A boolean on MemoryProjectionOptions that no feature reads is an option
        // that binds and does nothing -- documented behaviour a consumer cannot obtain.
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();
        var features = scope.ServiceProvider.GetServices<IProjectionFeature>().ToList();

        var flags = typeof(MemoryProjectionOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool))
            .ToList();

        flags.Should().NotBeEmpty();

        foreach (var flag in flags)
        {
            var enabled = MemoryProjectionOptions.Default;
            // Records are immutable; rebuild through the copy constructor by reflection so this test
            // needs no maintenance when a flag is added.
            enabled = (MemoryProjectionOptions)typeof(MemoryProjectionOptions)
                .GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(enabled, null)!;
            flag.SetValue(enabled, true);

            features.Should().Contain(feature => feature.IsEnabled(enabled),
                $"'{flag.Name}' must switch on at least one registered projection feature");
        }
    }

    [Fact]
    public void NoFlagSetMeansNoFeatureIsEnabled()
    {
        // The off-state, asserted across the whole registered set at once.
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IProjectionFeature>()
            .Should().OnlyContain(feature => !feature.IsEnabled(MemoryProjectionOptions.Default));
    }

    [Fact]
    public void TheProjectorRunsNothingWhenNothingIsEnabled()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();
        var projector = new MemoryContextProjector(
            scope.ServiceProvider.GetServices<IProjectionFeature>());

        projector.Features.Should().HaveCount(6);
        projector.Features.Should().OnlyContain(f => !f.IsEnabled(MemoryProjectionOptions.Default));
    }
}
