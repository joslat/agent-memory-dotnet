using System.Text.Json;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.AI;
using System.Runtime.ExceptionServices;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// One-call contextual adapter. Core owns validation and identity authority. The host owns the
/// supplied client and must keep it alive until this adapter has drained its in-flight calls.
/// Register with a matching singleton client lifetime when used through dependency injection.
/// </summary>
internal sealed class ChatIdentityAdjudicator : IIdentityAdjudicator, IAsyncDisposable
{
    // A structured protocol, not identity authority: Core still validates IDs, quotes and policy.
    private static readonly ChatResponseFormat DecisionResponseFormat=ChatResponseFormat.ForJsonSchema(
        JsonSerializer.Deserialize<JsonElement>("""
        {
          "type":"object","additionalProperties":false,
          "required":["decision","candidateId","evidence"],
          "properties":{
            "decision":{"type":"string","enum":["same","different","uncertain"]},
            "candidateId":{"type":["string","null"]},
            "evidence":{"type":"array","maxItems":32,"items":{
              "type":"object","additionalProperties":false,"required":["sourceId","quote"],
              "properties":{
                "sourceId":{"type":"string","minLength":1},
                "quote":{"type":"string","minLength":1,"maxLength":2048}
              }
            }}
          }
        }
        """), "agent_memory_identity_v1", "Identity verdict grounded in exact quotes from offered sources.");

    private readonly IChatClient _client;
    private readonly int _maximum;
    private readonly object _gate = new();
    private readonly Dictionary<long, Task<string?>> _inflight = [];
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextId;
    private bool _disposed;
    private Task? _drain;

    /// <summary>Use a host-owned client with a strict bound, including calls ignoring cancellation.</summary>
    public ChatIdentityAdjudicator(IChatClient client, int maxInFlight = 8)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (maxInFlight is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxInFlight));
        _maximum = maxInFlight;
    }

    /// <inheritdoc />
    public Task<string?> GetDecisionAsync(IdentityAdjudicationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.MaxOutputTokens is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(request), "The output cap must be between 1 and 32768 tokens.");
        long id;
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var finished in _inflight.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToArray())
                _inflight.Remove(finished);
            if (_inflight.Count >= _maximum)
                throw new InvalidOperationException("The identity adjudicator has reached its in-flight call limit.");
            id = ++_nextId;
            _inflight.Add(id, completion.Task);
        }
        _ = ObserveAsync(id, completion.Task);
        _ = CompleteOwnedAsync(request, cancellationToken, completion);
        return completion.Task;
    }

    private async Task CompleteOwnedAsync(IdentityAdjudicationRequest request, CancellationToken cancellationToken,
        TaskCompletionSource<string?> completion)
    {
        try { completion.TrySetResult(await CompleteAsync(request, cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task<string?> CompleteAsync(IdentityAdjudicationRequest request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        linked.Token.ThrowIfCancellationRequested();
        var response = await _client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, request.SystemInstructions), new ChatMessage(ChatRole.User, request.DataJson)],
            new ChatOptions { MaxOutputTokens = request.MaxOutputTokens, Temperature = 0, ResponseFormat = DecisionResponseFormat },
            linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return response.Text;
    }

    private async Task ObserveAsync(long id, Task<string?> task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* The caller receives timely faults; late faults are observed here. */ }
        finally { lock (_gate) _inflight.Remove(id); }
    }

    /// <summary>
    /// Ask owned calls to cancel, then await their completion before releasing adapter state.
    /// An uncooperative client is never disposed underneath still-running work.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_drain is null)
            {
                _disposed = true;
                _drain = DrainAsync(_inflight.Values.ToArray());
            }
            return new ValueTask(_drain);
        }
    }

    private async Task DrainAsync(Task<string?>[] pending)
    {
        Exception? cancellationError = null;
        try { _shutdown.Cancel(); }
        catch (AggregateException error) { cancellationError = error; }
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception) { /* Calls and the observer own their error reporting. */ }
        _shutdown.Dispose();
        if (cancellationError is not null) ExceptionDispatchInfo.Capture(cancellationError).Throw();
    }
}
