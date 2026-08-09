using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Narrow compatibility adapter for AgentEval 0.16's reasoning-model judge request. It removes the
/// unsupported explicit zero temperature and raises only the exact 30-token judge ceiling so hidden
/// reasoning cannot consume the entire allowance before emitting the required yes/no verdict.
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
        if (options?.Temperature != 0 || options.MaxOutputTokens != 30)
            return;

        options.Temperature = null;
        options.MaxOutputTokens = 512;
    }
}
