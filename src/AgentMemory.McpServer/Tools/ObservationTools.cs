using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer.Tools;
using AgentMemory.McpServer.Security;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// Token-budget-aware observation retrieval tool for MCP.
/// </summary>
[McpServerToolType]
internal sealed class ObservationTools
{
    [McpServerTool(Name = "memory_get_observations"), Description("Get compressed observations from memory within a token budget. Retrieves recent context and compresses it to fit the specified token limit." + McpRecallGuard.DescriptionSuffix)]
    public static async Task<string> MemoryGetObservations(
        IShortTermMemoryService shortTermMemory,
        IContextCompressor compressor,
        IOptions<AgentMemoryMcpOptions> options,
        [Description("Session ID to retrieve observations for")] string? sessionId = null,
        [Description("Maximum tokens for the response")] int maxTokens = 4000,
        [Description("Include entity observations")] bool includeEntities = true,
        [Description("Include fact observations")] bool includeFacts = true,
        [Description("Include preference observations")] bool includePreferences = true,
        CancellationToken cancellationToken = default)
    {
        var sid = sessionId ?? options.Value.DefaultSessionId;
        maxTokens = Math.Clamp(maxTokens, 256, 100_000); // a non-positive/absurd budget would break compression

        var messages = await shortTermMemory.GetRecentMessagesAsync(sid, limit: 100, cancellationToken).ConfigureAwait(false);

        if (messages.Count == 0)
        {
            return ToolJsonContext.Serialize(new
            {
                untrustedContent = McpRecallGuard.Notice,
                sessionId = sid,
                wasCompressed = false,
                observations = Array.Empty<string>(),
                reflections = Array.Empty<string>(),
                instructionLike = false,
                recentMessageCount = 0,
                originalTokenCount = 0,
                compressedTokenCount = 0,
                includedSections = BuildIncludedSections(includeEntities, includeFacts, includePreferences)
            });
        }

        var compressionOptions = new ContextCompressionOptions
        {
            TokenThreshold = maxTokens,
            RecentMessageCount = Math.Min(10, messages.Count),
            MaxObservations = 5,
            EnableReflections = true
        };

        var compressed = await compressor.CompressAsync(messages, compressionOptions, cancellationToken).ConfigureAwait(false);

        // Observations and reflections are compressed from recalled messages, so they are guarded like every
        // other recalled text (#92, the MCP surface); a withheld line keeps its place as "[withheld]".
        var guard = McpRecallGuard.From(options.Value);
        var observationVerdicts = (compressed.WasCompressed ? compressed.Observations : (IEnumerable<string>)Array.Empty<string>())
            .Select(o => (Text: o, Verdict: guard.Check(o, MemoryTrustLevel.Untrusted))).ToList();
        var reflectionVerdicts = compressed.Reflections.Select(r => (Text: r, Verdict: guard.Check(r, MemoryTrustLevel.Untrusted))).ToList();
        var observations = observationVerdicts.Select(x => x.Verdict.Text(x.Text) ?? "[withheld]").ToList();
        var reflections = reflectionVerdicts.Select(x => x.Verdict.Text(x.Text) ?? "[withheld]").ToList();

        var result = new StringBuilder();
        result.AppendLine($"## Memory Observations for session '{sid}'");
        result.AppendLine();

        if (reflections.Count > 0)
        {
            result.AppendLine("### Reflections");
            foreach (var reflection in reflections)
                result.AppendLine($"- {reflection}");
            result.AppendLine();
        }

        if (observations.Count > 0)
        {
            result.AppendLine("### Observations");
            foreach (var observation in observations)
                result.AppendLine($"- {observation}");
            result.AppendLine();
        }

        result.AppendLine($"### Summary");
        result.AppendLine($"- Recent messages kept: {compressed.RecentMessages.Count}");
        result.AppendLine($"- Original tokens: {compressed.OriginalTokenCount}");
        result.AppendLine($"- Compressed tokens: {compressed.CompressedTokenCount}");
        result.AppendLine($"- Sections included: {string.Join(", ", BuildIncludedSections(includeEntities, includeFacts, includePreferences))}");

        return ToolJsonContext.Serialize(new
        {
            sessionId = sid,
            wasCompressed = compressed.WasCompressed,
            untrustedContent = McpRecallGuard.Notice,
            observations,
            reflections,
            instructionLike = observationVerdicts.Any(x => x.Verdict.InstructionLike) || reflectionVerdicts.Any(x => x.Verdict.InstructionLike),
            recentMessageCount = compressed.RecentMessages.Count,
            originalTokenCount = compressed.OriginalTokenCount,
            compressedTokenCount = compressed.CompressedTokenCount,
            includedSections = BuildIncludedSections(includeEntities, includeFacts, includePreferences),
            formattedSummary = result.ToString()
        });
    }

    private static List<string> BuildIncludedSections(bool entities, bool facts, bool preferences)
    {
        var sections = new List<string>();
        if (entities) sections.Add("entities");
        if (facts) sections.Add("facts");
        if (preferences) sections.Add("preferences");
        return sections;
    }
}
