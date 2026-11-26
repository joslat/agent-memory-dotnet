using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Gate;

/// <summary>
/// The judge at the fan-in: per memory type, one call to each judge with every memory found, one yes/no question per
/// memory, all types and judges at once; the judges' probabilities blended by weight.
/// </summary>
/// <remarks>
/// <para>
/// The judges have <see cref="MemoryGateOptions.Timeout"/>, and at that deadline the gate keeps whoever has answered: each
/// memory type is blended over the judges that answered it in time (the weights renormalise over them), and when one judge
/// alone answered a type its memories are held to that judge's own <see cref="SystemOneEndpoint.Threshold"/>
/// (<see cref="MemoryGateDecision.Thresholds"/>). A type no judge answered before the deadline is left unanswered as a whole,
/// for the caller to take the similarity floor for that type; when no judge answered anything, the decision fails with a
/// <see cref="TimeoutException"/>. A late judge is named in <see cref="MemoryGateDecision.Late"/> and is not counted as a
/// failure.
/// </para>
/// <para>
/// A judge that fails is left out of the blend for that type; when every judge failed a type, the decision fails as a whole
/// and recall falls back to the similarity floor. A judge that keeps failing is left out without being called for a
/// cool-down (<see cref="MemoryGateOptions.JudgeFailuresBeforeCooldown"/>).
/// </para>
/// </remarks>
public sealed class SystemOneMemoryGate : IMemoryGate
{
    private readonly SystemOneClient _client;
    private readonly IOptions<MemoryGateOptions> _options;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private readonly ILogger<SystemOneMemoryGate> _logger;
    private readonly Lazy<Task<IReadOnlyList<BankTurn>>> _bank;

    internal SystemOneMemoryGate(SystemOneClient client, IOptions<MemoryGateOptions> options, ILogger<SystemOneMemoryGate> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        _client = client;
        _options = options;
        _embeddings = embeddings;
        _logger = logger;
        _bank = new Lazy<Task<IReadOnlyList<BankTurn>>>(LoadBankAsync);
    }

    /// <summary>The judges that can be asked: an endpoint and a share of the blend.</summary>
    internal static IEnumerable<SystemOneEndpoint> UsableJudges(MemoryGateOptions options) =>
        options.Judges.Where(j => j.Endpoint is not null && j.Weight > 0);

    /// <inheritdoc />
    public async Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _options.Value;
        var judges = UsableJudges(options).ToList();
        if (judges.Count == 0)
            throw new InvalidOperationException("The memory gate has no judge configured (MemoryGateOptions.Judges).");
        // The judges' deadline (GATE-DEADLINE-WHOEVER): at it, the gate keeps whoever has answered.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        IReadOnlyList<BankTurn> neighbours;
        try
        {
            neighbours = await NeighboursAsync(request.Turn, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(TimedOut(options));
        }
        var cooling = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var byType = request.Candidates.GroupBy(c => c.MemoryType, StringComparer.Ordinal).ToList();
        var calls = byType.SelectMany(type =>
        {
            var memories = type.Select((c, i) => (Key: $"m{i + 1}", Candidate: c)).ToList();
            var examples = neighbours.Select(n => new GatePrompt.Example(
                n.Turn, n.Types.Contains(type.Key, StringComparer.Ordinal), [.. n.Memories.Take(4)])).ToList();
            var state = GatePrompt.State(request, type.Key, [.. memories.Select(m => (m.Key, m.Candidate.Text))], examples);
            var questions = GatePrompt.Questions(memories.Select(m => m.Key));
            return judges.Select(judge => (Judge: judge, Type: type.Key, Memories: memories,
                Answer: AskOrNullAsync(judge, state, questions, cooling, deadline.Token, cancellationToken)));
        }).ToList();
        await Task.WhenAll(calls.Select(c => c.Answer)).ConfigureAwait(false);

        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        Dictionary<string, double>? thresholds = null;
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var late = new SortedSet<string>(StringComparer.Ordinal);
        var unansweredTypes = new List<string>();
        foreach (var type in calls.GroupBy(c => c.Type, StringComparer.Ordinal))
        {
            late.UnionWith(type.Where(c => c.Answer.Result.Late).Select(c => c.Judge.Name));
            var answers = type.Where(c => c.Answer.Result.Scores is not null).ToList();
            // A memory type made only of companions (module items) that no judge answered stays unanswered: the caller keeps
            // those items within their own bounds, and core memories are judged as without them.
            if (answers.Count == 0 && type.First().Memories.All(m => m.Candidate.Companion)) continue;
            if (answers.Count == 0)
            {
                // The deadline came before any judge answered this type: it is left unanswered as a whole, and the caller
                // takes its fallback for this type only. A type that every judge failed fails the decision, as before.
                if (type.Any(c => c.Answer.Result.Late))
                {
                    unansweredTypes.Add(type.Key);
                    continue;
                }
                throw cooling.Count == judges.Count
                    ? new JudgeCoolingDownException(string.Join("+", cooling.Keys.Order(StringComparer.Ordinal)), cooling.Values.Min())
                    : new InvalidOperationException($"No judge answered for the memory type '{type.Key}'.");
            }
            var weight = answers.Sum(a => a.Judge.Weight);
            // One judge alone answered this type: its memories are held to that judge's own bar, when it has one.
            var ownBar = answers.Count == 1 ? answers[0].Judge.Threshold : null;
            foreach (var (key, candidate) in type.First().Memories)
            {
                probabilities[candidate.Key] = answers.Sum(a => a.Judge.Weight * a.Answer.Result.Scores![key]) / weight;
                if (ownBar is { } bar)
                    (thresholds ??= new Dictionary<string, double>(StringComparer.Ordinal))[candidate.Key] = bar;
            }
            answered.UnionWith(answers.Select(a => a.Judge.Name));
        }
        // Nothing answered: no decision at all, as before (the caller takes its fallback). Late judges make it a timeout.
        if (answered.Count == 0 && request.Candidates.Count > 0)
            throw late.Count > 0
                ? new TimeoutException(TimedOut(options))
                : new InvalidOperationException("No judge answered for any memory type.");
        if (late.Count > 0)
            _logger.LogInformation(
                "Memory gate: {Late} did not answer within {Ms} ms and {Verb} left out; judged by {Answered}{Unanswered}.",
                string.Join("+", late), options.Timeout.TotalMilliseconds, late.Count == 1 ? "is" : "are",
                string.Join("+", answered.Order(StringComparer.Ordinal)),
                unansweredTypes.Count == 0 ? "" : $"; no judge answered {string.Join(", ", unansweredTypes)} in time");
        return new MemoryGateDecision(probabilities, string.Join("+", answered.Order(StringComparer.Ordinal)))
        {
            Thresholds = thresholds,
            Late = [.. late],
        };
    }

