using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Inference;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Decisions;

/// <summary>The AgentMemory the kernel writes into: the Conversational preset, model extraction, a live Neo4j.</summary>
internal static class DecisionKernelHost
{
    internal static IServiceProvider Build(InferenceProviderSettings settings)
    {
        if (!InferenceClientFactory.TryCreateChatClient(settings, "extraction", out var chat, out var why, generousTimeout: true))
            throw new InvalidOperationException($"No extraction model: {why}");
        if (!InferenceClientFactory.TryCreateEmbeddingGenerator(settings, out var embedder, out var whyNot))
            throw new InvalidOperationException($"No embedding model: {whyNot}");

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(chat);
        services.AddSingleton(embedder);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddNeo4jAgentMemory(
            MemoryOptions.CreateConversational(),
            neo4j =>
            {
                neo4j.Uri = Environment.GetEnvironmentVariable("Neo4j__Uri") ?? "bolt://localhost:7687";
                neo4j.Username = Environment.GetEnvironmentVariable("Neo4j__Username") ?? "neo4j";
                neo4j.Password = Environment.GetEnvironmentVariable("Neo4j__Password") ?? "password";
                neo4j.EmbeddingDimensions = settings.EmbeddingDimensions
                    ?? throw new InvalidOperationException("The embedding model's dimension is unknown; set it (fail closed).");
            },
            llm => llm.ApplyConversational());
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync().GetAwaiter().GetResult();
        return provider;
    }

    private sealed record Manifest(string Owner, DateTimeOffset BuiltAt, int Sources, List<ClockEntry> Clock);

    private sealed record ClockEntry(string Date, DateTimeOffset KnownAt);

    /// <summary>
    /// The kernel for one corpus: loaded from its manifest when one exists (no extraction), else replayed into memory under
    /// a new owner and recorded. A dry run never replays (it would spend); it returns null instead.
    /// </summary>
    internal static async Task<IDecisionRetriever> BuildOrLoadAsync(IServiceProvider services, string set, IReadOnlyList<DecisionChunk> chunks,
        string manifestPath, bool rebuild, bool dryRun, Action<string> log, CancellationToken cancellationToken)
    {
        if (!rebuild && File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))!;
            log($"  kernel {set}: reusing {manifest.Owner} ({manifest.Sources} sources, built {manifest.BuiltAt:u})");
            return DecisionKernel.Load(services, manifest.Owner, chunks, [.. manifest.Clock.Select(c => (c.Date, c.KnownAt))]);
        }
        if (dryRun)
        {
            log($"  kernel {set}: not built (a dry run spends nothing; {DecisionKernel.Sources(chunks).Count} sources would be replayed)");
            return new NothingRetriever();
        }

        var owner = $"decision-space-{set.ToLowerInvariant()}-{DateTimeOffset.UtcNow.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";
        log($"  kernel {set}: replaying {DecisionKernel.Sources(chunks).Count} sources into {owner}");
        var kernel = await DecisionKernel.BuildAsync(services, owner, chunks, concurrency: 6, log, cancellationToken).ConfigureAwait(false);
        var built = new Manifest(owner, DateTimeOffset.UtcNow, kernel.SourceCount, [.. kernel.Clock.Select(c => new ClockEntry(c.Date, c.KnownAt))]);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(built, new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            .ConfigureAwait(false);
        return kernel;
    }

    private sealed class NothingRetriever : IDecisionRetriever
    {
        public Task<IReadOnlyList<DecisionChunk>> RetrieveAsync(string question, int top, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DecisionChunk>>([]);
    }
}
