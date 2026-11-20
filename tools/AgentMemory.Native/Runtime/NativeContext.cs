using AgentMemory.Native.Abi;
using AgentMemory.Native.Composition;
using AgentMemory.Native.Contract;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Native.Runtime;

/// <summary>
/// One open logical context: its configuration, its service container and its jobs. Every member is thread-safe.
/// Capacity is counted in unsettled work: a job counts from submit until its work has ended, whatever happened to its
/// handle or its delivered result in the meantime.
/// </summary>
internal sealed class NativeContext
{
    /// <summary>Delivered but unreleased jobs a caller may hold beyond the pending limit.</summary>
    private const int MaxRetainedBeyondPending = 256;

    private readonly object _gate = new();
    private readonly Dictionary<ulong, NativeJob> _jobs = [];
    private readonly HashSet<NativeJob> _active = [];
    private readonly SemaphoreSlim _running;
    private readonly TaskCompletionSource _closingSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ServiceProvider _provider;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private bool _closing;
    private ulong _stateRevision = 1;

    public NativeContext(ulong handle, OpenConfig config, ServiceProvider provider,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        Handle = handle;
        Config = config;
        _provider = provider;
        _embeddings = embeddings;
        _running = new SemaphoreSlim(config.MaxRunningJobs, config.MaxRunningJobs);
    }

    public ulong Handle { get; }
    public OpenConfig Config { get; }

    public int Submit(byte[] request, uint timeoutMs, out ulong jobHandle)
    {
        jobHandle = 0;
        var (operation, requestId) = NativeRequest.Peek(request);
        NativeJob job;
        lock (_gate)
        {
            if (_closing) return AbiStatus.Closing;
            if (_active.Count >= Config.MaxPendingJobs || _jobs.Count >= Config.MaxPendingJobs + MaxRetainedBeyondPending)
                return AbiStatus.Overloaded;
            job = new NativeJob(ContextRegistry.NextJobHandle(), request, operation, requestId);
            _jobs.Add(job.Handle, job);
            _active.Add(job);
        }

        var timeout = timeoutMs == 0 ? Config.DefaultTimeoutMs : (int)Math.Min(timeoutMs, int.MaxValue);
        job.Cancellation.Token.Register(static state => ((NativeJob)state!).FinishStopped(), job);
        job.Cancellation.CancelAfter(timeout);
        _ = Task.Run(() => RunAsync(job));
        jobHandle = job.Handle;
        return AbiStatus.Ok;
    }

