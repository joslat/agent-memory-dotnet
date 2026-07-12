namespace AgentMemory.Enrichment;

/// <summary>
/// Service keys for keyed <see cref="AgentMemory.Abstractions.Services.IEnrichmentService"/>
/// registrations. Use with <c>GetRequiredKeyedService</c> or the
/// <c>[FromKeyedServices]</c> attribute to resolve a specific enrichment provider.
/// </summary>
public static class EnrichmentServiceKeys
{
    /// <summary>
    /// Key for the Diffbot-backed enrichment service registered by
    /// <see cref="ServiceCollectionExtensions.AddDiffbotEnrichment"/>. Diffbot is registered as a
    /// keyed, <b>opt-in</b> service: it coexists with the default (Wikimedia) unkeyed
    /// <see cref="AgentMemory.Abstractions.Services.IEnrichmentService"/> registration but is
    /// <b>excluded from the automatic background enrichment queue</b> (which resolves the unkeyed
    /// <c>IEnumerable&lt;IEnrichmentService&gt;</c>, and .NET DI never surfaces keyed registrations there).
    /// To use Diffbot, resolve it explicitly by this key via
    /// <c>GetRequiredKeyedService&lt;IEnrichmentService&gt;(EnrichmentServiceKeys.Diffbot)</c> or
    /// <c>[FromKeyedServices(EnrichmentServiceKeys.Diffbot)]</c> and invoke it yourself.
    /// </summary>
    public const string Diffbot = "Diffbot";
}
