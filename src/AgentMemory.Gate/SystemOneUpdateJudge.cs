using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Options;

namespace AgentMemory.Gate;

/// <summary>
/// The judge on the write path: one call, one yes/no question per pair, "does the new memory replace this stored one?",
/// asked of the explicitly selected judge (or the first configured endpoint for legacy callers).
/// The write path closes at its own threshold; retrieval blends never select storage policy.
/// </summary>
[Experimental("AMGATE001")]
public sealed class SystemOneUpdateJudge : IMemoryUpdateJudge
{
    private readonly SystemOneClient _client;
    private readonly IOptions<MemoryGateOptions> _options;

    internal SystemOneUpdateJudge(SystemOneClient client, IOptions<MemoryGateOptions> options)
    {
        _client = client;
        _options = options;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.Value.UpdateJudge && Select(_options.Value) is not null;

    private static SystemOneEndpoint? Select(MemoryGateOptions options) => options.UpdateJudgeName is null
        ? options.Judges.FirstOrDefault(j => j.Endpoint is not null)
        : options.Judges.SingleOrDefault(j => j.Endpoint is not null && j.Name == options.UpdateJudgeName);

    internal static readonly (string True, string False) Criteria =
        ("the stored memory stops being true now: it is changed, cancelled or corrected by the new one",
         "both stay true: the stored memory is a past result or earlier event that remains history, or they are about different things");

    /// <summary>
    /// The question for closings a store-aware writer named (<see cref="MemoryUpdateRequest.Named"/>): storage round 4's "v3",
    /// word for word (strategy arena, round4_rejudge.py). On training it confirmed 180 of the writer's 199 right closings where
    /// the question above confirmed 148.
    /// </summary>
    internal static readonly (string True, string False) NamedCriteria =
        ("the stored memory no longer holds as stored: the new one changes its value (a new job, place, commute, count, time or "
         + "plan), corrects it, states the same thing more precisely (a date, a place, a number), cancels it, or reports that a "
         + "planned or ongoing thing has now happened, ended or been called off, or that the person it is about has died, married, "
         + "moved away or had the baby, so a state they were in (where or how they lived, an engagement, an expecting) has ended",
         "both hold as they are: they are about different things, or the stored memory is a dated past event or result that "
         + "stays true as history beside a separate new one (an earlier race, a trip already taken)");

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, double>> JudgeAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _options.Value;
        var judge = options.UpdateJudge ? Select(options) : null;
        if (judge is null) throw new InvalidOperationException("No enabled storage-closing judge is selected.");
        return _client.AskAsync(judge, State(request), Questions(request), options, cancellationToken);
    }

    internal static object State(MemoryUpdateRequest request) => new Dictionary<string, object?>
    {
        ["today"] = GatePrompt.Day(request.Now),
        ["assistant"] = "the person's personal assistant; it keeps long-term memories about them",
        ["conversation"] = request.Said,
        ["already stored (the most similar)"] = request.Pairs.Select(p => p.StoredMemory).Distinct(StringComparer.Ordinal).ToList(),
    };

    internal static IReadOnlyDictionary<string, YesNo> Questions(MemoryUpdateRequest request)
    {
        var (yes, no) = request.Named ? NamedCriteria : Criteria;
        return request.Pairs.ToDictionary(p => p.Key, p => new YesNo(
            $"Does the new memory \"{p.NewMemory}\" replace this stored one: \"{p.StoredMemory}\"?", yes, no),
            StringComparer.Ordinal);
    }
}
