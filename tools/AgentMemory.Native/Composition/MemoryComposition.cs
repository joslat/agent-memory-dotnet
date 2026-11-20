using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Native.Composition;

/// <summary>Store connection settings of one native context.</summary>
internal sealed record MemorySettings
{
    public required string Uri { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string Database { get; init; } = "neo4j";
    public required int EmbeddingDimensions { get; init; }
    public TimeSpan? TransactionTimeout { get; init; }
    public TimeSpan ConnectionAcquisitionTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan MaxTransactionRetryTime { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The composition root of the read-only (R0) native profile. It registers the same public Core and Neo4j roots any
/// host uses, then narrows what a recall may do: no working-memory block (its rebuild writes during recall), strict
/// owner isolation (an unscoped recall fails closed), and an embedding generator that refuses (the caller supplies the
/// query vector). Recall goes through <see cref="IMemoryContextAssembler"/>, never <c>IMemoryService.RecallAsync</c>,
/// which writes access stamps after every recall.
/// </summary>
internal static class MemoryComposition
{
    public static ServiceProvider Build(MemorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var services = new ServiceCollection();
        services.AddLogging();
        // The library never embeds on its own here: its orchestrator turns a failed embedding into an empty vector and
        // an empty recall, which would read as "no memories". The native layer embeds query text itself and reports.
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new RefusingEmbeddingGenerator());
        services.AddAgentMemoryCore(options =>
        {
            options.WorkingMemory.Enabled = false;
            options.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant;
        });
        services.AddNeo4jAgentMemory(neo4j =>
        {
            neo4j.Uri = settings.Uri;
            neo4j.Username = settings.Username;
            neo4j.Password = settings.Password;
            neo4j.Database = settings.Database;
            neo4j.EmbeddingDimensions = settings.EmbeddingDimensions;
            neo4j.TransactionTimeout = settings.TransactionTimeout;
            // Fail fast: a caller with a deadline stops waiting long before the driver's own 60 s and 30 s would.
            neo4j.ConnectionAcquisitionTimeout = settings.ConnectionAcquisitionTimeout;
            neo4j.ConnectionTimeout = settings.ConnectionTimeout;
            neo4j.MaxTransactionRetryTime = settings.MaxTransactionRetryTime;
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>
    /// The read-only recall options: facts and preferences of the bound owner only, current valid time, every
    /// expansion, transcript, trace and graph section off.
    /// </summary>
    public static RecallOptions ReadOnlyRecall(string ownerId, int maxFacts, int maxPreferences, double minScore) =>
        RecallOptions.Default with
        {
            Scope = MemoryScope.For(ownerId, includeShared: false),
            BlendMode = RetrievalBlendMode.MemoryOnly,
            ValidTime = ValidTimeMode.Current,
            MaxRecentMessages = 0,
            MaxRelevantMessages = 0,
            MaxEntities = 0,
            MaxRelationships = 0,
            MaxTraces = 0,
            MaxGraphRagItems = 0,
            MaxFacts = maxFacts,
            MaxPreferences = maxPreferences,
            MinSimilarityScore = minScore,
            EventDetails = false,
            PersonDetails = false,
            ExpandFactsByPredicate = false,
            ExpandFactsByIdentity = false,
            ResolveQueryRelations = false,
            LegibleForgetting = false,
            ProspectiveFiring = false,
            Upcoming = false,
            TimeWindows = false,
            OverlapExpansion = false,
            IncludeDiagnostics = false,
        };

    /// <summary>One read-only recall in its own dependency scope.</summary>
    public static async Task<MemoryContext> RecallAsync(
        ServiceProvider provider,
        string ownerId,
        string sessionId,
        string? queryText,
        float[]? queryEmbedding,
        RecallOptions options,
        CancellationToken cancellationToken)
    {
        var scope = provider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var assembler = scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>();
            var request = new RecallRequest
            {
                SessionId = sessionId,
                UserId = ownerId,
                Query = queryText ?? string.Empty,
                QueryEmbedding = queryEmbedding,
                Options = options,
            };
            return await assembler.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
