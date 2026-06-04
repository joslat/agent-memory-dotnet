using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

internal static class ScriptedDemo
{
    private const string RecallQuery = "Deployment summaries before 09:00 UTC.";

    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var memoryService = services.GetRequiredService<IMemoryService>();
        var assembler = services.GetRequiredService<IMemoryContextAssembler>();

        var request = new RecallRequest
        {
            SessionId = DemoDataSeeder.SessionId,
            Query = RecallQuery,
            Options = new RecallOptions
            {
                MinSimilarityScore = 0.0,
                MaxRecentMessages = 6,
                MaxRelevantMessages = 3,
                MaxEntities = 4,
                MaxPreferences = 3,
                MaxFacts = 3,
                MaxTraces = 1
            }
        };

        var recall = await memoryService.RecallAsync(request, cancellationToken);
        logger.LogInformation("Scripted recall query: {Query}", RecallQuery);
        logger.LogInformation(
            "Recall totals -> items={TotalItems}, recent={RecentCount}, relevantMessages={RelevantCount}, entities={EntityCount}, preferences={PreferenceCount}, facts={FactCount}",
            recall.TotalItemsRetrieved,
            recall.Context.RecentMessages.Items.Count,
            recall.Context.RelevantMessages.Items.Count,
            recall.Context.RelevantEntities.Items.Count,
            recall.Context.RelevantPreferences.Items.Count,
            recall.Context.RelevantFacts.Items.Count);

        foreach (var preference in recall.Context.RelevantPreferences.Items.Take(2))
            logger.LogInformation("Preference hit -> {Category}: {Preference}", preference.Category, preference.PreferenceText);

        foreach (var fact in recall.Context.RelevantFacts.Items.Take(2))
            logger.LogInformation("Fact hit -> {Subject} {Predicate} {Object}", fact.Subject, fact.Predicate, fact.Object);

        var context = await assembler.AssembleContextAsync(request, cancellationToken);
        logger.LogInformation(
            "Assembled context -> recent={RecentCount}, entities={EntityCount}, preferences={PreferenceCount}, facts={FactCount}",
            context.RecentMessages.Items.Count,
            context.RelevantEntities.Items.Count,
            context.RelevantPreferences.Items.Count,
            context.RelevantFacts.Items.Count);

        foreach (var entity in context.RelevantEntities.Items.Take(3))
            logger.LogInformation("Entity hit -> {Name} ({Type})", entity.Name, entity.Type);

        foreach (var message in context.RelevantMessages.Items.Take(2))
            logger.LogInformation("Relevant message -> [{Role}] {Content}", message.Role, message.Content);
    }
}

internal sealed class DisabledGraphRagContextSource : IGraphRagContextSource
{
    public Task<GraphRagContextResult> GetContextAsync(
        GraphRagContextRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GraphRagContextResult
        {
            Items = Array.Empty<GraphRagContextItem>(),
            Metadata = new Dictionary<string, object>
            {
                ["mode"] = "disabled"
            }
        });
}

internal sealed class DisabledMemoryExtractionPipeline : IMemoryExtractionPipeline
{
    public Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExtractionResult
        {
            Metadata = new Dictionary<string, object>
            {
                ["mode"] = "disabled"
            }
        });
}
