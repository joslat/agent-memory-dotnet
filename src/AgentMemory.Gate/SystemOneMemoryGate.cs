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
/// A judge that fails is left out of the blend for that type (the weights renormalise over those that answered); when no
/// judge answers a type, the decision fails as a whole and recall falls back to the similarity floor.
/// </remarks>
[Experimental("AMGATE001")]
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

    /// <inheritdoc />
    public async Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var judges = _options.Value.Judges.Where(j => j.Endpoint is not null && j.Weight > 0).ToList();
        if (judges.Count == 0)
            throw new InvalidOperationException("The memory gate has no judge configured (MemoryGateOptions.Judges).");
        var neighbours = await NeighboursAsync(request.Turn, cancellationToken).ConfigureAwait(false);
        var byType = request.Candidates.GroupBy(c => c.MemoryType, StringComparer.Ordinal).ToList();
        var calls = byType.SelectMany(type =>
        {
            var memories = type.Select((c, i) => (Key: $"m{i + 1}", Candidate: c)).ToList();
            var examples = neighbours.Select(n => new GatePrompt.Example(
                n.Turn, n.Types.Contains(type.Key, StringComparer.Ordinal), [.. n.Memories.Take(4)])).ToList();
            var state = GatePrompt.State(request, type.Key, [.. memories.Select(m => (m.Key, m.Candidate.Text))], examples);
            var questions = GatePrompt.Questions(memories.Select(m => m.Key));
            return judges.Select(judge => (Judge: judge, Type: type.Key, Memories: memories,
                Answer: AskOrNullAsync(judge, state, questions, cancellationToken)));
        }).ToList();
        await Task.WhenAll(calls.Select(c => c.Answer)).ConfigureAwait(false);

        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in calls.GroupBy(c => c.Type, StringComparer.Ordinal))
        {
            var answers = type.Where(c => c.Answer.Result is not null).ToList();
            if (answers.Count == 0)
                throw new InvalidOperationException($"No judge answered for the memory type '{type.Key}'.");
            var weight = answers.Sum(a => a.Judge.Weight);
            foreach (var (key, candidate) in type.First().Memories)
                probabilities[candidate.Key] = answers.Sum(a => a.Judge.Weight * a.Answer.Result![key]) / weight;
            answered.UnionWith(answers.Select(a => a.Judge.Name));
        }
        return new MemoryGateDecision(probabilities, string.Join("+", answered.Order(StringComparer.Ordinal)));
    }

    private async Task<IReadOnlyDictionary<string, double>?> AskOrNullAsync(
        SystemOneEndpoint judge, object state, IReadOnlyDictionary<string, YesNo> questions, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.AskAsync(judge, state, questions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The judge '{Judge}' did not answer; it is left out of this blend.", judge.Name);
            return null;
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
