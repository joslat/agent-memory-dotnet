using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Takes access bookkeeping off the recall path without taking it out of the process (30.12).
/// </summary>
/// <remarks>
/// <para>
/// Access tracking feeds decay and retention; nothing in a returned context depends on it, so a caller
/// waiting for it is waiting for nothing. <c>MemoryOptions.DeferAccessTracking</c> already made it
/// fire-and-forget — and its own comment admits the flaw: the write is started inside the request scope,
/// so a host that disposes that scope when the response completes can dispose the repository out from
/// under an in-flight write. The option "reads as enabled and does nothing", visible only as an
/// <see cref="ObjectDisposedException"/> in a log nobody reads.
/// </para>
/// <para>
/// This is the fix that keeps the win: a <b>singleton</b> channel owned by the root container, drained
/// by one long-running consumer that resolves its own scope per batch. The recall path does a bounded,
/// non-blocking write and returns.
/// </para>
/// <para>
/// <b>Bounded, and it drops rather than blocks.</b> An unbounded queue turns a slow database into
/// unbounded memory growth, and a blocking one puts the latency straight back on the recall path this
/// exists to clear. Dropping is the right failure for this payload specifically: a lost access stamp
/// slightly ages one memory's retention score, which is a rounding error against the decay half-life —
/// and drops are counted and logged rather than silent, because a queue quietly discarding its input
/// would make the decay curve wrong for reasons no one could see.
/// </para>
/// <para>
/// <b>Drain on dispose.</b> Shutdown completes the writer and waits for the consumer, so a run that ends
/// promptly still records what it recalled — which is what makes "audit rows equal at end of run"
/// checkable at all.
/// </para>
/// </remarks>
internal sealed class MemoryAccessTrackingChannel : IMemoryAccessTracker, IAsyncDisposable, IDisposable
{
    private readonly Channel<IReadOnlyList<(string NodeId, MemoryNodeKind Kind)>> _channel;
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<MemoryAccessTrackingChannel> _logger;
    private readonly Task _consumer;
    private readonly CancellationTokenSource _shutdown = new();

    private long _enqueued;
    private long _dropped;
    private long _written;

    public MemoryAccessTrackingChannel(
        IServiceProvider rootProvider,
        IOptions<MemoryOptions> options,
        ILogger<MemoryAccessTrackingChannel> logger)
    {
        _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var capacity = Math.Max(1, options?.Value.AccessTrackingQueueCapacity ?? 1024);
        _channel = Channel.CreateBounded(
            new BoundedChannelOptions(capacity)
            {
                // DropWrite, not Wait: waiting would reintroduce the latency this exists to remove, and
                // on a stalled database it would do so on every recall at once.
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            },
            // THE itemDropped callback, and it is not optional bookkeeping. Under DropWrite, TryWrite
            // returns TRUE and discards the item -- so a drop counter keyed on the return value counts
            // zero forever while the queue silently throws work away. That is precisely the
            // "quietly discarding its input" failure the class comment warns about, and it was built
            // that way on the first draft; the test that found it asserted a non-zero drop count on a
            // capacity-1 queue and got 0.
            (IReadOnlyList<(string NodeId, MemoryNodeKind Kind)> _) => OnDropped());

        _consumer = Task.Run(DrainAsync);
    }

    /// <summary>How many batches were accepted, dropped, and written. For tests and diagnostics.</summary>
    public (long Enqueued, long Dropped, long Written) Counters =>
        (Interlocked.Read(ref _enqueued), Interlocked.Read(ref _dropped), Interlocked.Read(ref _written));

    /// <inheritdoc/>
    public void Track(IReadOnlyList<(string NodeId, MemoryNodeKind Kind)> nodes)
    {
        if (nodes is null || nodes.Count == 0) return;

        Interlocked.Increment(ref _enqueued);
        // Returns true even when the item is dropped, under DropWrite. The drop is counted by the
        // itemDropped callback above, not here -- see the comment on the channel construction.
        if (!_channel.Writer.TryWrite(nodes)) OnDropped();
    }

    /// <summary>Counts a dropped batch and says so, rarely enough not to become the noise itself.</summary>
    private void OnDropped()
    {
        var dropped = Interlocked.Increment(ref _dropped);
        if (dropped == 1 || dropped % 100 == 0)
        {
            _logger.LogWarning(
                "Access-tracking queue full; dropped {Dropped} batch(es). Retention scores will age "
                + "slightly faster for the affected memories. Raise "
                + "MemoryOptions.AccessTrackingQueueCapacity if this persists.",
                dropped);
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var batch in _channel.Reader.ReadAllAsync(_shutdown.Token)
                .ConfigureAwait(false))
            {
                await WriteAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. Anything already in the channel is drained below by DisposeAsync's completion
            // path; this catch exists so cancellation is not an unobserved fault.
        }
        catch (Exception ex)
        {
            // The consumer must never die: a dead consumer turns every subsequent Track into a silent
            // drop, and the queue would then fill and stay full for the process's lifetime.
            _logger.LogError(ex, "Access-tracking consumer stopped unexpectedly.");
        }
    }

    private async Task WriteAsync(IReadOnlyList<(string NodeId, MemoryNodeKind Kind)> batch)
    {
        try
        {
            // A FRESH scope per batch, created and disposed here. The decay service is scoped in most
            // hosts, and a singleton capturing one instance forever is the captive-dependency trap this
            // codebase has already paid for once (the captive HttpClient in the Diffbot registration).
            // Creating it here rather than taking a factory is what lets the scope actually be disposed
            // when the write completes.
            using var scope = _rootProvider.CreateScope();
            var decay = scope.ServiceProvider.GetService<IMemoryDecayService>();
            if (decay is null) return;

            await decay.UpdateAccessTimestampsAsync(batch, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _written);
        }
        catch (Exception ex)
        {
            // Bookkeeping, on a path no caller is waiting for. Logged and swallowed: failing here would
            // kill the consumer and convert one bad batch into permanent silence.
            _logger.LogWarning(ex, "Access-tracking batch failed; retention scores are unaffected "
                + "except for these {Count} node(s).", batch.Count);
        }
    }

    /// <summary>
    /// Synchronous disposal, for a container disposed synchronously.
    /// </summary>
    /// <remarks>
    /// <b>Required, not a courtesy.</b> A singleton implementing only <see cref="IAsyncDisposable"/>
    /// makes <c>ServiceProvider.Dispose()</c> <i>throw</i> — "type only implements IAsyncDisposable" —
    /// so registering one would break every host that disposes its container the ordinary way,
    /// including <c>using var provider = services.BuildServiceProvider()</c>. Found by a test that did
    /// exactly that. Blocks on the same drain, which is bounded by the 10-second backstop below.
    /// </remarks>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        // Complete first, then wait: the consumer drains what is already queued and then exits on its
        // own, so a short-lived run still records what it recalled. The token is a backstop for a
        // consumer that is stuck inside a write.
        _channel.Writer.TryComplete();
        try
        {
            await _consumer.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Access-tracking drain did not finish within 10s; cancelling.");
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Access-tracking drain ended with a fault.");
        }

        _shutdown.Dispose();
    }
}
