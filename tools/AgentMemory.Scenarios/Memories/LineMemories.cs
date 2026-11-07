using Microsoft.Extensions.AI;

namespace AgentMemory.Scenarios.Memories;

/// <summary>
/// The whole contract of an agent memory, reduced to its two moments: hear something, and later bring back what
/// matters for a question. From Pablo Piovano's three-memories-one-question (MIT).
/// </summary>
internal interface IMemory : IAsyncDisposable
{
    string Name { get; }

    /// <summary>One line on how this memory chooses what goes into the prompt.</summary>
    string How { get; }

    Task RememberAsync(Turn turn, CancellationToken cancellationToken = default);

    /// <summary>The text this memory would put in the prompt for <paramref name="question"/>.</summary>
    /// <param name="budgetTokens">How much room it gets. <c>null</c> lets the memory decide.</param>
    Task<string> RecallAsync(string question, int? budgetTokens, CancellationToken cancellationToken = default);
}

/// <summary>
/// A memory that keeps what it heard as dated lines and, when asked, orders them and takes lines from the top until the
/// budget runs out. A notes file and a vector store differ only in the order. (Pablo Piovano, MIT.)
/// </summary>
internal abstract class LineMemory : IMemory
{
    protected List<string> Lines { get; } = [];

    public abstract string Name { get; }

    public abstract string How { get; }

    public virtual Task RememberAsync(Turn turn, CancellationToken cancellationToken = default)
    {
        Lines.Add($"- [{turn.At:yyyy-MM-dd}] {turn.Text}");
        return Task.CompletedTask;
    }

    /// <summary>All the lines, best first, for this question.</summary>
    protected abstract Task<IReadOnlyList<string>> RankAsync(string question, CancellationToken cancellationToken);

    public async Task<string> RecallAsync(string question, int? budgetTokens, CancellationToken cancellationToken = default)
    {
        var ranked = await RankAsync(question, cancellationToken).ConfigureAwait(false);
        return string.Join('\n', Fit(ranked, budgetTokens ?? int.MaxValue));
    }

    /// <summary>The smallest budget at which every needed fact is in the prompt, or null when one never is.</summary>
    public async Task<int?> SmallestBudgetThatWorksAsync(Question question, CancellationToken cancellationToken = default)
    {
        var ranked = await RankAsync(question.Text, cancellationToken).ConfigureAwait(false);
        for (var count = 1; count <= ranked.Count; count++)
        {
            var prompt = string.Join('\n', ranked.Take(count));
            if (Checks.Needs(question, prompt).All(need => need.Found)) return Checks.Tokens(prompt);
        }

        return null;
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<string> Fit(IReadOnlyList<string> ranked, int budgetTokens)
    {
        var used = 0;
        foreach (var line in ranked)
        {
            used += Checks.Tokens(line + "\n");
            if (used > budgetTokens) yield break;   // the cut: whatever is below it never reaches the model
            yield return line;
        }
    }
}

/// <summary>The notes file: every statement appended as a line, loaded from the top until the room runs out.</summary>
internal sealed class FileMemory : LineMemory
{
    public override string Name => "file";

    public override string How => "a notes file, loaded from the top until the budget runs out";

    protected override Task<IReadOnlyList<string>> RankAsync(string question, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Lines);   // the order it was written in; the question changes nothing
}

/// <summary>
/// The vector store: every statement becomes an embedding, and the prompt gets the statements most similar to the
/// question. It finds a paraphrase; it cannot find what is related to the question without resembling it.
/// </summary>
internal sealed class VectorMemory(IEmbeddingGenerator<string, Embedding<float>> embeddings) : LineMemory
{
    private readonly List<ReadOnlyMemory<float>> _vectors = [];

    public override string Name => "vector";

    public override string How => "embeddings, most similar to the question first";

    public override async Task RememberAsync(Turn turn, CancellationToken cancellationToken = default)
    {
        // The vector first: a failed embedding then leaves the lines and the vectors as they were, still aligned.
        var embedded = await embeddings.GenerateAsync([turn.Text], cancellationToken: cancellationToken).ConfigureAwait(false);
        await base.RememberAsync(turn, cancellationToken).ConfigureAwait(false);
        _vectors.Add(embedded[0].Vector);
    }

    protected override async Task<IReadOnlyList<string>> RankAsync(string question, CancellationToken cancellationToken)
    {
        var asked = (await embeddings.GenerateAsync([question], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector;
        return Lines
            .Select((line, index) => (line, score: Cosine(asked.Span, _vectors[index].Span)))
            .OrderByDescending(scored => scored.score)
            .Select(scored => scored.line)
            .ToList();
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, lengthA = 0, lengthB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            lengthA += a[i] * a[i];
            lengthB += b[i] * b[i];
        }

        return dot / (Math.Sqrt(lengthA) * Math.Sqrt(lengthB));
    }
}
