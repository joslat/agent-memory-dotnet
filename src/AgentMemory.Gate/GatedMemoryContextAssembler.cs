using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Gate;

/// <summary>
/// Recall with the gate: every memory type searched wide, the judges score every memory found, and only what reaches the
/// threshold reaches the prompt. <see cref="MemoryGateMode.Today"/> is recall as without the gate (and the fallback when
/// no judge answers in time); <see cref="MemoryGateMode.Everything"/> delivers everything found.
/// </summary>
/// <remarks>
/// <para>
/// The context says what happened in <see cref="MemoryContext.Metadata"/>: <c>gate</c> (judge, today, everything, or
/// today after a judge failure), <c>gate.reason</c> on a fallback, <c>gate.offered</c> and <c>gate.kept</c>,
/// <c>gate.judgedBy</c> and <c>gate.ms</c> (the judges' time).
/// </para>
/// <para>
/// The working memory (the profile and the last messages), the forgotten-topic summaries and GraphRAG pass through
/// untouched: they are always in. Live recall only; an as-of recall is not gated.
/// </para>
/// </remarks>
[Experimental("AMGATE001")]
public sealed class GatedMemoryContextAssembler : IMemoryContextAssembler
{
    private readonly IMemoryContextAssembler _inner;
    private readonly IMemoryGate _gate;
    private readonly IOptions<MemoryGateOptions> _options;
    private readonly RecallOptions _appRecall;
    private readonly IClock? _clock;
    private readonly ILogger<GatedMemoryContextAssembler> _logger;

    /// <summary>Wraps <paramref name="inner"/>, the library's own assembler.</summary>
    public GatedMemoryContextAssembler(
        IMemoryContextAssembler inner, IMemoryGate gate, IOptions<MemoryGateOptions> options,
        ILogger<GatedMemoryContextAssembler> logger, IOptions<MemoryOptions>? memoryOptions = null, IClock? clock = null)
    {
        _inner = inner;
        _gate = gate;
        _options = options;
        _appRecall = memoryOptions?.Value.Recall ?? RecallOptions.Default;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MemoryContext> AssembleContextAsync(RecallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var o = _options.Value;
        if (o.Mode == MemoryGateMode.Today)
            return Mark(await _inner.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false), "today");
        var wide = await _inner.AssembleContextAsync(request with { Options = Wide(request.Options, o) }, cancellationToken)
            .ConfigureAwait(false);
        var candidates = GateCandidates.Of(wide);
        if (o.Mode == MemoryGateMode.Everything)
            return Mark(wide, "everything", ("gate.offered", candidates.Count), ("gate.kept", candidates.Count));
        if (candidates.Count == 0)
            return Mark(wide, "judge", ("gate.offered", 0), ("gate.kept", 0));

        string reason;
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(o.Timeout);
            var decision = await _gate.DecideAsync(new MemoryGateRequest(
                request.Query, Conversation(wide, request.Query), _clock?.UtcNow ?? DateTimeOffset.UtcNow,
                [.. candidates.Select(c => c.Candidate)]), timeout.Token).ConfigureAwait(false);
            var unanswered = candidates.Count(c => !decision.Probabilities.ContainsKey(c.Candidate.Key));
            if (unanswered > 0)
                _logger.LogWarning("Memory gate: the judges left {Unanswered} of {Offered} memories unanswered; they are left out.",
                    unanswered, candidates.Count);
            var kept = candidates
                .Where(c => decision.Probabilities.TryGetValue(c.Candidate.Key, out var p) && p >= o.Threshold)
                .Select(c => c.ItemId)
                .ToHashSet(StringComparer.Ordinal);
            _logger.LogInformation("Memory gate: kept {Kept} of {Offered} memories at P >= {Threshold} (judged by {Judges} in {Ms} ms).",
                kept.Count, candidates.Count, o.Threshold, decision.AnsweredBy, watch.ElapsedMilliseconds);
            return Mark(GateCandidates.Keep(wide, kept), "judge", ("gate.offered", candidates.Count), ("gate.kept", kept.Count),
                ("gate.judgedBy", decision.AnsweredBy), ("gate.ms", (int)watch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reason = $"the judges did not answer within {o.Timeout.TotalMilliseconds:0} ms";
            _logger.LogWarning("Memory gate: {Reason}; recall falls back to today's.", reason);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            reason = ex.Message;
            _logger.LogWarning(ex, "The memory gate failed; recall falls back to today's.");
        }
        var today = await _inner.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false);
        return Mark(today, "today (fallback)", ("gate.reason", reason), ("gate.ms", (int)watch.ElapsedMilliseconds));
    }

    /// <inheritdoc />
    public Task<MemoryContext> AssembleContextAsOfAsync(
        RecallRequest request, DateTimeOffset asOf, DateTimeOffset? systemAsOf = null, CancellationToken cancellationToken = default) =>
        _inner.AssembleContextAsOfAsync(request, asOf, systemAsOf, cancellationToken);

    /// <summary>
    /// The request's recall, opened: no similarity floor and <see cref="MemoryGateOptions.WideLimit"/> a type, so the judges
    /// see everything the types find. Starts from the application's recall when the request did not set its own.
    /// </summary>
    internal RecallOptions Wide(RecallOptions requested, MemoryGateOptions o)
    {
        var basis = ReferenceEquals(requested, RecallOptions.Default) ? _appRecall : requested;
        var n = o.WideLimit;
        return basis with
        {
            MinSimilarityScore = 0,
            MinTraceSimilarityScore = 0,
            MaxFacts = Math.Max(basis.MaxFacts, n),
            MaxEntities = Math.Max(basis.MaxEntities, n),
            MaxPreferences = Math.Max(basis.MaxPreferences, n),
            MaxRelevantMessages = Math.Max(basis.MaxRelevantMessages, n),
            MaxRelationships = Math.Max(basis.MaxRelationships, n),
            MaxTraces = Math.Max(basis.MaxTraces, n),
        };
    }

    /// <summary>The turns before this one, oldest first, as the judge reads them (the last four).</summary>
    private static IReadOnlyList<MemoryGateTurn> Conversation(MemoryContext context, string turn) =>
        [.. context.RecentMessages.Items
            .Where(m => !string.Equals(m.Content, turn, StringComparison.Ordinal))
            .OrderBy(m => m.TimestampUtc)
            .TakeLast(4)
            .Select(m => new MemoryGateTurn(m.Role, m.Content))];

    private static MemoryContext Mark(MemoryContext context, string gate, params (string Key, object Value)[] more)
    {
        var metadata = new Dictionary<string, object>(context.Metadata, StringComparer.Ordinal) { ["gate"] = gate };
        foreach (var (key, value) in more)
            metadata[key] = value;
        return context with { Metadata = metadata };
    }
}
