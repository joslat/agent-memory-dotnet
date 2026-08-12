using Microsoft.ML.Tokenizers;

namespace AgentMemory.LongMemEval;

/// <summary>How a token figure was produced. Never inferred by a reader — always recorded.</summary>
internal enum TokenCountMethod
{
    /// <summary>Four characters per token. Fine for ratios between arms; not a billing figure.</summary>
    CharacterHeuristic = 0,

    /// <summary>A real BPE tokenizer for the configured model.</summary>
    Exact = 1,
}

/// <summary>
/// Exact token counting for figures we intend to <b>publish</b>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LongMemEvalContextSize"/>'s four-characters-per-token estimate is deliberate and stays:
/// the question it answers is whether one arm costs several times another, and a ratio survives a
/// consistent approximation.
/// </para>
/// <para>
/// <b>It stops being sufficient the moment a number leaves this repository.</b> A published token
/// count invites comparison against a provider's bill, and 4-chars-per-token is wrong by a
/// double-digit percentage on real text. Zep's "1.6k context tokens versus 115k" is the most
/// persuasive number in this category precisely because a token count cannot be inflated by a better
/// answer model — which is exactly why ours has to be right before we print it.
/// </para>
/// <para>
/// <b>Fails soft, and says so.</b> If no tokenizer can be constructed for the configured model, this
/// returns the heuristic and reports <see cref="TokenCountMethod.CharacterHeuristic"/> rather than
/// throwing. A missing vocabulary must not fail a benchmark run — but it must also never be silently
/// reported as an exact count, which is why the method travels with every figure.
/// </para>
/// </remarks>
internal sealed class LongMemEvalTokenCounter
{
    private readonly Tokenizer? _tokenizer;

    /// <summary>How every count from this instance was produced.</summary>
    internal TokenCountMethod Method { get; }

    /// <summary>The encoding actually used, or null when falling back to the heuristic.</summary>
    internal string? Encoding { get; }

    internal LongMemEvalTokenCounter(string? modelId)
    {
        // Model ids reaching here are deployment names as often as canonical model names, so an
        // unknown one is expected rather than exceptional -- hence try/catch over a lookup table that
        // would go stale.
        try
        {
            _tokenizer = TiktokenTokenizer.CreateForModel(NormalizeModel(modelId));
            Method = TokenCountMethod.Exact;
            Encoding = NormalizeModel(modelId);
        }
        catch (Exception)
        {
            _tokenizer = null;
            Method = TokenCountMethod.CharacterHeuristic;
            Encoding = null;
        }
    }

    /// <summary>
    /// Maps a deployment name onto a model the tokenizer library knows.
    /// </summary>
    /// <remarks>
    /// Deployment names are operator-chosen ("gpt-4o-eval-2", "judge"), so substring matching is the
    /// only thing that works; anything unrecognised falls through to the heuristic rather than being
    /// silently counted with the wrong vocabulary, which would be worse than not counting at all.
    /// </remarks>
    private static string NormalizeModel(string? modelId)
    {
        var id = (modelId ?? string.Empty).ToLowerInvariant();
        if (id.Contains("gpt-4o", StringComparison.Ordinal) ||
            id.Contains("gpt-5", StringComparison.Ordinal) ||
            id.Contains("o200k", StringComparison.Ordinal))
            return "gpt-4o";
        if (id.Contains("gpt-4", StringComparison.Ordinal) ||
            id.Contains("gpt-35", StringComparison.Ordinal) ||
            id.Contains("gpt-3.5", StringComparison.Ordinal) ||
            id.Contains("cl100k", StringComparison.Ordinal))
            return "gpt-4";
        throw new NotSupportedException($"No known tokenizer vocabulary for model id '{modelId}'.");
    }

    /// <summary>Token count for <paramref name="content"/>, by whichever method this instance uses.</summary>
    internal int Count(string? content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        return _tokenizer is null
            ? LongMemEvalContextSize.Estimate(content)
            : _tokenizer.CountTokens(content);
    }
}
