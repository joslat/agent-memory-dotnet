using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neo4j.Driver;

namespace AgentMemory.LongMemEval;

internal sealed record LongMemEvalPreparedQuestion(
    int QuestionNumber,
    string QuestionId,
    string HistorySha256,
    string ScopeSha256,
    int MessagesPrepared,
    int SourceSessions,
    int ExtractionUnitsPrepared,
    LongMemEvalGraphSnapshot GraphSnapshot);

internal sealed record LongMemEvalPreparationManifest(
    int SchemaVersion,
    string PreparationId,
    string DatasetSha256,
    string AgentEvalRevision,
    string ScopeRunIdSha256,
    string AnswerModelId,
    string JudgeModelId,
    string ExtractionModelId,
    string EmbeddingModelId,
    int EmbeddingDimensions,
    int MaxRelevantMessages,
    string ExtractionSourceTime,
    string ExtractionResponseContract,
    bool UseJsonResponseFormat,
    bool UseUnifiedExtraction,
    bool UseMultiSessionBatchExtraction,
    int PreparationWorkers,
    int MaxSessionsPerBatch,
    int MaxInputTokens,
    int MaxConcurrentBatchesPerExtraction,
    int MaxConcurrentExtractionBatches,
    IReadOnlyList<LongMemEvalPreparedQuestion> Questions,
    long InitialExtractionCalls,
    string Fingerprint,
    // ── Ingestion identity (schema 6) ────────────────────────────────────
    // Everything below changes WHAT WAS STORED, and none of it was recorded. A volume built with
    // AssistantContent=Utterance could be adopted by a run configured for Ignore, and the report's
    // fingerprint would describe the run's configuration while the graph came from the other one.
    // Recorded here so a reuse can be refused instead of quietly measuring the wrong corpus.
    string AssistantContent = "Ignore",
    bool UsePredicateVocabulary = false,
    string ExtractionVocabularySha256 = "",
    string QueryRelationLexiconSha256 = "",
    string ExtractionProvenance = "Batch",
    // ── Catalog metadata (schema 6) ──────────────────────────────────────
    // Not part of the fingerprint: these describe the build for a human, and two corpora that differ
    // only in their description are the same corpus.
    string PreparedAtUtc = "",
    string Description = "",
    IReadOnlyList<string>? MemoryTypes = null,
    int QuestionSeed = 0)
{
    public const int CurrentSchemaVersion = 6;

    internal int MessagesPrepared => Questions.Sum(question => question.MessagesPrepared);

    internal int ExtractionUnitsPrepared =>
        Questions.Sum(question => question.ExtractionUnitsPrepared);

    internal static LongMemEvalPreparationManifest Create(
        string preparationId,
        string datasetSha256,
        string agentEvalRevision,
        string scopeRunId,
        string answerModelId,
        string judgeModelId,
        string extractionModelId,
        string embeddingModelId,
        int embeddingDimensions,
        int maxRelevantMessages,
        string extractionSourceTime,
        IReadOnlyList<LongMemEvalPreparedQuestion> questions,
        long initialExtractionCalls,
        bool useJsonResponseFormat = true,
        string extractionResponseContract = "json-object",
        bool useUnifiedExtraction = false,
        bool useMultiSessionBatchExtraction = false,
        int preparationWorkers = 1,
        int maxSessionsPerBatch = 1,
        int maxInputTokens = 100_000,
        int maxConcurrentBatchesPerExtraction = 1,
        int maxConcurrentExtractionBatches = 0,
        string assistantContent = "Ignore",
        bool usePredicateVocabulary = false,
        string extractionVocabularySha256 = "",
        string queryRelationLexiconSha256 = "",
        string extractionProvenance = "Batch",
        string preparedAtUtc = "",
        string description = "",
        IReadOnlyList<string>? memoryTypes = null,
        int questionSeed = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentEvalRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(answerModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(judgeModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(embeddingDimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRelevantMessages);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionSourceTime);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionResponseContract);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentOutOfRangeException.ThrowIfNegative(initialExtractionCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preparationWorkers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionsPerBatch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maxConcurrentBatchesPerExtraction);
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcurrentExtractionBatches);
        if (useMultiSessionBatchExtraction && !useUnifiedExtraction)
        {
            throw new ArgumentException(
                "Multi-session extraction requires unified extraction.");
        }

        var materialized = questions.ToArray();
        if (materialized.Length == 0)
            throw new ArgumentException("A preparation manifest requires at least one question.", nameof(questions));
        if (materialized.Select(question => question.QuestionNumber).Distinct().Count() != materialized.Length ||
            materialized.Select(question => question.QuestionId).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new ArgumentException(
                "A preparation manifest requires unique question numbers and ids.",
                nameof(questions));
        }

        var manifest = new LongMemEvalPreparationManifest(
            CurrentSchemaVersion,
            preparationId,
            datasetSha256,
            agentEvalRevision,
            Hash(scopeRunId),
            answerModelId,
            judgeModelId,
            extractionModelId,
            embeddingModelId,
            embeddingDimensions,
            maxRelevantMessages,
            extractionSourceTime,
            extractionResponseContract,
            useJsonResponseFormat,
            useUnifiedExtraction,
            useMultiSessionBatchExtraction,
            preparationWorkers,
            maxSessionsPerBatch,
            maxInputTokens,
            maxConcurrentBatchesPerExtraction,
            maxConcurrentExtractionBatches,
            materialized,
            initialExtractionCalls,
            Fingerprint: string.Empty,
            AssistantContent: assistantContent,
            UsePredicateVocabulary: usePredicateVocabulary,
            ExtractionVocabularySha256: extractionVocabularySha256,
            QueryRelationLexiconSha256: queryRelationLexiconSha256,
            ExtractionProvenance: extractionProvenance,
            PreparedAtUtc: preparedAtUtc,
            Description: description,
            MemoryTypes: memoryTypes ?? [],
            QuestionSeed: questionSeed);
        return manifest with { Fingerprint = ComputeFingerprint(manifest) };
    }

    internal void VerifyIntegrity()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported LongMemEval preparation manifest schema {SchemaVersion}.");
        }

        var expected = ComputeFingerprint(this);
        if (!string.Equals(Fingerprint, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("LongMemEval preparation manifest fingerprint mismatch.");
    }

    internal static string ComputeFingerprint(LongMemEvalPreparationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.PreparationId,
            manifest.DatasetSha256,
            manifest.AgentEvalRevision,
            manifest.ScopeRunIdSha256,
            manifest.AnswerModelId,
            manifest.JudgeModelId,
            manifest.ExtractionModelId,
            manifest.EmbeddingModelId,
            manifest.EmbeddingDimensions,
            manifest.MaxRelevantMessages,
            manifest.ExtractionSourceTime,
            // Schema 6. These five decide what the extractor was asked for and therefore what the
            // graph contains; leaving them out of the fingerprint is what let two materially different
            // corpora hash identically.
            manifest.AssistantContent,
            manifest.UsePredicateVocabulary,
            manifest.ExtractionVocabularySha256,
            manifest.QueryRelationLexiconSha256,
            manifest.ExtractionProvenance,
            manifest.QuestionSeed,
            manifest.UseJsonResponseFormat,
            manifest.ExtractionResponseContract,
            manifest.UseUnifiedExtraction,
            manifest.UseMultiSessionBatchExtraction,
            manifest.PreparationWorkers,
            manifest.MaxSessionsPerBatch,
            manifest.MaxInputTokens,
            manifest.MaxConcurrentBatchesPerExtraction,
            manifest.MaxConcurrentExtractionBatches,
            Questions = manifest.Questions.Select(question => new
            {
                question.QuestionNumber,
                question.QuestionId,
                question.HistorySha256,
                question.ScopeSha256,
                question.MessagesPrepared,
                question.SourceSessions,
                question.ExtractionUnitsPrepared,
                question.GraphSnapshot
            }),
            manifest.InitialExtractionCalls
        };
        return Hash(JsonSerializer.Serialize(canonical, JsonOptions));
    }

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

