using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Extraction.AzureLanguage.Internal;

namespace Neo4j.AgentMemory.Extraction.AzureLanguage;

/// <summary>
/// DI registration helpers for the Azure Language extraction services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Azure AI Language-backed extractors and their options.
    /// </summary>
    public static IServiceCollection AddAzureLanguageExtraction(
        this IServiceCollection services,
        Action<AzureLanguageOptions> configure)
    {
        services.AddOptions<AzureLanguageOptions>().Configure(configure);

        services.AddSingleton<TextAnalyticsClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AzureLanguageOptions>>().Value;
            return new TextAnalyticsClient(new Uri(opts.Endpoint), new AzureKeyCredential(opts.ApiKey));
        });

        services.AddSingleton<ITextAnalyticsClientWrapper>(sp =>
            new TextAnalyticsClientWrapper(sp.GetRequiredService<TextAnalyticsClient>()));

        services.AddScoped<IEntityExtractor>(sp => new AzureLanguageEntityExtractor(
            sp.GetRequiredService<ITextAnalyticsClientWrapper>(),
            sp.GetRequiredService<IOptions<AzureLanguageOptions>>(),
            sp.GetRequiredService<ILogger<AzureLanguageEntityExtractor>>()));

        services.AddScoped<IFactExtractor>(sp => new AzureLanguageFactExtractor(
            sp.GetRequiredService<ITextAnalyticsClientWrapper>(),
            sp.GetRequiredService<IOptions<AzureLanguageOptions>>(),
            sp.GetRequiredService<ILogger<AzureLanguageFactExtractor>>()));

        services.AddScoped<IRelationshipExtractor>(sp => new AzureLanguageRelationshipExtractor(
            sp.GetRequiredService<ITextAnalyticsClientWrapper>(),
            sp.GetRequiredService<IOptions<AzureLanguageOptions>>(),
            sp.GetRequiredService<ILogger<AzureLanguageRelationshipExtractor>>()));

        return services;
    }
}
