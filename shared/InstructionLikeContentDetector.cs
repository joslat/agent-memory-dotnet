using System.Text.RegularExpressions;

// One source compiled into Core (the in-process adapters' admission) and the MCP server (its recall tools), as
// RecordFrame.cs is shared: one detection rule, one decision, on every surface that hands recalled memory to a model.
#if AGENTMEMORY_CORE
namespace AgentMemory.Core.Security;
#elif AGENTMEMORY_MCPSERVER_NAMS
namespace AgentMemory.McpServer.Nams.Security;
#else
namespace AgentMemory.McpServer.Security;
#endif

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
/// backtracking on adversarial input (a lesson learned building a similar detector for #88). One shared
/// source (not the Agent Framework adapter, where it originated in #92 Phase 2), compiled into Core, which the
/// Agent Framework and Semantic Kernel adapters reach through <c>InternalsVisibleTo</c> (#92 Phase 6), and into
/// the MCP server, which references only the abstractions: one copy of security-sensitive matching logic.
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