    private static string TimedOut(MemoryGateOptions options) =>
        $"the judges did not answer within {options.Timeout.TotalMilliseconds:0} ms";

    /// <summary>
    /// One judge's answer for one memory type: its scores, or null when it failed, was cooling down or was late. Late is a
    /// judge still answering at the deadline: left out of this blend, and not counted as a failure (the client releases it).
    /// The caller's own cancellation is not lateness: it throws.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, double>? Scores, bool Late)> AskOrNullAsync(SystemOneEndpoint judge, object state,
        IReadOnlyDictionary<string, YesNo> questions, System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> cooling,
        CancellationToken deadline, CancellationToken cancellationToken)
    {
        try
        {
            return (await _client.AskAsync(judge, state, questions, _options.Value, deadline).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return (null, true);
        }
        catch (JudgeCoolingDownException ex)
        {
            cooling[judge.Name] = ex.Until;     // not called: the client warned once when it left the judge out
            return (null, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The judge '{Judge}' did not answer; it is left out of this blend.", judge.Name);
            return (null, false);
        }
    }

    // ---- examples: the labelled turns most similar to this one (optional) ----

    private sealed record BankTurn(string Turn, IReadOnlyList<string> Types, IReadOnlyList<string> Memories, float[]? Vector);

    private async Task<IReadOnlyList<BankTurn>> NeighboursAsync(string turn, CancellationToken cancellationToken)
    {
        var count = _options.Value.Examples;
        if (_options.Value.ExamplesPath is null || count <= 0) return [];
        var bank = await _bank.Value.ConfigureAwait(false);
        if (bank.Count == 0 || _embeddings is null || bank[0].Vector is null) return [];
        var query = (await _embeddings.GenerateAsync([turn], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector.ToArray();
        return [.. bank.OrderByDescending(b => Cosine(query, b.Vector!)).Take(count)];
    }

    private async Task<IReadOnlyList<BankTurn>> LoadBankAsync()
    {
        try
        {
            var path = _options.Value.ExamplesPath!;
            var rows = JsonSerializer.Deserialize<List<BankRow>>(await File.ReadAllTextAsync(path).ConfigureAwait(false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            var vectors = _embeddings is null ? null
                : await _embeddings.GenerateAsync(rows.Select(r => r.Turn)).ConfigureAwait(false);
            return [.. rows.Select((r, i) => new BankTurn(r.Turn, r.Types ?? [], r.Memories ?? [], vectors?[i].Vector.ToArray()))];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The gate's examples could not be read; the judge sees none.");
            return [];
        }
    }

    private sealed record BankRow(string Turn, List<string>? Types, List<string>? Memories);

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}
