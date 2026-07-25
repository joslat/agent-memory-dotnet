using Microsoft.Extensions.AI;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// A model stand-in that returns a fixed, valid extraction payload after a configurable delay.
/// </summary>
/// <remarks>
/// <para>
/// The response body is <b>not</b> arbitrary text. All four LLM extractors parse the same JSON shape
/// (<c>entities</c> / <c>facts</c> / <c>preferences</c> / <c>relations</c>), and if the model returns
/// something unparseable, extraction yields nothing, persistence writes nothing, and a post-turn
/// scenario silently measures a no-op while looking perfectly healthy. Returning a valid payload is
/// what makes <c>PERF-W-02</c> measure a real default turn.
/// </para>
/// <para>
/// <paramref name="delay"/> exists so the hermetic profile can reproduce the <em>shape</em> of a remote
/// deployment deterministically. Measuring at zero latency isolates database and CPU cost; measuring at
/// a remote-like latency isolates orchestration and overlap. A change that improves only the latter is
/// an ordering win; one that improves both removed work.
/// </para>
/// </remarks>
public sealed class ScriptedChatClient : IChatClient
{
    /// <summary>
    /// Valid extraction output. Deliberately small and fixed: the point is to exercise the persistence
    /// path with a realistic item count, not to simulate a model's variability.
    /// </summary>
    public const string ExtractionPayload = """
        {
          "entities": [
            {"name": "Acme Corporation", "type": "ORGANIZATION", "confidence": 0.92},
            {"name": "Alice Martin", "type": "PERSON", "confidence": 0.95}
          ],
          "facts": [
            {"subject": "Alice Martin", "predicate": "works_at", "object": "Acme Corporation", "confidence": 0.9},
            {"subject": "Alice Martin", "predicate": "leads", "object": "platform team", "confidence": 0.85}
          ],
          "preferences": [
            {"category": "communication", "preference": "prefers concise written summaries", "confidence": 0.88}
          ],
          "relations": [
            {"source": "Alice Martin", "target": "Acme Corporation", "relationType": "WORKS_AT", "confidence": 0.9}
          ]
        }
        """;

    private readonly TimeSpan _delay;
    private readonly string _payload;

    public ScriptedChatClient(TimeSpan delay, string? payload = null)
    {
        _delay = delay;
        _payload = payload ?? ExtractionPayload;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

        // Token counts are approximated from character length rather than invented, so cost accounting
        // stays proportional to real prompt growth as scenarios change.
        var inputChars = messages.Sum(m => m.Text?.Length ?? 0);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, _payload))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputChars / 4,
                OutputTokenCount = _payload.Length / 4,
            },
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
