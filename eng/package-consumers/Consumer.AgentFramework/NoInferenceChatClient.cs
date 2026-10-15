using Microsoft.Extensions.AI;

internal sealed class NoInferenceChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Package composition must not invoke inference.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Package composition must not invoke inference.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
