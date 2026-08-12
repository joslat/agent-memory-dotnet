using System.Globalization;
using System.Text;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.Llm.Internal;
using AgentMemory.Core.Memory;
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

    /// <summary>
    /// The system prompt, with the established relation vocabulary offered when one is supplied.
    /// </summary>
    /// <remarks>
    /// Extraction invents a predicate per sentence when nothing tells it which relations exist —
    /// measured at 700 facts under 421 distinct predicates, with a single birth expressed as
    /// "was born", "was born in", "were born in", "had" and "welcomed", which left counting
    /// questions unanswerable even once a relation could be retrieved whole. Reconciling phrasings
    /// afterwards cannot be done safely, since "bought" and "sold" are one similarity threshold
    /// apart, so the vocabulary is applied at generation instead.
    /// <para>
    /// The extractor is told to <b>prefer</b> these relations, never to be limited to them: a model
    /// restricted to a fixed list would drop facts that genuinely need a new relation. An empty
    /// vocabulary yields the original prompt byte-for-byte, so callers that do not use this are
    /// unaffected — including the frozen batch plan, whose estimated input totals depend on prompt
    /// size.
    /// </para>
    /// </remarks>
    internal static string BuildSystemPrompt(
        MemoryPredicateVocabulary? vocabulary,
        AssistantContentMode assistantContent = AssistantContentMode.Ignore,
        TemporalValidityMode temporalValidity = TemporalValidityMode.Ignore,
        ExtractionProvenanceMode provenance = ExtractionProvenanceMode.Batch)
    {
        // Every shared instruction, appended in the same order every rung uses. A setting honoured by
        // only some extractors is worse than no setting - it makes behaviour depend on a performance
        // flag - and this rung was the one my first pass missed.
        var assistant = ExtractionPromptSemantics.AssistantContentInstruction(assistantContent)
            + ExtractionPromptSemantics.TemporalValidityInstruction(temporalValidity)
            + ExtractionPromptSemantics.ProvenanceInstruction(provenance);
        var established = vocabulary?.Snapshot() ?? [];
        if (established.Count == 0)
            return SystemPrompt + assistant;

        return SystemPrompt +
            "\nEstablished relation predicates, in order of preference: " +
            string.Join(", ", established) +
            ".\nReuse an established predicate whenever it fits; introduce a new one only when none does." +
            assistant;
    }

    private const string UserInstruction =
        "Extract every source session independently and acknowledge all processed source sessions:";

    /// <summary>The vocabulary offered to the model, or null when the option is off.</summary>
    /// <remarks>
    /// Built from the curated seed on each use rather than cached, so the size the plan estimates
    /// and the string actually sent can never diverge — an estimate taken from a different prompt
    /// than the request would corrupt the frozen plan's token accounting silently.
    /// </remarks>
    private MemoryPredicateVocabulary? ActiveVocabulary =>
        _options.UsePredicateVocabulary ? MemoryPredicateSeedVocabulary.Create() : null;

    private readonly IChatClient _chatClient;
    private readonly LlmExtractionOptions _options;
    private readonly ILogger<LlmMultiSessionUnifiedMemoryExtractor> _logger;
    private readonly LlmExtractionBatchConcurrencyLimiter? _concurrencyLimiter;
    private readonly LlmExtractionBatchDiagnostics? _batchDiagnostics;

    public LlmMultiSessionUnifiedMemoryExtractor(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        ILogger<LlmMultiSessionUnifiedMemoryExtractor> logger,
        LlmExtractionBatchConcurrencyLimiter? concurrencyLimiter = null,
        LlmExtractionBatchDiagnostics? batchDiagnostics = null)
    {
        _chatClient = chatClient;
        _options = options.Value;
        _logger = logger;
        _concurrencyLimiter = concurrencyLimiter;
        _batchDiagnostics = batchDiagnostics;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            _options.MaxConcurrentBatchesPerExtraction);
    }

    public bool IsEnabled =>
        _options.UseUnifiedExtraction && _options.UseMultiSessionBatchExtraction;

    public MultiSessionExtractionPlan Plan(
        IReadOnlyList<ExtractionRequest> requests,
        int maxSessionsPerBatch,
        int maxInputTokens)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputTokens);

        var duplicate = requests.GroupBy(request => request.SessionId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new ArgumentException($"Source session key '{duplicate.Key}' is not unique.", nameof(requests));

        var batches = PlanBatches(requests, maxSessionsPerBatch, maxInputTokens)
            .Select(batch => new MultiSessionExtractionBatchPlan(
                batch.Select(request => request.SessionId).ToArray(),
                EstimateInputTokens(batch)))
            .ToArray();
        return new MultiSessionExtractionPlan(batches);
    }

    public async Task<IReadOnlyDictionary<string, UnifiedExtractionResult>> ExtractAsync(
        IReadOnlyList<ExtractionRequest> requests,
        int maxSessionsPerBatch,
        int maxInputTokens,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(requests, maxSessionsPerBatch, maxInputTokens);
        var requestsBySession = requests.ToDictionary(
            request => request.SessionId,
            StringComparer.Ordinal);
        var extractedByBatch =
            new IReadOnlyDictionary<string, UnifiedExtractionResult>?[plan.BatchCount];
        var concurrency = Math.Min(
            _options.MaxConcurrentBatchesPerExtraction,
            plan.BatchCount);

        if (concurrency <= 1)
        {
            for (var index = 0; index < plan.BatchCount; index++)
            {
                extractedByBatch[index] = await ExtractPlannedBatchAsync(
                        plan.Batches[index],
                        requestsBySession,
                        maxInputTokens,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, plan.BatchCount),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = cancellationToken
                },
                async (index, itemCancellationToken) =>
                {
                    extractedByBatch[index] = await ExtractPlannedBatchAsync(
                            plan.Batches[index],
                            requestsBySession,
                            maxInputTokens,
                            itemCancellationToken)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        var unordered = new Dictionary<string, UnifiedExtractionResult>(StringComparer.Ordinal);
        foreach (var extracted in extractedByBatch)
        {
            if (extracted is null)
                throw new InvalidOperationException(
                    "Multi-session extraction did not complete every planned batch.");
            foreach (var pair in extracted)
                unordered.Add(pair.Key, pair.Value);
        }

        if (unordered.Count != requests.Count)
            throw new InvalidOperationException(
                $"Multi-session extraction returned {unordered.Count} sessions for {requests.Count} inputs.");

        var results = new Dictionary<string, UnifiedExtractionResult>(StringComparer.Ordinal);
        foreach (var request in requests)
            results.Add(request.SessionId, unordered[request.SessionId]);
        return results;
    }

    private async Task<IReadOnlyDictionary<string, UnifiedExtractionResult>>
        ExtractPlannedBatchAsync(
            MultiSessionExtractionBatchPlan plannedBatch,
            IReadOnlyDictionary<string, ExtractionRequest> requestsBySession,
            int maxInputTokens,
            CancellationToken cancellationToken)
    {
        var batch = plannedBatch.SourceSessionIds
            .Select(sessionId => requestsBySession[sessionId])
            .ToArray();
        return await ExtractOrSplitAsync(
                batch,
                maxInputTokens,
                cancellationToken)
            .ConfigureAwait(false);
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
        // FormatException only, deliberately. Every failure the batch's own shape causes arrives as
        // one - BatchValidationException derives from it, covering the token budget, the
        // acknowledgement check and the source-session-key check, as does an unparseable response -
        // and halving the batch is a real remedy for each. A provider transport failure is not that:
        // re-sending each half puts the same request shape at the same endpoint that just failed, so
        // the split neither diagnoses nor fixes it, and it doubles the call count. That broke a
        // 37-minute n=50 preparation at question 20 ("observed 14 calls ... expected exactly 12")
        // over one ClientResultException.
        //
        // NOTE, verified rather than assumed: there is currently NO transport retry on this path.
        // LlmExtractionRunner honours MaxRetries, but only by re-prompting on a parse failure - its
        // GetResponseAsync call sits outside any catch, so a transport exception propagates
        // immediately. Splitting was therefore the only thing resembling a retry for transports, and
        // it was a bad one: it re-sent to the endpoint that had just failed and doubled the call
        // count. Removing it does not remove a working recovery; it removes a misleading one. A real
        // transport retry is tracked separately, because it must be reconciled with the harness's
        // exact-call-count invariant rather than quietly breaking it.
        // The provider refused this content and will refuse it again, at any batch size. Skipping the
        // affected sessions costs their extraction; propagating costs the ENTIRE preparation, which
        // for 50 questions is ~616 calls and over an hour. Recorded, never silent: the count reaches
        // the manifest, so a corpus with gaps says so instead of looking complete.
        catch (Exception ex) when (IsContentRejection(ex))
        {
            _batchDiagnostics?.RecordContentRejection(ex, batch.Count);
            _logger.LogWarning(
                ex,
                "Provider refused the content of {Count} source session(s); their extraction is "
                + "skipped and recorded. The corpus will be missing whatever those sessions held.",
                batch.Count);
            return batch.ToDictionary(
                request => request.SessionId,
                _ => new UnifiedExtractionResult(),
                StringComparer.Ordinal);
        }
        catch (Exception ex) when (batch.Count > 1 && IsBatchShapeFailure(ex))
        {
            _batchDiagnostics?.RecordSplit(ex, batch.Count);
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
        var estimatedInputTokens = EstimateInputTokens(batch);
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.extract.unified_batch");
        activity?.SetTag("memory.extract.source_sessions", batch.Count);
        activity?.SetTag("memory.extract.estimated_input_tokens", estimatedInputTokens);
        var runner = new LlmExtractionRunner(_chatClient, _options, _logger);

        Task<IReadOnlyList<IReadOnlyDictionary<string, UnifiedExtractionResult>>> RunProviderAsync() =>
            runner.RunAsync(
                BuildSystemPrompt(
                    ActiveVocabulary, _options.AssistantContent, _options.TemporalValidity,
                    _options.Provenance),
                UserInstruction,
                BuildBatchText(batch, _options.Provenance),
                response => new[] { ProjectAndValidate(response, batch) },
                cancellationToken,
                failOnParseExhaustion: true,
                responseFormat: LlmMultiSessionExtractionResponseContract.CreateResponseFormat(batch.Count));

        var projected = _concurrencyLimiter is null
            ? await RunProviderAsync().ConfigureAwait(false)
            : await _concurrencyLimiter.RunAsync(RunProviderAsync, cancellationToken)
                .ConfigureAwait(false);
        return projected.Single();
    }

    private static IReadOnlyDictionary<string, UnifiedExtractionResult> ProjectAndValidate(
        LlmExtractionResponse response,
        IReadOnlyList<ExtractionRequest> batch)
    {
        var sourceSessions = batch.Select((request, index) =>
            new BatchSourceSession(
                LlmMultiSessionExtractionResponseContract.Alias(index), request)).ToArray();
        var expected = sourceSessions.Select(item => item.Alias).ToHashSet(StringComparer.Ordinal);
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
                    // This rung asks for valid_from/valid_until whenever TemporalValidityMode.Extract is
                    // set -- the instruction is shared -- but used to drop both on the floor here, so the
                    // setting was silently a no-op under multi-session extraction. That is precisely the
                    // "a setting only some extractors respect" defect ExtractionPromptSemantics exists to
                    // prevent, arriving through the projection instead of the prompt.
                    ValidFrom = item.ValidFrom,
                    ValidUntil = item.ValidUntil,
                    SourceRole = item.SourceRole,
                    SourceTurn = item.SourceTurn,
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
                    SourceRole = item.SourceRole,
                    SourceTurn = item.SourceTurn,
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

        return sourceSessions.ToDictionary(
            item => item.Request.SessionId,
            item => results[item.Alias].ToResult(),
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

    private IReadOnlyList<IReadOnlyList<ExtractionRequest>> PlanBatches(
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

    private int EstimateInputTokens(IReadOnlyList<ExtractionRequest> batch) =>
        checked(
            // Token accounting must see the SAME prompt the call will send, or the frozen batch plan
            // under-estimates by exactly the instruction it forgot.
            Encoding.UTF8.GetByteCount(BuildSystemPrompt(
                ActiveVocabulary, _options.AssistantContent, _options.TemporalValidity,
                _options.Provenance)) +
            Encoding.UTF8.GetByteCount(UserInstruction) +
            Encoding.UTF8.GetByteCount(BuildBatchText(batch, _options.Provenance)) +
            35);

    private static string BuildBatchText(
        IReadOnlyList<ExtractionRequest> batch,
        ExtractionProvenanceMode provenance = ExtractionProvenanceMode.Batch)
    {
        var numbered = provenance == ExtractionProvenanceMode.PerItem;
        var builder = new StringBuilder();
        for (var index = 0; index < batch.Count; index++)
        {
            var request = batch[index];
            builder.Append("<source_session key=\"")
                .Append(LlmMultiSessionExtractionResponseContract.Alias(index)).AppendLine("\">");
            for (var turn = 0; turn < request.Messages.Count; turn++)
            {
                var message = request.Messages[turn];
                // Numbered WITHIN each source session, restarting at 1. A batch-global number would be
                // unresolvable: results are demultiplexed back per session, and each session's own
                // source-message ids are what a turn has to index into.
                if (numbered)
                    builder.Append('[')
                        .Append((turn + 1).ToString(CultureInfo.InvariantCulture))
                        .Append("] ");
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
    private sealed record BatchSourceSession(
        string Alias,
        ExtractionRequest Request);


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


    /// <summary>
    /// Whether a failure is caused by the batch's own shape, and so is worth splitting for.
    /// </summary>
    /// <remarks>
    /// Two families qualify. <see cref="FormatException"/> covers the validation and parse failures
    /// this class raises itself. A <b>permanent 4xx</b> qualifies too, and missing it cost a full
    /// 60-minute preparation: the provider rejected oversized batches with HTTP 400, the splitter
    /// had been narrowed to FormatException only so it declined to help, and the transport retry
    /// re-sent each rejected request until the watchdog fired.
    /// <para>
    /// 408 and 429 are excluded deliberately — they are transient and belong to the retry policy, and
    /// splitting on a rate limit would answer congestion by sending more requests.
    /// </para>
    /// </remarks>
    internal static bool IsBatchShapeFailure(Exception exception)
    {
        if (exception is FormatException)
            return true;

        // A content-filter rejection is a 4xx, but it is NOT a shape failure: the request is
        // well-formed and the provider is refusing the content. Splitting re-sends the same text to
        // the same filter, so it can only fail again, one session at a time, until the batch reaches
        // size 1 and the exception escapes.
        if (IsContentRejection(exception)) return false;

        var status = Internal.LlmExtractionRunner.TryGetStatus(exception);
        return status is >= 400 and < 500 and not 408 and not 429;
    }

    /// <summary>
    /// Whether the provider refused this content outright, rather than failing to process it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terminal by nature. A retry sends the same text to the same policy, and a split sends smaller
    /// pieces of the same text; neither can succeed. Measured cost of treating it as retryable: a
    /// 50-question preparation died after <b>270 of 616</b> calls because two sessions of a public
    /// research dataset tripped an Azure content policy.
    /// </para>
    /// <para>
    /// Matched on the provider's own vocabulary rather than on status alone, because 400 covers both
    /// "your request is malformed" (which splitting legitimately diagnoses) and "I will not process
    /// this" (which it cannot).
    /// </para>
    /// </remarks>
    internal static bool IsContentRejection(Exception exception)
    {
        var status = Internal.LlmExtractionRunner.TryGetStatus(exception);
        if (status != 400) return false;

        var message = exception.Message;
        return message.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("content filter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cyber_policy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ResponsibleAIPolicyViolation", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BatchValidationException(string message) : FormatException(message);
}