internal sealed record LongMemEvalPreparationExpectation(
    string DatasetSha256,
    string AgentEvalRevision,
    string AnswerModelId,
    string JudgeModelId,
    string ExtractionModelId,
    string EmbeddingModelId,
    int EmbeddingDimensions,
    int MaxRelevantMessages,
    string ExtractionSourceTime,
    bool UseJsonResponseFormat = true,
    string ExtractionResponseContract = "json-object",
    bool UseUnifiedExtraction = false,
    bool UseMultiSessionBatchExtraction = false,
    int PreparationWorkers = 1,
    int MaxSessionsPerBatch = 1,
    int MaxInputTokens = 100_000,
    int MaxConcurrentBatchesPerExtraction = 1,
    int MaxConcurrentExtractionBatches = 0)
{
    internal void Validate(LongMemEvalPreparationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.DatasetSha256, DatasetSha256, StringComparison.Ordinal) ||
            !string.Equals(manifest.AgentEvalRevision, AgentEvalRevision, StringComparison.Ordinal) ||
            !string.Equals(manifest.AnswerModelId, AnswerModelId, StringComparison.Ordinal) ||
            !string.Equals(manifest.JudgeModelId, JudgeModelId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ExtractionModelId, ExtractionModelId, StringComparison.Ordinal) ||
            !string.Equals(manifest.EmbeddingModelId, EmbeddingModelId, StringComparison.Ordinal) ||
            manifest.EmbeddingDimensions != EmbeddingDimensions ||
            manifest.MaxRelevantMessages != MaxRelevantMessages ||
            manifest.UseJsonResponseFormat != UseJsonResponseFormat ||
            !string.Equals(manifest.ExtractionResponseContract, ExtractionResponseContract, StringComparison.Ordinal) ||
            !string.Equals(manifest.ExtractionSourceTime, ExtractionSourceTime, StringComparison.Ordinal) ||
            manifest.UseUnifiedExtraction != UseUnifiedExtraction ||
            manifest.UseMultiSessionBatchExtraction != UseMultiSessionBatchExtraction ||
            manifest.PreparationWorkers != PreparationWorkers ||
            manifest.MaxSessionsPerBatch != MaxSessionsPerBatch ||
            manifest.MaxInputTokens != MaxInputTokens ||
            manifest.MaxConcurrentBatchesPerExtraction != MaxConcurrentBatchesPerExtraction ||
            manifest.MaxConcurrentExtractionBatches != MaxConcurrentExtractionBatches)
        {
            throw new InvalidOperationException(
                "Prepared LongMemEval configuration does not match the sealed manifest.");
        }
    }
}

