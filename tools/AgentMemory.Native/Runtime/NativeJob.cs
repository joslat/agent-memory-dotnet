using System.Diagnostics;
using AgentMemory.Native.Contract;

namespace AgentMemory.Native.Runtime;

/// <summary>Why a job was told to stop before its work finished.</summary>
internal enum StopReason
{
    None = 0,
    CancelledByCaller = 1,
    ContextClosing = 2,
}

/// <summary>
/// One submitted operation. Its result can be delivered (a timeout or cancel retires it at once) while its work is
/// still running; <see cref="Settled"/> turns true only when the work itself has ended, and only then does the job give
/// back its capacity.
/// </summary>
internal sealed class NativeJob(ulong handle, byte[] request)
{
    private long _startedAt;
    private int _stopReason;
    private volatile bool _settled;

    public ulong Handle { get; } = handle;
    public byte[] Request { get; } = request;
    public long SubmittedAt { get; } = Stopwatch.GetTimestamp();
    public CancellationTokenSource Cancellation { get; } = new();
    public TaskCompletionSource<byte[]> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? Operation { get; private set; }
    public string? RequestId { get; private set; }
    public bool Settled => _settled;
    public StopReason StopReason => (StopReason)Volatile.Read(ref _stopReason);

    public void Describe(NativeRequest request)
    {
        Operation = request.Operation;
        RequestId = request.RequestId;
    }

    public void MarkStarted() => Interlocked.CompareExchange(ref _startedAt, Stopwatch.GetTimestamp(), 0);

    public JobTiming Timing()
    {
        var now = Stopwatch.GetTimestamp();
        var started = Interlocked.Read(ref _startedAt);
        return started == 0
            ? new JobTiming((long)Stopwatch.GetElapsedTime(SubmittedAt, now).TotalMilliseconds, 0)
            : new JobTiming((long)Stopwatch.GetElapsedTime(SubmittedAt, started).TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(started, now).TotalMilliseconds);
    }

    /// <summary>Delivers a result; the first delivery wins, so a late finish never replaces a timeout.</summary>
    public void Finish(byte[] result) => Result.TrySetResult(result);

    /// <summary>Records why the job must stop, then signals its work; the first reason wins.</summary>
    public void RequestStop(StopReason reason)
    {
        Interlocked.CompareExchange(ref _stopReason, (int)reason, (int)StopReason.None);
        if (_settled) return;
        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The work settled between the check and the call; there is nothing left to stop.
        }
    }

    /// <summary>Delivers the stop result: cancelled by the caller, cancelled by closing, or else the deadline passed.</summary>
    public void FinishStopped()
    {
        var (status, code, message) = StopReason switch
        {
            StopReason.CancelledByCaller => ("cancelled", "cancelled", "The caller cancelled the job."),
            StopReason.ContextClosing => ("cancelled", "closing", "The context is closing."),
            _ => ("timeout", "timeout", "The job did not finish within its time."),
        };
        Finish(ResultWriter.Failure(Operation, RequestId, status, code, message, Timing()));
    }

    /// <summary>The work has ended. The deadline timer is stopped; the job keeps its delivered result.</summary>
    public void MarkSettled()
    {
        _settled = true;
        try
        {
            Cancellation.CancelAfter(Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Nothing disposes a job's cancellation source today; this keeps a future change from turning into a crash.
        }
    }
}
