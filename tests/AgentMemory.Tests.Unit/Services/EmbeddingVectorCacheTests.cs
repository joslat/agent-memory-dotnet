using AgentMemory.Abstractions.Options;
using AgentMemory.Core;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The same text is embedded once. Measured live: the user's message went out twice per turn (as the
/// recall query, then as the stored message), and known entity names every turn they were mentioned.
/// </summary>
public sealed class EmbeddingVectorCacheTests
{
    private sealed class CountingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string[]> Calls { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var inputs = values.ToArray();
            Calls.Add(inputs);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                inputs.Select(v => new Embedding<float>(new[] { v.Length, 1f }))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static EmbeddingOrchestrator Orchestrator(CountingGenerator generator, int capacity) =>
        new(generator, NullLogger<EmbeddingOrchestrator>.Instance, new EmbeddingVectorCache(capacity));

    [Fact]
    public void On_by_default_since_2_4_except_under_strict_multi_tenant_isolation_where_only_a_set_value_turns_it_on()
    {
        // Owner decision, 22 Nov 2026 (docket def-speed-2): a cached text answers faster, so in a shared deployment one
        // tenant could tell that another asked the same text. Strict multi-tenant hosts keep it off unless they set it.
        new MemoryOptions().EmbeddingCacheCapacity.Should().Be(MemoryOptions.DefaultEmbeddingCacheCapacity).And.Be(512);

        var strict = new MemoryOptions();
        strict.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant;
        strict.EmbeddingCacheCapacity.Should().Be(0);
        strict.EmbeddingCacheCapacity = 64;
        strict.EmbeddingCacheCapacity.Should().Be(64, "a value the host sets is used as given");
        (strict with { }).EmbeddingCacheCapacity.Should().Be(64, "a copy keeps the host's value");

        MemoryOptions.CreateConversational().EmbeddingCacheCapacity.Should().Be(512, "the preset sets it with strict isolation");
        new MemoryOptions { EmbeddingCacheCapacity = 0 }.EmbeddingCacheCapacity.Should().Be(0, "0 still turns it off");
    }

    [Fact]
    public void The_container_sizes_the_cache_from_the_options_as_resolved()
    {
        using var defaults = new ServiceCollection().AddLogging().AddAgentMemoryCore(_ => { }).BuildServiceProvider();
        defaults.GetRequiredService<EmbeddingVectorCache>().Capacity.Should().Be(512);

        using var strict = new ServiceCollection().AddLogging()
            .AddAgentMemoryCore(o => o.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant).BuildServiceProvider();
        strict.GetRequiredService<EmbeddingVectorCache>().Capacity.Should().Be(0);
    }

    [Fact]
    public async Task At_capacity_zero_every_request_goes_out()
    {
        var generator = new CountingGenerator();
        var sut = Orchestrator(generator, capacity: 0);

        await sut.EmbedAsync("hello");
        await sut.EmbedAsync("hello");

        generator.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_repeated_text_is_served_from_memory()
    {
        var generator = new CountingGenerator();
        var sut = Orchestrator(generator, capacity: 8);

        var first = await sut.EmbedAsync("My friend Priya Nair works at Contoso.");
        var second = await sut.EmbedAsync("My friend Priya Nair works at Contoso.");

        generator.Calls.Should().ContainSingle();
        second.Should().Equal(first);
    }

    [Fact]
    public async Task A_batch_sends_only_what_it_has_not_seen_and_keeps_positions()
    {
        var generator = new CountingGenerator();
        var sut = Orchestrator(generator, capacity: 8);
        await sut.EmbedAsync("Priya Nair");

        var vectors = await sut.EmbedBatchAsync(["Priya Nair", "", "Contoso", "Madrid"]);

        generator.Calls.Should().HaveCount(2);
        generator.Calls[1].Should().Equal("Contoso", "Madrid");
        vectors[0].Should().Equal(10f, 1f);
        vectors[1].Should().BeEmpty();
        vectors[2].Should().Equal(7f, 1f);
        vectors[3].Should().Equal(6f, 1f);
    }

    [Fact]
    public async Task A_batch_of_known_texts_makes_no_request()
    {
        var generator = new CountingGenerator();
        var sut = Orchestrator(generator, capacity: 8);
        await sut.EmbedBatchAsync(["a1", "b1"]);

        await sut.EmbedBatchAsync(["b1", "a1"]);

        generator.Calls.Should().ContainSingle();
    }

    [Fact]
    public void The_least_recently_used_entry_is_evicted()
    {
        var cache = new EmbeddingVectorCache(2);
        cache.Set("a", [1f]);
        cache.Set("b", [2f]);
        cache.TryGet("a", out _).Should().BeTrue();   // a is now the most recent
        cache.Set("c", [3f]);

        cache.TryGet("b", out _).Should().BeFalse();
        cache.TryGet("a", out _).Should().BeTrue();
        cache.TryGet("c", out _).Should().BeTrue();
        cache.Count.Should().Be(2);
    }

    [Fact]
    public void Failed_generations_are_never_remembered()
    {
        var cache = new EmbeddingVectorCache(4);
        cache.Set("x", []);

        cache.TryGet("x", out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_container_shares_one_cache_across_scopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var generator = new CountingGenerator();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
        services.AddAgentMemoryCore(o => o.EmbeddingCacheCapacity = 16);
        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AgentMemory.Abstractions.Services.IEmbeddingOrchestrator>().EmbedAsync("same");
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AgentMemory.Abstractions.Services.IEmbeddingOrchestrator>().EmbedAsync("same");

        generator.Calls.Should().ContainSingle();
    }

    [Fact]
    public void A_negative_capacity_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(o => o.EmbeddingCacheCapacity = -1);
        using var provider = services.BuildServiceProvider();

        var act = () => _ = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }
}
