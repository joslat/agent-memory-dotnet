using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Enrichment;
using AgentMemory.Observability;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Unit.Observability;

/// <summary>
/// DI-resolution tests for enrichment-service decoration. Covers both the default (unkeyed,
/// Wikimedia) path and the keyed (Diffbot) path that <c>AddAgentMemoryObservability</c> must wrap.
/// </summary>
public sealed class EnrichmentServiceDecorationTests
{
    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static void ConfigureDiffbot(DiffbotEnrichmentOptions o) => o.ApiKey = "test-key";

    [Fact]
    public void AddDiffbotEnrichment_RegistersDiffbotBehindKeyedEnrichmentService()
    {
        var services = BaseServices();
        services.AddDiffbotEnrichment(ConfigureDiffbot);

        using var provider = services.BuildServiceProvider();

        // Resolvable via the abstraction (keyed), not just the internal concrete type.
        var enrichment = provider.GetRequiredKeyedService<IEnrichmentService>(EnrichmentServiceKeys.Diffbot);
        enrichment.Should().BeOfType<CachedEnrichmentService>("Diffbot is wrapped in the cache decorator");

        // Opt-in contract (by design): Diffbot registers ONLY under the key, never as an unkeyed
        // IEnrichmentService. The background enrichment queue injects the unkeyed
        // IEnumerable<IEnrichmentService>, which .NET DI resolves WITHOUT keyed registrations — so
        // Diffbot never auto-runs and must be resolved explicitly by key.
        provider.GetService<IEnrichmentService>().Should().BeNull("Diffbot is keyed/opt-in, not a default provider");
        provider.GetServices<IEnrichmentService>().Should().BeEmpty(
            "keyed registrations are excluded from the unkeyed IEnumerable the enrichment queue consumes");
    }

    [Fact]
    public void AddAgentMemoryObservability_DecoratesKeyedDiffbotEnrichmentService()
    {
        var services = BaseServices();
        services.AddDiffbotEnrichment(ConfigureDiffbot);
        services.AddAgentMemoryObservability();

        using var provider = services.BuildServiceProvider();

        var enrichment = provider.GetRequiredKeyedService<IEnrichmentService>(EnrichmentServiceKeys.Diffbot);
        enrichment.Should().BeOfType<InstrumentedEnrichmentService>("observability wraps the keyed provider");
    }

    [Fact]
    public void AddAgentMemoryObservability_DecoratesUnkeyedEnrichmentService()
    {
        var services = BaseServices();
        services.AddEnrichmentServices();
        services.AddAgentMemoryObservability();

        using var provider = services.BuildServiceProvider();

        var enrichment = provider.GetRequiredService<IEnrichmentService>();
        enrichment.Should().BeOfType<InstrumentedEnrichmentService>("the default (Wikimedia) provider is still decorated");
    }

    [Fact]
    public void AddAgentMemoryObservability_DecoratesBothDefaultAndKeyedProviders()
    {
        var services = BaseServices();
        services.AddEnrichmentServices();
        services.AddDiffbotEnrichment(ConfigureDiffbot);
        services.AddAgentMemoryObservability();

        using var provider = services.BuildServiceProvider();

        var wikimedia = provider.GetRequiredService<IEnrichmentService>();
        var diffbot = provider.GetRequiredKeyedService<IEnrichmentService>(EnrichmentServiceKeys.Diffbot);

        wikimedia.Should().BeOfType<InstrumentedEnrichmentService>();
        diffbot.Should().BeOfType<InstrumentedEnrichmentService>();
        wikimedia.Should().NotBeSameAs(diffbot, "each provider is decorated independently");
    }

    [Fact]
    public void EnrichmentQueue_ResolvesOnlyUnkeyedProviders_DiffbotStaysOptInByKey()
    {
        // Pins the opt-in contract from the consumer's angle: BackgroundEnrichmentQueue injects
        // IEnumerable<IEnrichmentService> (unkeyed), which .NET DI resolves WITHOUT keyed registrations.
        // So with both providers registered, only Wikimedia participates in the auto queue; Diffbot is
        // reachable solely by key. If someone later registers Diffbot unkeyed (auto-running a paid API
        // on every enqueued entity), this test fails.
        var services = BaseServices();
        services.AddEnrichmentServices();                 // unkeyed Wikimedia
        services.AddDiffbotEnrichment(ConfigureDiffbot);  // keyed Diffbot
        services.AddAgentMemoryObservability();

        using var provider = services.BuildServiceProvider();

        // What the enrichment queue would receive: the unkeyed enumerable — Wikimedia only.
        var queueProviders = provider.GetServices<IEnrichmentService>().ToList();
        queueProviders.Should().ContainSingle("only the unkeyed (Wikimedia) provider auto-participates in the queue");
        queueProviders[0].Should().BeOfType<InstrumentedEnrichmentService>();

        // Diffbot remains resolvable, but exclusively by key.
        provider.GetRequiredKeyedService<IEnrichmentService>(EnrichmentServiceKeys.Diffbot)
            .Should().BeOfType<InstrumentedEnrichmentService>();
    }

    [Fact]
    public void AddAgentMemoryObservability_WithoutEnrichmentService_IsNoOp()
    {
        var services = BaseServices();
        services.AddAgentMemoryObservability();

        using var provider = services.BuildServiceProvider();

        provider.GetService<IEnrichmentService>().Should().BeNull();
        provider.GetKeyedService<IEnrichmentService>(EnrichmentServiceKeys.Diffbot).Should().BeNull();
    }
}
