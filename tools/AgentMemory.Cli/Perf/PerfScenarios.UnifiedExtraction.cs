using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Cli.Perf;

public static partial class PerfScenarios
{
    internal const string UnifiedExtractionProbeMessage =
        "LAB-U1 source: Alice Martin works at Acme Corporation and prefers concise written summaries.";

    internal const string UnifiedExtractionPayload =
        """
        {
          "entities": [
            {"name":"Acme Corporation","type":"ORGANIZATION","confidence":0.92,"aliases":[]},
            {"name":"Alice Martin","type":"PERSON","confidence":0.95,"aliases":[]}
          ],
          "facts": [
            {"subject":"Alice Martin","predicate":"works_at","object":"Acme Corporation","confidence":0.90},
            {"subject":"Alice Martin","predicate":"leads","object":"platform team","confidence":0.85}
          ],
          "preferences": [
            {"category":"communication","preference":"prefers concise written summaries","confidence":0.88}
          ],
          "relations": [
            {"source":"Alice Martin","target":"Acme Corporation","relation_type":"WORKS_AT","confidence":0.90}
          ]
        }
        """;

    /// <summary>
    /// PERF-W-09 — isolates one typed unified extraction call over the same shape as PERF-W-07.
    /// Storage, resolution, embeddings, persistence, recall, answer, and judge remain excluded.
    /// </summary>
    private static async Task ExtractUnifiedOnlyAsync(ScenarioContext context)
    {
        IReadOnlyList<Message> messages =
        [
            new Message
            {
                MessageId = "perf-w09-source-00",
                ConversationId = "perf-w09-conversation",
                SessionId = "perf-w09-session",
                Role = "user",
                Content = UnifiedExtractionProbeMessage,
                TimestampUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            },
        ];

        var extractor = context.Profile.Services.GetRequiredService<IUnifiedMemoryExtractor>();
        using var activity = new Activity("lab.extraction.unified").Start();
        var startedAt = Stopwatch.GetTimestamp();
        UnifiedExtractionResult result;
        try
        {
            result = await extractor.ExtractAsync(messages, context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            context.Turn.RecordSpan(
                "lab.extractor.unified",
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }

        context.Turn.Add("extract.input_messages", messages.Count);
        context.Turn.Add("extract.entities", result.Entities.Count);
        context.Turn.Add("extract.facts", result.Facts.Count);
        context.Turn.Add("extract.preferences", result.Preferences.Count);
        context.Turn.Add("extract.relationships", result.Relationships.Count);

        var calls = context.Turn.Counter("llm.unified.calls");
        context.Turn.Add("llm.unified.retries", Math.Max(0, calls - 1));
        var outputsExact =
            result.Entities.Count == 2 &&
            result.Entities[0].Name == "Acme Corporation" &&
            result.Entities[1].Name == "Alice Martin" &&
            result.Facts.Count == 2 &&
            result.Facts[0].Predicate == "works_at" &&
            result.Facts[1].Predicate == "leads" &&
            result.Preferences.Count == 1 &&
            result.Preferences[0].Category == "communication" &&
            result.Relationships.Count == 1 &&
            result.Relationships[0].RelationshipType == "WORKS_AT";
        var purposeMetricsExact =
            calls == 1 &&
            context.Turn.Counter("llm.unified.tokens_in") > 0 &&
            context.Turn.Counter("llm.unified.tokens_out") > 0 &&
            context.Turn.SpanCounts.GetValueOrDefault("provider.llm.unified") == 1 &&
            context.Turn.SpanCounts.GetValueOrDefault("lab.extractor.unified") == 1;
        var excludedWork =
            context.Turn.Counter("embed.requests") +
            context.Turn.Counter("embed.items") +
            context.Turn.Counter("neo4j.queries") +
            context.Turn.Counter("neo4j.tx.read") +
            context.Turn.Counter("neo4j.tx.write") +
            context.Turn.Counter("store.messages") +
            context.Turn.Counter("persist.entities") +
            context.Turn.Counter("persist.facts") +
            context.Turn.Counter("persist.preferences") +
            context.Turn.Counter("persist.relationships") +
            context.Turn.Counter("items.retrieved");

        if (context.Turn.Counter("llm.calls") != 1 ||
            !purposeMetricsExact ||
            !outputsExact ||
            excludedWork != 0)
        {
            throw new InvalidOperationException(
                $"PERF-W-09 unified extraction contract failed (llm.calls=" +
                $"{context.Turn.Counter("llm.calls")}/1, purpose_metrics_exact=" +
                $"{purposeMetricsExact}, outputs={result.Entities.Count}/{result.Facts.Count}/" +
                $"{result.Preferences.Count}/{result.Relationships.Count}, expected 2/2/1/1, " +
                $"excluded_work={excludedWork}/0). This arm must measure one non-empty typed call " +
                "without storage, resolution, embedding, persistence, recall, answer, or judge work.");
        }
    }
}
