using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Extraction-only provider adapter. Some reasoning deployments reject an explicit zero
/// temperature, so the harness uses the provider default and fingerprints that behavior.
/// Answer and judge requests do not pass through this adapter.
/// </summary>
internal sealed class ProviderCompatibleExtractionChatClient(IChatClient inner) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Normalize(options);
        return inner.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Normalize(options);
        await foreach (var update in inner
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    /// <summary>Whether any call in this process had its temperature-0 request dropped.</summary>
    /// <remarks>
    /// Measured consequence, not a hypothetical: three cold builds of the IDENTICAL configuration over
    /// the identical corpus stored 6,078 / 6,199 / 6,272 canonical triples with only <b>7.5% common to
    /// all three</b> and ~60% unique to each, and scored 25 accuracy points apart. Extraction was
    /// running at the provider default of 1.0 the whole time, because this deployment rejects an
    /// explicit 0 outright. The rewrite is necessary - without it every call throws - but a run whose
    /// artifacts imply determinism it never had is the same failure as a CLI flag that is accepted and
    /// ignored. This flag exists so the report can say so.
    /// </remarks>
    internal static bool TemperatureRequestWasDropped { get; private set; }

    private static void Normalize(ChatOptions? options)
    {
        if (options?.Temperature == 0)
        {
            options.Temperature = null;
            TemperatureRequestWasDropped = true;
        }
    }
}