internal static class LongMemEvalPreparationFingerprint
{
    internal static LongMemEvalPreparationExpectation Expect(
        string datasetSha256,
        string agentEvalRevision,
        string answerModelId,
        string judgeModelId,
        string extractionModelId,
        string embeddingModelId,
        int embeddingDimensions,
        int maxRelevantMessages,
        bool useJsonResponseFormat = true,
        string extractionResponseContract = "json-object",
        bool useUnifiedExtraction = false,
        bool useMultiSessionBatchExtraction = false,
        int preparationWorkers = 1,
        int maxSessionsPerBatch = 1,
        int maxInputTokens = 100_000,
        int maxConcurrentBatchesPerExtraction = 1,
        int maxConcurrentExtractionBatches = 0) =>
        new(
            datasetSha256,
            agentEvalRevision,
            answerModelId,
            judgeModelId,
            extractionModelId,
            embeddingModelId,
            embeddingDimensions,
            maxRelevantMessages,
            "metadata-only-not-in-extraction-prompt",
            useJsonResponseFormat,
            extractionResponseContract,
            useUnifiedExtraction,
            useMultiSessionBatchExtraction,
            preparationWorkers,
            maxSessionsPerBatch,
            maxInputTokens,
            maxConcurrentBatchesPerExtraction,
            maxConcurrentExtractionBatches);
}
public sealed class LongMemEvalPreparedState
{
    private readonly IReadOnlyDictionary<int, LongMemEvalPreparedQuestion> _byNumber;

