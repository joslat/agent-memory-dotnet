using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.RouterArena.Ask;

internal sealed record GateContextResult
{
    public required string RequestSha256 { get; init; }
    public IReadOnlyDictionary<string, double> Scores { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<string> Selected { get; init; } = [];
    public IReadOnlyList<string> GateRejected { get; init; } = [];
    public IReadOnlyList<string> BudgetRejected { get; init; } = [];
    public string? Context { get; init; }
    public string? AnsweredBy { get; init; }
    public string? Error { get; init; }
    public double ElapsedMilliseconds { get; init; }
}

internal sealed record PairedGateResult
{
    public required GateContextResult Baseline { get; init; }
    public required GateContextResult Overlay { get; init; }
    public bool ReusedBaseline { get; init; }
    // Null means the comparison failed, not that no original was displaced.
    public IReadOnlyList<string>? DisplacedOriginals { get; init; }
}

/// <summary>
/// Controlled gate/context comparison over recorded candidate pools. It does not search a store,
/// simulate search misses, establish source support, or implement the library's context truncation policy.
/// </summary>
internal static class PairedGateEvaluation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Render(IReadOnlyList<MemoryGateCandidate> items) => JsonSerializer.Serialize(items, Json);

    public static async Task<PairedGateResult> RunAsync(MemoryGateRequest baseline, MemoryGateRequest overlay,
        double threshold, int maxContextCharacters, TimeSpan timeout,
        Func<MemoryGateRequest, CancellationToken, Task<MemoryGateDecision>> decide,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(decide);
        ValidatePair(baseline, overlay, threshold, maxContextCharacters, timeout);

        var first = await EvaluateAsync(baseline, threshold, maxContextCharacters, timeout, decide, cancellationToken).ConfigureAwait(false);
        var reuse = first.RequestSha256 == Hash(overlay);
        var second = reuse ? first : await EvaluateAsync(overlay, threshold, maxContextCharacters, timeout, decide, cancellationToken).ConfigureAwait(false);
        return new PairedGateResult
        {
            Baseline = first, Overlay = second, ReusedBaseline = reuse,
            DisplacedOriginals = first.Error is null && second.Error is null
                ? first.Selected.Except(second.Selected, StringComparer.Ordinal).ToArray() : null,
        };
    }

    internal static void ValidatePair(MemoryGateRequest baseline, MemoryGateRequest overlay,
        double threshold, int maxContextCharacters, TimeSpan timeout)
    {
        Validate(baseline);
        Validate(overlay);
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1 || maxContextCharacters is < 2 or > 131072 ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Invalid gate/context budget.");
        var originals = baseline.Candidates.ToDictionary(c => c.Key, StringComparer.Ordinal);
        if (baseline.Turn != overlay.Turn || baseline.Now != overlay.Now ||
            !baseline.Conversation.SequenceEqual(overlay.Conversation) ||
            !baseline.Candidates.SequenceEqual(overlay.Candidates.Where(c => originals.ContainsKey(c.Key))))
            throw new ArgumentException("Both arms must retain the same turn, clock, conversation and ordered original pool.");

    }

    private static async Task<GateContextResult> EvaluateAsync(MemoryGateRequest request, double threshold, int budget,
        TimeSpan timeout, Func<MemoryGateRequest, CancellationToken, Task<MemoryGateDecision>> decide, CancellationToken cancellationToken)
    {
        var hash = Hash(request);
        var watch = Stopwatch.StartNew();
        GateContextResult Failure(string error) => new() { RequestSha256 = hash, Error = error, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds };
        if (request.Candidates.Count == 0) return new() { RequestSha256 = hash, Context = Render([]) };
        MemoryGateDecision decision;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            decision = await decide(request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure("timeout"); }
        catch (Exception ex) { return Failure(ex.GetType().Name); }
        var keys = request.Candidates.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        if (decision?.Probabilities is null || decision.Probabilities.Count != keys.Count ||
            decision.Probabilities.Any(p => !keys.Contains(p.Key) || !double.IsFinite(p.Value) || p.Value is < 0 or > 1))
            return Failure("invalid-decision");

        List<MemoryGateCandidate> selected = [];
        List<string> gateRejected = [], budgetRejected = [];
        foreach (var item in request.Candidates)
        {
            if (decision.Probabilities[item.Key] < threshold) gateRejected.Add(item.Key);
            else if (Render([.. selected, item]).Length > budget) budgetRejected.Add(item.Key);
            else selected.Add(item);
        }
        return new GateContextResult
        {
            RequestSha256 = hash, Scores = new Dictionary<string, double>(decision.Probabilities, StringComparer.Ordinal),
            Selected = selected.Select(c => c.Key).ToArray(), GateRejected = gateRejected, BudgetRejected = budgetRejected,
            Context = Render(selected), AnsweredBy = decision.AnsweredBy, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
        };
    }

    private static string Hash(MemoryGateRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json))).ToLowerInvariant();

    private static void Validate(MemoryGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Turn) || request.Turn.Length > 8000 || request.Conversation is null ||
            request.Conversation.Count > 32 || request.Conversation.Any(t => t is null || string.IsNullOrWhiteSpace(t.Role) || t.Text is null) ||
            request.Candidates is null || request.Candidates.Count > 512 ||
            request.Candidates.Any(c => c is null || string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.MemoryType) || string.IsNullOrWhiteSpace(c.Text)) ||
            request.Candidates.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count ||
            JsonSerializer.Serialize(request, Json).Length > 131072)
            throw new ArgumentException("Invalid or oversized gate request.");
    }
}
