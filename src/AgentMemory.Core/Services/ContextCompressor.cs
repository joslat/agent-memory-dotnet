using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Compresses conversation context using a 3-tier strategy:
/// tier 1 (reflections) → tier 2 (observations) → tier 3 (recent messages verbatim).
/// </summary>
internal sealed class ContextCompressor : IContextCompressor
{
    private const int CharsPerToken = 4;

    private readonly IChatClient _chatClient;
    private readonly ILogger<ContextCompressor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextCompressor"/> class.
    /// </summary>
    /// <param name="chatClient">The chat client used to generate observation summaries and reflections.</param>
    /// <param name="logger">The logger used to record compression diagnostics.</param>
    public ContextCompressor(
        IChatClient chatClient,
        ILogger<ContextCompressor> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public int EstimateTokenCount(IReadOnlyList<Message> messages) =>
        messages.Sum(m => m.Content.Length) / CharsPerToken;

    /// <inheritdoc/>
    public async Task<CompressedContext> CompressAsync(
        IReadOnlyList<Message> messages,
        ContextCompressionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return new CompressedContext
            {
                WasCompressed = false,
                OriginalTokenCount = 0,
                CompressedTokenCount = 0
            };
        }

        int originalTokenCount = EstimateTokenCount(messages);

        if (originalTokenCount <= options.TokenThreshold)
        {
            return new CompressedContext
            {
                RecentMessages = messages,
                WasCompressed = false,
                OriginalTokenCount = originalTokenCount,
                CompressedTokenCount = originalTokenCount
            };
        }

        _logger.LogDebug(
            "Context compression triggered: {Tokens} tokens exceeds threshold {Threshold}",
            originalTokenCount, options.TokenThreshold);

        // Tier 3: keep the most recent messages verbatim, summarize the older ones. This requires
        // chronological (oldest-first) order, but callers may pass newest-first (GetRecentMessagesAsync /
        // recall order DESC). Normalize by timestamp first (R6-D) — otherwise Skip(count - recentCount)
        // kept the OLDEST verbatim and Take(...) fed the NEWEST turns to summarization, the exact inversion
        // of the documented behavior.
        var chronological = messages.OrderBy(m => m.TimestampUtc).ToList();
        var recentCount = Math.Min(options.RecentMessageCount, chronological.Count);
        var recentMessages = chronological.Skip(chronological.Count - recentCount).ToList();
        var olderMessages = chronological.Take(chronological.Count - recentCount).ToList();

        // Tier 2: summarize older messages in chunks into observations
        var observations = new List<string>();
        if (olderMessages.Count > 0)
        {
            int chunkSize = Math.Max(1, (int)Math.Ceiling((double)olderMessages.Count / options.MaxObservations));
            var chunks = olderMessages
                .Select((msg, idx) => (msg, idx))
                .GroupBy(x => x.idx / chunkSize)
                .Select(g => g.Select(x => x.msg).ToList())
                .ToList();

            foreach (var chunk in chunks)
            {
                var observation = await SummarizeChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(observation))
                    observations.Add(observation);
            }
        }

        // Tier 1: summarize observations into high-level reflections
        var reflections = new List<string>();
        if (options.EnableReflections && observations.Count > 0)
        {
            var reflection = await GenerateReflectionAsync(observations, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reflection))
                reflections.Add(reflection);
        }

        int compressedTokenCount = EstimateTokenCount(recentMessages)
            + (observations.Sum(o => o.Length) / CharsPerToken)
            + (reflections.Sum(r => r.Length) / CharsPerToken);

        _logger.LogDebug(
            "Context compressed: {Original} → {Compressed} tokens ({Observations} observations, {Reflections} reflections)",
            originalTokenCount, compressedTokenCount, observations.Count, reflections.Count);

        return new CompressedContext
        {
            Reflections = reflections,
            Observations = observations,
            RecentMessages = recentMessages,
            WasCompressed = true,
            OriginalTokenCount = originalTokenCount,
            CompressedTokenCount = compressedTokenCount
        };
    }

    private async Task<string> SummarizeChunkAsync(
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken)
    {
        var conversationText = string.Join("\n", messages.Select(m => $"{m.Role}: {m.Content}"));

        try
        {
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You are a conversation summarizer. Produce a concise 1-2 sentence observation " +
                    "capturing the key points from this conversation excerpt. Return only the summary text."),
                new(ChatRole.User, conversationText)
            };

            var response = await _chatClient.GetResponseAsync(chatMessages, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Text?.Trim() ?? string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancellation must propagate, not be masked as fallback summary text
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to summarize conversation chunk; using fallback.");
            return $"[Summary of {messages.Count} messages]";
        }
    }

    private async Task<string> GenerateReflectionAsync(
        IReadOnlyList<string> observations,
        CancellationToken cancellationToken)
    {
        var observationText = string.Join("\n", observations.Select((o, i) => $"{i + 1}. {o}"));

        try
        {
            var chatMessages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You are a reflective reasoning assistant. Given a list of conversation observations, " +
                    "produce a single high-level reflection that captures the overall themes and key context. " +
                    "Return only the reflection text."),
                new(ChatRole.User, $"Observations:\n{observationText}")
            };

            var response = await _chatClient.GetResponseAsync(chatMessages, cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Text?.Trim() ?? string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancellation must propagate, not be masked as an empty reflection
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate reflection; skipping.");
            return string.Empty;
        }
    }
}
