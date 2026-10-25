using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// <see cref="IMemoryClosingApprover"/> over the core writer's own <see cref="ClosingPolicy"/>: the same judges, fallback,
/// thresholds and verdicts, so a module's closings are decided exactly as the core writer's are.
/// </summary>
internal sealed class MemoryClosingApprover(
    ILogger<MemoryClosingApprover> logger,
    IOptions<ExtractionOptions>? options = null,
    IMemoryUpdateJudge? updateJudge = null,
    IMemoryUpdateJudgeFallback? fallbackJudge = null) : IMemoryClosingApprover
{
    private readonly ClosingPolicy _policy = new(options?.Value ?? new ExtractionOptions(), updateJudge, fallbackJudge, logger);

    public bool CanApprove(bool named) => named ? _policy.CanJudgeNamed : _policy.CanJudgeSimilarity;

    public async Task<IReadOnlyDictionary<string, MemoryClosingVerdict>> ApproveAsync(MemoryUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var none = new MemoryClosingVerdict(null, false);
        if (request.Pairs.Count == 0 || !CanApprove(request.Named))
            return request.Pairs.ToDictionary(p => p.Key, _ => none, StringComparer.Ordinal);
        var scores = await _policy.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        return request.Pairs.ToDictionary(p => p.Key, p => scores.TryGetValue(p.Key, out var score)
            ? new MemoryClosingVerdict(score, _policy.Approves(score, request.Named)) : none, StringComparer.Ordinal);
    }
}
