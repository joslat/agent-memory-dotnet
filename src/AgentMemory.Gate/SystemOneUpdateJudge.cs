using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Options;

namespace AgentMemory.Gate;

/// <summary>
/// The judge on the write path: one call, one yes/no question per pair, "does the new memory replace this stored one?",
/// asked of the first configured judge (JEV, as measured). The write path closes at its own threshold.
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
    public bool IsEnabled => _options.Value.UpdateJudge && _options.Value.Judges.Any(j => j.Endpoint is not null);

    internal static readonly (string True, string False) Criteria =
        ("the stored memory is no longer true once the new one is stored", "both stay true, or they are about different things");

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, double>> JudgeAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var judge = _options.Value.Judges.First(j => j.Endpoint is not null);
        return _client.AskAsync(judge, State(request), Questions(request), cancellationToken);
    }

    internal static object State(MemoryUpdateRequest request) => new Dictionary<string, object?>
    {
        ["today"] = GatePrompt.Day(request.Now),
        ["assistant"] = "the person's personal assistant; it keeps long-term memories about them",
        ["conversation"] = request.Said,
        ["already stored (the most similar)"] = request.Pairs.Select(p => p.StoredMemory).Distinct(StringComparer.Ordinal).ToList(),
    };

    internal static IReadOnlyDictionary<string, YesNo> Questions(MemoryUpdateRequest request) =>
        request.Pairs.ToDictionary(p => p.Key, p => new YesNo(
            $"Does the new memory \"{p.NewMemory}\" replace this stored one: \"{p.StoredMemory}\"?", Criteria.True, Criteria.False),
            StringComparer.Ordinal);
}
