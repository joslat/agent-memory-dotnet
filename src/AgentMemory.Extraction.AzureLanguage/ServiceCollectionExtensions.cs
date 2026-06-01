using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.AzureLanguage.Internal;

namespace AgentMemory.Extraction.AzureLanguage;

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
        services.AddOptions<AzureLanguageOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Endpoint), "AzureLanguage Endpoint must be provided.")
            .Validate(o => Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _), "AzureLanguage Endpoint must be a valid absolute URI.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "AzureLanguage ApiKey must be provided.")
            .Validate(o => o.MaxDocumentBatchSize > 0, "AzureLanguage MaxDocumentBatchSize must be positive.")
            .ValidateOnStart();

        services.AddSingleton<TextAnalyticsClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AzureLanguageOptions>>().Value;
            return new TextAnalyticsClient(new Uri(opts.Endpoint), new AzureKeyCredential(opts.ApiKey));
        });

        services.AddSingleton<ITextAnalyticsClientWrapper>(sp =>
            new TextAnalyticsClientWrapper(sp.GetRequiredService<TextAnalyticsClient>()));

        services.TryAddScoped<AzureExtractionContext>();

        services.AddScoped<IEntityExtractor>(sp => new AzureLanguageEntityExtractor(
            sp.GetRequiredService<ITextAnalyticsClientWrapper>(),
            sp.GetRequiredService<IOptions<AzureLanguageOptions>>(),
            sp.GetRequiredService<ILogger<AzureLanguageEntityExtractor>>(),
            sp.GetRequiredService<AzureExtractionContext>()));

        services.AddScoped<IFactExtractor>(sp => new AzureLanguageFactExtractor(
            sp.GetRequiredService<ITextAnalyticsClientWrapper>(),
            sp.GetRequiredService<IOptions<AzureLanguageOptions>>(),
            sp.GetRequiredService<ILogger<AzureLanguageFactExtractor>>()));

        services.AddScoped<IRelationshipExtractor>(sp => new AzureLanguageRelationshipExtractor(
            sp.GetRequiredService<ITextAnalyticsClientWrapper>(),
            sp.GetRequiredService<IOptions<AzureLanguageOptions>>(),
            sp.GetRequiredService<ILogger<AzureLanguageRelationshipExtractor>>(),
            sp.GetRequiredService<AzureExtractionContext>()));

        services.AddScoped<IPreferenceExtractor>(sp => new AzureLanguagePreferenceExtractor(
            sp.GetRequiredService<ITextAnalyticsClientWrapper>(),
            sp.GetRequiredService<IOptions<AzureLanguageOptions>>(),
            sp.GetRequiredService<ILogger<AzureLanguagePreferenceExtractor>>()));

        return services;
    }
}
