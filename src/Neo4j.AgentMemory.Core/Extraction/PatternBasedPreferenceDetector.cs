using System.Text.RegularExpressions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction;

/// <summary>
/// Fast regex-based preference extractor that detects common preference patterns
/// across 7 categories without requiring an LLM call.
/// Use this as a first-pass filter before falling back to <see cref="IPreferenceExtractor"/> LLM implementations.
/// </summary>
public sealed class PatternBasedPreferenceDetector : IPreferenceExtractor
{
    // Confidence levels
    private const double StrongPatternConfidence = 0.95;
    private const double RegexMatchConfidence = 0.85;

    private static readonly IReadOnlyList<PatternRule> Rules = BuildRules();

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return Task.FromResult<IReadOnlyList<ExtractedPreference>>(Array.Empty<ExtractedPreference>());

        var results = new List<ExtractedPreference>();

        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            foreach (var rule in Rules)
            {
                var matches = rule.Pattern.Matches(message.Content);
                foreach (Match match in matches)
                {
                    results.Add(new ExtractedPreference
                    {
                        Category = rule.Category,
                        PreferenceText = match.Value.Trim(),
                        Context = $"Detected via pattern in {message.Role} message",
                        Confidence = rule.IsStrong ? StrongPatternConfidence : RegexMatchConfidence
                    });
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ExtractedPreference>>(results);
    }

    private static List<PatternRule> BuildRules()
    {
        const RegexOptions opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

        return
        [
            // ── Communication style ──────────────────────────────────────────────
            new PatternRule("communication_style",
                new Regex(@"\bI\s+(?:prefer|like)\s+(?:to\s+communicate|when\s+you|responses\s+that)[^.!?]*[.!?]?", opts),
                IsStrong: true),

            new PatternRule("communication_style",
                new Regex(@"\bplease\s+always\s+[^.!?]+[.!?]?", opts),
                IsStrong: true),

            new PatternRule("communication_style",
                new Regex(@"\bI\s+prefer\s+(?:concise|brief|detailed|short|long|clear|direct)[^.!?]*[.!?]?", opts),
                IsStrong: true),

            // ── Format preferences ───────────────────────────────────────────────
            new PatternRule("format",
                new Regex(@"\b(?:use|I\s+want|I\s+prefer)\s+(?:bullet\s+points?|numbered\s+lists?|markdown)[^.!?]*[.!?]?", opts),
                IsStrong: false),

            new PatternRule("format",
                new Regex(@"\bI\s+(?:want|prefer|like)\s+(?:JSON|XML|YAML|CSV|plain\s+text|code\s+examples?)[^.!?]*[.!?]?", opts),
                IsStrong: false),

            new PatternRule("format",
                new Regex(@"\balways\s+include\s+(?:code\s+examples?|examples?|snippets?|output)[^.!?]*[.!?]?", opts),
                IsStrong: false),

            new PatternRule("format",
                new Regex(@"\bformat\s+(?:your\s+)?(?:responses?|answers?|output)\s+(?:as|with|using)\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),

            // ── Tool preferences ─────────────────────────────────────────────────
            new PatternRule("tool_preference",
                new Regex(@"\bI\s+prefer\s+using\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),

            new PatternRule("tool_preference",
                new Regex(@"\bdon'?t\s+(?:use|suggest|recommend)\s+[^.!?]+[.!?]?", opts),
                IsStrong: true),

            new PatternRule("tool_preference",
                new Regex(@"\balways\s+suggest\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),

            new PatternRule("tool_preference",
                new Regex(@"\bI\s+(?:use|rely\s+on|work\s+with)\s+(?:[A-Z][a-zA-Z0-9]+(?:\s+[A-Z][a-zA-Z0-9]+)*)[^.!?]*[.!?]?", opts),
                IsStrong: false),

            // ── Language / tone ──────────────────────────────────────────────────
            new PatternRule("language_tone",
                new Regex(@"\bkeep\s+it\s+(?:formal|informal|casual|professional|simple|technical)[^.!?]*[.!?]?", opts),
                IsStrong: true),

            new PatternRule("language_tone",
                new Regex(@"\bbe\s+(?:casual|formal|concise|direct|thorough|friendly|professional)[^.!?]*[.!?]?", opts),
                IsStrong: false),

            new PatternRule("language_tone",
                new Regex(@"\buse\s+(?:technical|simple|layman'?s?|plain|academic)\s+language[^.!?]*[.!?]?", opts),
                IsStrong: false),

            new PatternRule("language_tone",
                new Regex(@"\bI\s+prefer\s+(?:a\s+)?(?:formal|casual|professional|friendly|informal)\s+tone[^.!?]*[.!?]?", opts),
                IsStrong: true),

            // ── Time / scheduling ────────────────────────────────────────────────
            new PatternRule("time_scheduling",
                new Regex(@"\bI\s+work\s+(?:in|from|during|at)\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),

            new PatternRule("time_scheduling",
                new Regex(@"\bmy\s+(?:time\s*zone|timezone)\s+is\s+[^.!?]+[.!?]?", opts),
                IsStrong: true),

            new PatternRule("time_scheduling",
                new Regex(@"\bI'?m\s+available\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),

            new PatternRule("time_scheduling",
                new Regex(@"\bI\s+(?:usually|typically|normally)\s+(?:work|start|finish|meet)[^.!?]*[.!?]?", opts),
                IsStrong: false),

            // ── Privacy / sharing ────────────────────────────────────────────────
            new PatternRule("privacy",
                new Regex(@"\bdon'?t\s+share\s+[^.!?]+[.!?]?", opts),
                IsStrong: true),

            new PatternRule("privacy",
                new Regex(@"\bkeep\s+[^.!?]*?\bprivate\b[^.!?]*[.!?]?", opts),
                IsStrong: true),

            new PatternRule("privacy",
                new Regex(@"\byou\s+can\s+share\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),

            new PatternRule("privacy",
                new Regex(@"\bdo\s+not\s+(?:share|disclose|reveal|store)\s+[^.!?]+[.!?]?", opts),
                IsStrong: true),

            // ── Content preferences ──────────────────────────────────────────────
            new PatternRule("content",
                new Regex(@"\bI'?m\s+interested\s+in\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),

            new PatternRule("content",
                new Regex(@"\bskip\s+(?:the\s+)?[^.!?]+[.!?]?", opts),
                IsStrong: false),

            new PatternRule("content",
                new Regex(@"\bfocus\s+on\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),

            new PatternRule("content",
                new Regex(@"\bI\s+(?:don'?t\s+care\s+about|prefer\s+not\s+to\s+see|want\s+to\s+avoid)\s+[^.!?]+[.!?]?", opts),
                IsStrong: false),
        ];
    }

    private sealed record PatternRule(string Category, Regex Pattern, bool IsStrong);
}

