using System.Text.RegularExpressions;

namespace AgentMemory.Core.Security;

/// <summary>
/// Lightweight, deterministic detector for content that resembles an instruction directed at the model,
/// rather than stored factual/relational information (#92 Phase 2). Deliberately NOT a complete
/// prompt-injection detector (see issue #92's non-goals) -- its purpose is to flag the most common,
/// unambiguous phrasings so an adapter's admission logic can quote or exclude them; applications wanting
/// stronger detection should implement their own equivalent.
/// </summary>
/// <remarks>
/// Every alternative here is a fixed phrase (with small, non-repeating optional single-word groups) --
/// deliberately NOT a repeating group with nested quantifiers, which is vulnerable to catastrophic
/// backtracking on adversarial input (a lesson learned building a similar detector for #88). Lives in Core
/// (not the Agent Framework adapter, where it originated in #92 Phase 2) so both the Agent Framework and
/// Semantic Kernel adapters can share one implementation (#92 Phase 6) rather than each carrying its own
/// copy of security-sensitive matching logic -- <c>InternalsVisibleTo</c> already grants both adapters
/// visibility into Core's internals.
/// </remarks>
internal static class InstructionLikeContentDetector
{
    // No trailing \b: several alternatives end in punctuation ("new instructions:") or would otherwise
    // need it selectively, and a boundary is already enforced at the START of the match, which is what
    // matters for these fixed lead-in phrases -- a missing trailing boundary only risks classifying a
    // LONGER phrase as instruction-like too (e.g. "instructionsfoo"), never a false negative.
    // Every group is non-capturing (?:...) -- only IsMatch is ever called, no group value is read, so
    // there is nothing to gain from the capture bookkeeping the regex engine would otherwise track on
    // every attempt.
    private static readonly Regex Pattern = new(
        @"\b(?:ignore (?:all |any )?(?:previous|prior|above) instructions" +
        @"|disregard (?:all |any )?(?:previous|prior|above) instructions" +
        @"|forget (?:all |your |any )?(?:previous|prior) instructions" +
        @"|you are now (?:a|an|the) " +
        @"|system prompt" +
        @"|new instructions:" +
        @"|reveal (?:all |every )?(?:secrets|credentials|customer records?|customer data|api keys?)" +
        @"|call this tool" +
        @"|execute this command" +
        @"|do not tell the user" +
        @"|override the (?:policy|system prompt|instructions))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsMatch(string? content) => !string.IsNullOrEmpty(content) && Pattern.IsMatch(content);
}
