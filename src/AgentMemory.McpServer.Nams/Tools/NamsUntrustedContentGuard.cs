using System.Text.RegularExpressions;

namespace AgentMemory.McpServer.Nams.Tools;

/// <summary>
/// Local, dependency-free mirror of Core's <c>RecalledMemoryDelimiter</c>/<c>InstructionLikeContentDetector</c>
/// (<c>src/AgentMemory.Core/Security/</c>), for the one place in this package that surfaces raw, potentially
/// untrusted content: <see cref="NamsReasoningReadTools.NamsReasoningTrace"/>'s tool-call Input/Output.
/// <c>AgentMemory.McpServer.Nams</c> has no Core/AgentFramework reference (by design), so it can't reuse
/// those internal types directly -- this replicates their algorithms at a reduced scope: delimiting +
/// instruction-like flagging only, not the full #92 admission-policy/trust-level pipeline. That fuller
/// pipeline is deliberately NOT built here -- unlike the automatic recall pipeline (which injects recalled
/// content into every turn), a model only sees this content if it or a developer deliberately calls this
/// introspection tool, a narrower and more deliberate exposure surface.
/// </summary>
internal static class NamsUntrustedContentGuard
{
    // Same fixed-phrase, non-capturing-group pattern as Core's InstructionLikeContentDetector (see that
    // type's own comment for why: avoids catastrophic backtracking on adversarial input, a lesson from #88).
    private static readonly Regex InstructionLikePattern = new(
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

    /// <summary>
    /// Escapes angle brackets and wraps in a delimiter boundary, defeating tag/boundary forgery (the same
    /// principle as Core's <c>RecalledMemoryDelimiter</c>) -- does NOT detect or block plain-language
    /// instruction-like content; see <see cref="IsInstructionLike"/> for that, used as an advisory flag
    /// alongside the delimited content, never as a silent redaction.
    /// </summary>
    public static string? Delimit(string? content) =>
        content is null
            ? null
            : $"""<untrusted_tool_content>{content.Replace("<", "&lt;").Replace(">", "&gt;")}</untrusted_tool_content>""";

    public static bool IsInstructionLike(string? content) =>
        !string.IsNullOrEmpty(content) && InstructionLikePattern.IsMatch(content);
}