    internal LongMemEvalPreparedState(
        LongMemEvalPreparationManifest manifest,
        string scopeRunId)
        : this(manifest, scopeRunId, expectation: null)
    {
    }

    internal LongMemEvalPreparedState(
        LongMemEvalPreparationManifest manifest,
        string scopeRunId,
        LongMemEvalPreparationExpectation? expectation)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRunId);
        manifest.VerifyIntegrity();
        if (!string.Equals(
                manifest.ScopeRunIdSha256,
                LongMemEvalPreparationManifest.Hash(scopeRunId),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Prepared LongMemEval scope does not match the sealed manifest.");
        }

        expectation?.Validate(manifest);
        Manifest = manifest;
        _byNumber = manifest.Questions.ToDictionary(question => question.QuestionNumber);
    }

    internal LongMemEvalPreparationManifest Manifest { get; }

    internal LongMemEvalPreparedQuestion ValidateQuestion(
        int questionNumber,
        LongMemEvalEvidenceQuestion evidenceQuestion,
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        string sessionId,
        string ownerId)
    {
        ArgumentNullException.ThrowIfNull(evidenceQuestion);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        if (!_byNumber.TryGetValue(questionNumber, out var prepared))
        {
            throw new InvalidOperationException(
                $"Prepared LongMemEval manifest has no question position {questionNumber}.");
        }

        var historySha256 = LongMemEvalEvidenceIndex.Fingerprint(history);
        var scopeSha256 = LongMemEvalPreparationManifest.Hash($"{sessionId}|{ownerId}");
        var sourceSessions = evidenceQuestion.Messages
            .Where(message =>
                !message.IsSyntheticBoundary &&
                !message.IsSyntheticFormatterPadding)
            .Select(message => message.SourceSessionOrdinal)
            .Distinct()
            .Count();
        // G3B.9 stopped persisting AgentEval's fabricated session-boundary turns, so the sealed
        // count is of real conversation only. Comparing it against every injected message would
        // reject every question. The guard stays exact — it is the expectation that was stale.
        var persistableMessages = evidenceQuestion.Messages
            .Count(message =>
                !message.IsSyntheticBoundary &&
                !message.IsSyntheticFormatterPadding);

        if (!string.Equals(prepared.QuestionId, evidenceQuestion.QuestionId, StringComparison.Ordinal) ||
            !string.Equals(prepared.HistorySha256, historySha256, StringComparison.Ordinal) ||
            !string.Equals(prepared.ScopeSha256, scopeSha256, StringComparison.Ordinal) ||
            prepared.MessagesPrepared != persistableMessages ||
            prepared.SourceSessions != sourceSessions ||
            prepared.ExtractionUnitsPrepared != sourceSessions)
        {
            throw new InvalidOperationException(
                $"Prepared LongMemEval question {questionNumber} does not match the sealed manifest.");
        }

        return prepared;
    }
}

internal sealed class Neo4jLongMemEvalPreparationStore(IDriver driver)
{
    private const string Label = "LongMemEvalPreparation";

