using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Gate;

/// <summary>
/// Recall with the gate: every memory type searched wide, the judges score every memory found, and only what reaches the
/// threshold reaches the prompt. <see cref="MemoryGateMode.Floor"/> is recall as without the gate (and the fallback when
/// no judge answers in time); <see cref="MemoryGateMode.Everything"/> delivers everything found.
/// </summary>
/// <remarks>
/// <para>
/// The context says what happened in <see cref="MemoryContext.Metadata"/>: <c>gate</c> (judge, floor, everything, or
/// floor after a judge failure), <c>gate.reason</c> on a fallback, <c>gate.offered</c> and <c>gate.kept</c>,
/// <c>gate.judgedBy</c> and <c>gate.ms</c> (the judges' time); and, in every mode, a <see cref="MemoryGateTrace"/> under
/// <c>gate.trace</c> with each memory considered, its probability and whether it went in. Each recall is a
/// <c>memory.gate</c> span with the counts (<see cref="MemoryGateTelemetry"/>). Judge mode with no judge configured is the
/// floor, said once in the log.
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
    private int _warnedNoJudge;

    /// <summary>The fallback reason when Judge mode has no judge configured.</summary>
    internal const string NoJudge = "no judge is configured (MemoryGateOptions.Judges)";

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
        using var span = AgentMemoryDiagnostics.Source.StartActivity(MemoryGateTelemetry.Span);
        span?.SetTag(MemoryGateTelemetry.Mode, Name(o.Mode));
        if (o.Mode == MemoryGateMode.Floor)
            return Floor(await _inner.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false), o, span, "floor", null, 0);
        if (o.Mode == MemoryGateMode.Judge && _gate is SystemOneMemoryGate && !SystemOneMemoryGate.UsableJudges(o).Any())
        {
            // Said once, not every turn: a host that chose Judge and configured no judge gets the floor, and is told so.
            if (Interlocked.Exchange(ref _warnedNoJudge, 1) == 0)
                _logger.LogWarning("Memory gate: Judge mode has no judge configured (MemoryGateOptions.Judges); recall falls back to the similarity floor.");
            return Floor(await _inner.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false), o, span,
                "floor (fallback)", NoJudge, 0);
        }
        var wide = await _inner.AssembleContextAsync(request with { Options = Wide(request.Options, o) }, cancellationToken)
            .ConfigureAwait(false);
        var candidates = GateCandidates.Of(wide);
        if (o.Mode == MemoryGateMode.Everything)
            return Mark(wide, o, span, "everything", null, [.. candidates.Select(c => Item(c, null, kept: true))], "", 0);
        if (candidates.Count == 0)
            return Mark(wide, o, span, "judge", null, [], "", 0);

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
            List<MemoryGateTraceItem> items = [.. candidates.Select(c => Item(c,
                decision.Probabilities.TryGetValue(c.Candidate.Key, out var p) ? p : null, kept.Contains(c.ItemId)))];
            _logger.LogInformation("Memory gate: kept {Kept} of {Offered} memories at P >= {Threshold} (judged by {Judges} in {Ms} ms).",
                items.Count(i => i.Kept), items.Count, o.Threshold, decision.AnsweredBy, watch.ElapsedMilliseconds);
            return Mark(GateCandidates.Keep(wide, kept), o, span, "judge", null, items, decision.AnsweredBy, watch.ElapsedMilliseconds,
                ("gate.judgedBy", decision.AnsweredBy), ("gate.ms", (int)watch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reason = $"the judges did not answer within {o.Timeout.TotalMilliseconds:0} ms";
            span?.SetTag(MemoryGateTelemetry.Fallback, "timeout");
            _logger.LogWarning("Memory gate: {Reason}; recall falls back to the similarity floor.", reason);
        }
        catch (OperationCanceledException) { throw; }
        catch (JudgeCoolingDownException ex)
        {
            reason = ex.Message;
            span?.SetTag(MemoryGateTelemetry.Fallback, "cooldown");
            _logger.LogDebug("Memory gate: {Reason}; recall takes the similarity floor.", reason);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            span?.SetTag(MemoryGateTelemetry.Fallback, ex.GetType().Name);
            _logger.LogWarning(ex, "The memory gate failed; recall falls back to the similarity floor.");
        }
        var floor = await _inner.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false);
        return Floor(floor, o, span, "floor (fallback)", reason, watch.ElapsedMilliseconds);
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

    private static string Name(MemoryGateMode mode) => mode switch
    {
        MemoryGateMode.Floor => "floor",
        MemoryGateMode.Judge => "judge",
        MemoryGateMode.Everything => "everything",
        _ => mode.ToString(),
    };

    private static MemoryGateTraceItem Item(GateCandidates.Entry c, double? probability, bool kept) =>
        new(c.ItemId, c.Candidate.MemoryType, c.Candidate.Text, probability, kept);

    /// <summary>The floor's context as it is (every memory it found goes in), marked; a fallback says why.</summary>
    private static MemoryContext Floor(MemoryContext floor, MemoryGateOptions o, Activity? span, string gate, string? reason, long ms)
    {
        List<MemoryGateTraceItem> items = [.. GateCandidates.Of(floor).Select(c => Item(c, null, kept: true))];
        if (reason is null)
            return Mark(floor, o, span, gate, null, items, "", 0);
        if (ReferenceEquals(reason, NoJudge)) span?.SetTag(MemoryGateTelemetry.Fallback, "no-judge");
        return Mark(floor, o, span, gate, reason, items, "", ms, ("gate.reason", reason), ("gate.ms", (int)ms));
    }

    /// <summary>
    /// The context with what happened: the metadata keys, the <see cref="MemoryGateTrace"/> under
    /// <see cref="MemoryGateTrace.MetadataKey"/>, and the span's counts, all counted from the trace's items (a memory in two
    /// sections, due and relevant, is two entries in the prompt and counts twice everywhere).
    /// </summary>
    private static MemoryContext Mark(
        MemoryContext context, MemoryGateOptions o, Activity? span, string gate, string? reason, IReadOnlyList<MemoryGateTraceItem> items,
        string answeredBy, long ms, params (string Key, object Value)[] more)
    {
        var kept = items.Count(i => i.Kept);
        var trace = new MemoryGateTrace(o.Mode, gate, reason, o.Mode == MemoryGateMode.Judge ? o.Threshold : 0, items.Count, kept,
            answeredBy, ms, items);
        if (span is not null)
        {
            span.SetTag(MemoryGateTelemetry.Outcome, gate);
            span.SetTag(MemoryGateTelemetry.Offered, items.Count);
            span.SetTag(MemoryGateTelemetry.Kept, kept);
            if (answeredBy.Length > 0) span.SetTag(MemoryGateTelemetry.JudgedBy, answeredBy);
            if (ms > 0) span.SetTag(MemoryGateTelemetry.JudgeMilliseconds, ms);
        }
        var metadata = new Dictionary<string, object>(context.Metadata, StringComparer.Ordinal)
        {
            ["gate"] = gate,
            ["gate.offered"] = items.Count,
            ["gate.kept"] = kept,
            [MemoryGateTrace.MetadataKey] = trace,
        };
        foreach (var (key, value) in more)
            metadata[key] = value;
        return context with { Metadata = metadata };
    }
}
