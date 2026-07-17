namespace AgentMemory.Tests.Unit.Nams;

/// <summary>A deterministic, network-free <see cref="HttpMessageHandler"/> for NAMS client tests -- every test in
/// this folder uses this instead of a real network call.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _responder;
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    /// <summary>Convenience constructor for a fixed sequence of responses, one per call (the last is reused for
    /// any call beyond the sequence's length).</summary>
    public FakeHttpMessageHandler(params Func<HttpResponseMessage>[] responses)
        : this((_, callIndex) => Task.FromResult(responses[Math.Min(callIndex, responses.Length - 1)]()))
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var callIndex = Requests.Count;
        Requests.Add(request);
        return await _responder(request, callIndex).ConfigureAwait(false);
    }
}