    private async Task RunAsync(NativeJob job)
    {
        var token = job.Cancellation.Token;
        try
        {
            NativeRequest request;
            try
            {
                request = NativeRequest.Parse(job.Request, Config);
            }
            catch (ContractException invalid)
            {
                job.Finish(ResultWriter.Failure(job.Operation, job.RequestId, "invalid", invalid.Code, invalid.Message, job.Timing()));
                return;
            }

            await _running.WaitAsync(token).ConfigureAwait(false);
            try
            {
                job.MarkStarted();
                job.Finish(await ExecuteAsync(request, job, token).ConfigureAwait(false));
            }
            finally
            {
                _running.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            job.FinishStopped();
        }
        catch (Exception exception)
        {
            var (status, code, message) = Classify(exception);
            job.Finish(ResultWriter.Failure(job.Operation, job.RequestId, status, code, message, job.Timing()));
        }
        finally
        {
            job.MarkSettled();
            lock (_gate)
            {
                _active.Remove(job);
            }
        }
    }

    private async Task<byte[]> ExecuteAsync(NativeRequest request, NativeJob job, CancellationToken token) =>
        request switch
        {
            CapabilitiesRequest => ResultWriter.Capabilities(request.RequestId, Config, ContextRegistry.LibraryVersion, job.Timing()),
            GetContextRequest recall => ResultWriter.Context(
                recall.RequestId, await RecallAsync(recall, token).ConfigureAwait(false), job.Timing()),
            _ => throw new ContractException("unknown_operation", $"Operation '{request.Operation}' is not offered."),
        };

    private async Task<Abstractions.Domain.MemoryContext> RecallAsync(GetContextRequest recall, CancellationToken token)
    {
        // Query text is embedded here, not inside the library, so a failed or wrong-sized embedding is reported as such
        // instead of becoming an empty recall.
        var vector = recall.QueryVector;
        if (vector is null)
        {
            var generated = await _embeddings!.GenerateAsync([recall.QueryText!], cancellationToken: token).ConfigureAwait(false);
            vector = generated[0].Vector.ToArray();
        }
        return await MemoryComposition.RecallAsync(
                _provider,
                recall.Owner,
                recall.SessionId,
                recall.QueryText,
                vector,
                MemoryComposition.ReadOnlyRecall(recall.Owner, recall.MaxFacts, recall.MaxPreferences, recall.MinScore),
                token)
            .ConfigureAwait(false);
    }

    /// <summary>A failure as the contract reports it. Messages never carry credentials or stack traces.</summary>
    public static (string Status, string Code, string Message) Classify(Exception exception) => exception switch
    {
        global::Neo4j.Driver.AuthenticationException => ("unavailable", "authentication", "The store refused the credentials."),
        global::Neo4j.Driver.ServiceUnavailableException or global::Neo4j.Driver.SessionExpiredException =>
            ("unavailable", "store_unavailable", exception.Message),
        global::Neo4j.Driver.Neo4jException store => ("error", "store_error", $"{store.Code}: {store.Message}"),
        HttpRequestException => ("unavailable", "embedding_unavailable", $"The embedding endpoint failed: {exception.Message}"),
        TaskCanceledException { InnerException: TimeoutException } =>
            ("unavailable", "embedding_timeout", "The embedding endpoint did not answer in time."),
        Embeddings.EmbeddingMismatchException => ("error", "embedding_mismatch", exception.Message),
        InvalidOperationException when exception.Message == RefusingEmbeddingGenerator.Message =>
            ("error", "embedding_required", exception.Message),
        ContractException contract => ("invalid", contract.Code, contract.Message),
        _ => ("error", "internal", $"{exception.GetType().Name}: {exception.Message}"),
    };

    public int Poll(ulong jobHandle, uint waitMs, out byte[]? result)
    {
        result = null;
        NativeJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobHandle, out job)) return AbiStatus.InvalidHandle;
        }

        var delivered = job.Result.Task;
        if (!delivered.IsCompleted && waitMs > 0)
            Task.WaitAny([delivered, _closingSignal.Task], (int)Math.Min(waitMs, int.MaxValue));
        if (delivered.IsCompleted) result = delivered.Result;
        return AbiStatus.Ok;
    }

    public int Cancel(ulong jobHandle)
    {
        NativeJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobHandle, out job)) return AbiStatus.InvalidHandle;
        }
        job.RequestStop(StopReason.CancelledByCaller);
        return AbiStatus.Ok;
    }

    public int Release(ulong jobHandle)
    {
        lock (_gate)
        {
            return _jobs.Remove(jobHandle) ? AbiStatus.Ok : AbiStatus.InvalidHandle;
        }
    }

    public void State(out ulong stateRevision, out uint flags)
    {
        lock (_gate)
        {
            stateRevision = _stateRevision;
            // Read-only profile: no admission, and no effect-capable work exists, so writes are always quiescent.
            flags = StateFlags.WriteQuiescent | (_closing ? StateFlags.Closing : StateFlags.ReadAllowed);
        }
    }

    /// <summary>Stops admission and asks every unsettled job to stop. True when no work is left running.</summary>
    public bool BeginClose()
    {
        List<NativeJob> running;
        lock (_gate)
        {
            if (!_closing)
            {
                _closing = true;
                _stateRevision++;
                _closingSignal.TrySetResult();
            }
            if (_active.Count == 0) return true;
            running = [.. _active];
        }
        foreach (var job in running) job.RequestStop(StopReason.ContextClosing);
        lock (_gate)
        {
            return _active.Count == 0;
        }
    }

    /// <summary>Disposes the services; called once, after <see cref="BeginClose"/> reported no running work.</summary>
    public void Dispose()
    {
        _provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _embeddings?.Dispose();
        lock (_gate)
        {
            _jobs.Clear();
        }
    }
}