    internal async Task SealAsync(
        LongMemEvalPreparationManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.VerifyIntegrity();
        var json = JsonSerializer.Serialize(
            manifest,
            LongMemEvalPreparationManifest.JsonOptions);

        await using var session = driver.AsyncSession(
            options => options.WithDefaultAccessMode(AccessMode.Write));
        await session.ExecuteWriteAsync(async transaction =>
        {
            var existingCursor = await transaction.RunAsync(
                $"MATCH (m:{Label} {{id: $id}}) RETURN count(m) AS count",
                new { id = manifest.PreparationId }).ConfigureAwait(false);
            var existing = await existingCursor.SingleAsync().ConfigureAwait(false);
            if (existing["count"].As<long>() != 0)
            {
                throw new InvalidOperationException(
                    "LongMemEval preparation id is already sealed.");
            }

            var createCursor = await transaction.RunAsync(
                $$"""
                CREATE (m:{{Label}} {
                    id: $id,
                    schema_version: $schemaVersion,
                    fingerprint: $fingerprint,
                    manifest_json: $manifestJson,
                    sealed_at: datetime()
                })
                RETURN m.fingerprint AS fingerprint
                """,
                new
                {
                    id = manifest.PreparationId,
                    schemaVersion = manifest.SchemaVersion,
                    fingerprint = manifest.Fingerprint,
                    manifestJson = json
                }).ConfigureAwait(false);
            var created = await createCursor.SingleAsync().ConfigureAwait(false);
            if (!string.Equals(
                    created["fingerprint"].As<string>(),
                    manifest.Fingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "LongMemEval preparation manifest was not sealed exactly.");
            }
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The preparation id sealed into this store, so an adopted volume describes itself.
    /// </summary>
    /// <remarks>
    /// G3B.12-R. Reuse only receives a volume name; the run identity it needs to reproduce session
    /// and owner scopes lives inside the graph. Reading it back is what lets a retained build be
    /// evaluated without a rebuild — and every question would otherwise trip
    /// <c>prepared-manifest-mismatch</c>, since scope hashes are derived from that id.
    /// <para>
    /// Exactly one manifest per store is required: more than one means volumes were mixed, which
    /// would silently evaluate one graph against another's sealed expectations.
    /// </para>
    /// </remarks>
    internal async Task<string> ReadSealedPreparationIdAsync(
        CancellationToken cancellationToken = default)
    {
        await using var session = driver.AsyncSession(
            options => options.WithDefaultAccessMode(AccessMode.Read));
        var ids = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync($"MATCH (m:{Label}) RETURN m.id AS id");
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(record => record["id"].As<string>()).ToList();
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return ids.Count switch
        {
            1 => ids[0],
            0 => throw new InvalidOperationException(
                "The reused volume holds no sealed LongMemEval preparation; it was never prepared, " +
                "or preparation did not complete."),
            _ => throw new InvalidOperationException(
                $"The reused volume holds {ids.Count} sealed preparations; exactly one is required.")
        };
    }

    internal async Task<LongMemEvalPreparationManifest> ReadAsync(
        string preparationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        await using var session = driver.AsyncSession(
            options => options.WithDefaultAccessMode(AccessMode.Read));
        var records = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                $$"""
                MATCH (m:{{Label}} {id: $id})
                RETURN m.schema_version AS schemaVersion,
                       m.fingerprint AS fingerprint,
                       m.manifest_json AS manifestJson
                """,
                new { id = preparationId }).ConfigureAwait(false);
            return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (records.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected one sealed LongMemEval preparation manifest; found {records.Count}.");
        }

        var record = records[0];
        var manifest = JsonSerializer.Deserialize<LongMemEvalPreparationManifest>(
            record["manifestJson"].As<string>(),
            LongMemEvalPreparationManifest.JsonOptions)
            ?? throw new InvalidOperationException(
                "LongMemEval preparation manifest could not be deserialized.");
        if (record["schemaVersion"].As<int>() != manifest.SchemaVersion ||
            !string.Equals(
                record["fingerprint"].As<string>(),
                manifest.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "LongMemEval preparation marker does not match its manifest.");
        }

        manifest.VerifyIntegrity();
        return manifest;
    }
}
