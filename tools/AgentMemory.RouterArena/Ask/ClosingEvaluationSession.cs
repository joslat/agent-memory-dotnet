using System.Diagnostics;
using AgentMemory.Gate;
using Microsoft.Extensions.Options;

namespace AgentMemory.RouterArena.Ask;

internal sealed record ClosingResponse
{
    public required string Id { get; init; }
    public required bool Named { get; init; }
    public double? Score { get; init; }
    public string? Error { get; init; }
    public double ElapsedMs { get; init; }
    public required CompoundRequestCounts Counts { get; init; }
}

internal static class ClosingEvaluationSession
{
    public static async Task<ClosingResponse> RunAsync(ClosingRequest item, ClosingConfiguration configuration,
        HttpMessageHandler? transport = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Fresh clients keep a previous timeout or cooldown out of the next observation.
        using var meter = new CompoundHttpMeter(transport ?? new SocketsHttpHandler { AllowAutoRedirect = false });
        using var http = new HttpClient(meter) { Timeout = Timeout.InfiniteTimeSpan };
        var judge = new SystemOneUpdateJudge(new SystemOneClient(http), Options.Create(new MemoryGateOptions
        {
            UpdateJudge = true, Judges = [configuration.Judge], JudgeFailuresBeforeCooldown = 0,
        }));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(configuration.Timeout);
        var watch = Stopwatch.StartNew();
        double? score = null;
        string? error = null;
        try
        {
            var answers = await judge.JudgeAsync(item.Request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!answers.TryGetValue("p1", out var value)) error = "missing-score";
            else if (!double.IsFinite(value) || value is < 0 or > 1) error = "invalid-score";
            else score = value;
        }
        // The command checkpoints this attempted call before propagating caller cancellation.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { error = "cancelled"; }
        catch (OperationCanceledException) { error = "timeout"; }
        catch (HttpRequestException ex) { error = ex.StatusCode is { } code ? $"http-{(int)code}" : "transport-error"; }
        catch (Exception ex) { error = ex.GetType().Name; }
        return new ClosingResponse
        {
            Id = item.Id, Named = item.Request.Named, Score = score, Error = error,
            ElapsedMs = watch.Elapsed.TotalMilliseconds, Counts = meter.Snapshot(),
        };
    }
}
