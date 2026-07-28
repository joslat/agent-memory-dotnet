using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Compatibility adapter for reasoning deployments that reject an explicit temperature of zero.
/// AgentEval 0.16 hard-codes zero in LongMemEvalJudge; these deployments only accept their default.
/// </summary>
internal sealed class DefaultTemperatureChatClient(IChatClient inner) : IChatClient
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

    private static void Normalize(ChatOptions? options)
    {
        if (options?.Temperature == 0)
            options.Temperature = null;
    }
}
