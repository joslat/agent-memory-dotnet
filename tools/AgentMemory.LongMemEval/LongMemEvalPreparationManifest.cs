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
    bool UseJsonResponseFormat,
    IReadOnlyList<LongMemEvalPreparedQuestion> Questions,
    long InitialExtractionCalls,
    string Fingerprint)
{
    public const int CurrentSchemaVersion = 2;

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
        bool useJsonResponseFormat = true)
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
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentOutOfRangeException.ThrowIfNegative(initialExtractionCalls);

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
            useJsonResponseFormat,
            materialized,
            initialExtractionCalls,
            Fingerprint: string.Empty);
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
            manifest.UseJsonResponseFormat,
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
    bool UseJsonResponseFormat = true)
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
            !string.Equals(
                manifest.ExtractionSourceTime,
                ExtractionSourceTime,
                StringComparison.Ordinal))
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
        bool useJsonResponseFormat = true) =>
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
            useJsonResponseFormat);
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

        if (!string.Equals(prepared.QuestionId, evidenceQuestion.QuestionId, StringComparison.Ordinal) ||
            !string.Equals(prepared.HistorySha256, historySha256, StringComparison.Ordinal) ||
            !string.Equals(prepared.ScopeSha256, scopeSha256, StringComparison.Ordinal) ||
            prepared.MessagesPrepared != evidenceQuestion.Messages.Count ||
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
