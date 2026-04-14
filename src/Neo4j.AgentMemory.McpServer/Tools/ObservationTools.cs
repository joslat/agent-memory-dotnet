using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer.Tools;

/// <summary>
/// Token-budget-aware observation retrieval tool for MCP.
/// </summary>
[McpServerToolType]
public sealed class ObservationTools
{
    [McpServerTool(Name = "memory_get_observations"), Description("Get compressed observations from memory within a token budget. Retrieves recent context and compresses it to fit the specified token limit.")]
    public static async Task<string> MemoryGetObservations(
        IShortTermMemoryService shortTermMemory,
        IContextCompressor compressor,
        IOptions<McpServerOptions> options,
        [Description("Session ID to retrieve observations for")] string? sessionId = null,
        [Description("Maximum tokens for the response")] int maxTokens = 4000,
        [Description("Include entity observations")] bool includeEntities = true,
        [Description("Include fact observations")] bool includeFacts = true,
        [Description("Include preference observations")] bool includePreferences = true,
        CancellationToken cancellationToken = default)
    {
        var sid = sessionId ?? options.Value.DefaultSessionId;

        var messages = await shortTermMemory.GetRecentMessagesAsync(sid, limit: 100, cancellationToken);

        if (messages.Count == 0)
        {
            return ToolJsonContext.Serialize(new
            {
                sessionId = sid,
                wasCompressed = false,
                observations = Array.Empty<string>(),
                reflections = Array.Empty<string>(),
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

        var compressed = await compressor.CompressAsync(messages, compressionOptions, cancellationToken);

        var observations = new List<string>();
        if (compressed.WasCompressed)
        {
            observations.AddRange(compressed.Observations);
        }

        var result = new StringBuilder();
        result.AppendLine($"## Memory Observations for session '{sid}'");
        result.AppendLine();

        if (compressed.Reflections.Count > 0)
        {
            result.AppendLine("### Reflections");
            foreach (var reflection in compressed.Reflections)
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
            observations,
            reflections = compressed.Reflections,
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
