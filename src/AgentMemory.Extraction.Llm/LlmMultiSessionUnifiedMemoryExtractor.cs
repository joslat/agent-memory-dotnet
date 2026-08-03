using System.Text;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.Llm.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Token-bounded multi-session unified extraction. Invalid or partial batch responses are never
/// accepted: a multi-session batch is split recursively, while an invalid single-session response
/// fails the operation.
/// </summary>
internal sealed class LlmMultiSessionUnifiedMemoryExtractor : IMultiSessionUnifiedMemoryExtractor
{
    private const string SystemPrompt =
        """
        You extract structured long-term memory from multiple independent source sessions.
        Return JSON only. Include processed_source_sessions containing every supplied source_session.
        Every entity, fact, preference, and relation must include its source_session.
        Use exactly this shape:
        {"processed_source_sessions":["..."],"entities":[{"source_session":"...","name":"...","type":"PERSON|ORGANIZATION|LOCATION|EVENT|OBJECT","confidence":0.9,"aliases":[]}],"facts":[{"source_session":"...","subject":"...","predicate":"...","object":"...","confidence":0.9}],"preferences":[{"source_session":"...","category":"...","preference":"...","confidence":0.85}],"relations":[{"source_session":"...","source":"...","target":"...","relation_type":"...","confidence":0.8}]}
        Sessions are independent. Never combine facts or entities across source_session values.
        Use empty arrays when a category has no supported memory. Do not emit prose or markdown.
        """;

    private const string UserInstruction =
        "Extract every source session independently and acknowledge all processed source sessions:";

    private readonly IChatClient _chatClient;
    private readonly LlmExtractionOptions _options;
    private readonly ILogger<LlmMultiSessionUnifiedMemoryExtractor> _logger;

    public LlmMultiSessionUnifiedMemoryExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmMultiSessionUnifiedMemoryExtractor> logger)
    {
        _chatClient = chatClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled =>
        _options.UseUnifiedExtraction && _options.UseMultiSessionBatchExtraction;

    public async Task<IReadOnlyDictionary<string, UnifiedExtractionResult>> ExtractAsync(
        IReadOnlyList<ExtractionRequest> requests,
        int maxSessionsPerBatch,
        int maxInputTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputTokens);

        var duplicate = requests.GroupBy(request => request.SessionId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new ArgumentException($"Source session key '{duplicate.Key}' is not unique.", nameof(requests));

        var results = new Dictionary<string, UnifiedExtractionResult>(StringComparer.Ordinal);
        foreach (var batch in PlanBatches(requests, maxSessionsPerBatch, maxInputTokens))
        {
            var extracted = await ExtractOrSplitAsync(batch, maxInputTokens, cancellationToken)
                .ConfigureAwait(false);
            foreach (var pair in extracted)
                results.Add(pair.Key, pair.Value);
        }

        if (results.Count != requests.Count)
            throw new InvalidOperationException(
                $"Multi-session extraction returned {results.Count} sessions for {requests.Count} inputs.");
        return results;
    }

    private async Task<IReadOnlyDictionary<string, UnifiedExtractionResult>> ExtractOrSplitAsync(
        IReadOnlyList<ExtractionRequest> batch,
        int maxInputTokens,
        CancellationToken cancellationToken)
    {
        try
        {
            if (EstimateInputTokens(batch) > maxInputTokens)
                throw new BatchValidationException("Batch exceeds the configured input-token budget.");
            return await ExtractBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (batch.Count > 1)
        {
            _logger.LogWarning(
                ex,
                "Multi-session extraction batch of {Count} did not pass validation; splitting.",
                batch.Count);
            var midpoint = batch.Count / 2;
            var left = await ExtractOrSplitAsync(batch.Take(midpoint).ToArray(), maxInputTokens, cancellationToken)
                .ConfigureAwait(false);
            var right = await ExtractOrSplitAsync(batch.Skip(midpoint).ToArray(), maxInputTokens, cancellationToken)
                .ConfigureAwait(false);
            return left.Concat(right).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
    }

    private async Task<IReadOnlyDictionary<string, UnifiedExtractionResult>> ExtractBatchAsync(
        IReadOnlyList<ExtractionRequest> batch,
        CancellationToken cancellationToken)
    {
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.extract.unified_batch");
        activity?.SetTag("memory.extract.source_sessions", batch.Count);
        var runner = new LlmExtractionRunner(_chatClient, _options, _logger);
        var projected = await runner.RunAsync(
            SystemPrompt,
            UserInstruction,
            BuildBatchText(batch),
            response => new[] { ProjectAndValidate(response, batch) },
            cancellationToken,
            failOnParseExhaustion: true).ConfigureAwait(false);
        return projected.Single();
    }

    private static IReadOnlyDictionary<string, UnifiedExtractionResult> ProjectAndValidate(
        LlmExtractionResponse response,
        IReadOnlyList<ExtractionRequest> batch)
    {
        var expected = batch.Select(request => request.SessionId).ToHashSet(StringComparer.Ordinal);
        var acknowledged = (response.ProcessedSourceSessions ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (!acknowledged.SetEquals(expected) || response.ProcessedSourceSessions!.Count != expected.Count)
            throw new BatchValidationException("Processed-session acknowledgement is incomplete or invalid.");

        var results = expected.ToDictionary(
            key => key,
            _ => new Accumulator(),
            StringComparer.Ordinal);

        foreach (var item in response.Entities ?? [])
        {
            var target = GetAccumulator(results, item.SourceSession);
            if (!string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Type))
                target.Entities.Add(new ExtractedEntity
                {
                    Name = item.Name,
                    Type = NormalizeType(item.Type),
                    Subtype = item.Subtype,
                    Description = item.Description,
                    Confidence = item.Confidence,
                    Aliases = item.Aliases,
                });
        }
        foreach (var item in response.Facts ?? [])
        {
            var target = GetAccumulator(results, item.SourceSession);
            if (!string.IsNullOrWhiteSpace(item.Subject) &&
                !string.IsNullOrWhiteSpace(item.Predicate) &&
                !string.IsNullOrWhiteSpace(item.Object))
                target.Facts.Add(new ExtractedFact
                {
                    Subject = item.Subject,
                    Predicate = item.Predicate,
                    Object = item.Object,
                    Confidence = item.Confidence,
                });
        }
        foreach (var item in response.Preferences ?? [])
        {
            var target = GetAccumulator(results, item.SourceSession);
            if (!string.IsNullOrWhiteSpace(item.Preference))
                target.Preferences.Add(new ExtractedPreference
                {
                    Category = item.Category,
                    PreferenceText = item.Preference,
                    Context = item.Context,
                    Confidence = item.Confidence,
                });
        }
        foreach (var item in response.Relations ?? [])
        {
            var target = GetAccumulator(results, item.SourceSession);
            if (!string.IsNullOrWhiteSpace(item.Source) &&
                !string.IsNullOrWhiteSpace(item.Target) &&
                !string.IsNullOrWhiteSpace(item.RelationType))
                target.Relationships.Add(new ExtractedRelationship
                {
                    SourceEntity = item.Source,
                    TargetEntity = item.Target,
                    RelationshipType = item.RelationType,
                    Description = item.Description,
                    Confidence = item.Confidence,
                });
        }

        return results.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToResult(),
            StringComparer.Ordinal);
    }

    private static Accumulator GetAccumulator(
        IReadOnlyDictionary<string, Accumulator> results,
        string? sourceSession)
    {
        if (string.IsNullOrWhiteSpace(sourceSession) || !results.TryGetValue(sourceSession, out var target))
            throw new BatchValidationException("A learned item has a missing or unknown source-session key.");
        return target;
    }

    private static IReadOnlyList<IReadOnlyList<ExtractionRequest>> PlanBatches(
        IReadOnlyList<ExtractionRequest> requests,
        int maxSessionsPerBatch,
        int maxInputTokens)
    {
        var batches = new List<IReadOnlyList<ExtractionRequest>>();
        var current = new List<ExtractionRequest>();
        foreach (var request in requests)
        {
            if (EstimateInputTokens([request]) > maxInputTokens)
                throw new InvalidOperationException(
                    $"Source session '{request.SessionId}' exceeds the configured input-token budget.");

            var candidate = current.Append(request).ToArray();
            if (current.Count > 0 &&
                (candidate.Length > maxSessionsPerBatch || EstimateInputTokens(candidate) > maxInputTokens))
            {
                batches.Add(current.ToArray());
                current.Clear();
            }
            current.Add(request);
        }
        if (current.Count > 0)
            batches.Add(current.ToArray());
        return batches;
    }

    private static int EstimateInputTokens(IReadOnlyList<ExtractionRequest> batch) =>
        checked(
            Encoding.UTF8.GetByteCount(SystemPrompt) +
            Encoding.UTF8.GetByteCount(UserInstruction) +
            Encoding.UTF8.GetByteCount(BuildBatchText(batch)) +
            35);

    private static string BuildBatchText(IReadOnlyList<ExtractionRequest> batch)
    {
        var builder = new StringBuilder();
        foreach (var request in batch)
        {
            builder.Append("<source_session key=\"").Append(request.SessionId).AppendLine("\">");
            foreach (var message in request.Messages)
            {
                builder.Append('[').Append(message.TimestampUtc.ToString("O")).Append("] ")
                    .Append(message.Role).Append(": ").AppendLine(message.Content);
            }
            builder.AppendLine("</source_session>");
        }
        return builder.ToString();
    }

    private static string NormalizeType(string type) => type.ToUpperInvariant() switch
    {
        "CONCEPT" => "OBJECT",
        "PLACE" => "LOCATION",
        "COMPANY" => "ORGANIZATION",
        "INDIVIDUAL" => "PERSON",
        var value => value,
    };

    private sealed class Accumulator
    {
        public List<ExtractedEntity> Entities { get; } = [];
        public List<ExtractedFact> Facts { get; } = [];
        public List<ExtractedPreference> Preferences { get; } = [];
        public List<ExtractedRelationship> Relationships { get; } = [];

        public UnifiedExtractionResult ToResult() => new()
        {
            Entities = Entities,
            Facts = Facts,
            Preferences = Preferences,
            Relationships = Relationships,
        };
    }

    private sealed class BatchValidationException(string message) : FormatException(message);
}
