using System.Globalization;
using AgentEval.Memory.External.Models;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.LongMemEval;

internal static class LongMemEvalAgentEvalEvidence
{
    internal static QuestionEvidenceEnvelope Build(
        MemoryContext context,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> originsByMessageId,
        LongMemEvalEvidenceDetail detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(originsByMessageId);

        var candidates = new List<EvidenceCandidate>();
        AddMessages(context, originsByMessageId, candidates);
        AddEntities(context, originsByMessageId, candidates);
        AddFacts(context, originsByMessageId, candidates);
        AddPreferences(context, originsByMessageId, candidates);

        if (candidates.Count > QuestionEvidenceEnvelope.MaximumReferences)
        {
            throw new InvalidOperationException(
                $"LongMemEval answer context produced {candidates.Count} normalized references; maximum is {QuestionEvidenceEnvelope.MaximumReferences}.");
        }

        var retrieved = candidates
            .Select((candidate, index) => Reference(
                candidate, index + 1, answerContextOrder: null, content: null))
            .ToArray();
        var answerContext = new List<EvidenceReference>(candidates.Count);
        var remainingContent = detail == LongMemEvalEvidenceDetail.Content
            ? QuestionEvidenceEnvelope.MaximumTotalContentLength
            : 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            string? content = null;
            if (remainingContent > 0)
            {
                var length = Math.Min(
                    Math.Min(candidate.Content.Length, EvidenceReference.MaximumContentLength),
                    remainingContent);
                content = candidate.Content[..length];
                remainingContent -= length;
            }

            answerContext.Add(Reference(
                candidate,
                index + 1,
                answerContextOrder: index + 1,
                content));
        }

        return new QuestionEvidenceEnvelope
        {
            SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
            Retrieved = retrieved,
            AnswerContext = answerContext
        };
    }

    private static void AddMessages(
        MemoryContext context,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> origins,
        ICollection<EvidenceCandidate> output)
    {
        var scores = Scores(context.RelevantMessages.RankedItems);
        foreach (var message in context.RelevantMessages.Items)
        {
            if (!origins.TryGetValue(message.MessageId, out var origin))
            {
                throw new InvalidOperationException(
                    $"Normalized LongMemEval evidence could not map message {message.MessageId} to a source origin.");
            }

            var observableSource = !origin.IsSyntheticBoundary &&
                !origin.IsSyntheticFormatterPadding;
            output.Add(new EvidenceCandidate(
                message.MessageId,
                ScoreOrNull(scores, message.MessageId),
                observableSource ? origin.SourceSessionId : null,
                observableSource ? origin.SourceTurnOrdinal : null,
                observableSource ? ParseTimestamp(origin.SourceTimestamp) : null,
                $"[{message.Role}] {message.Content}"));
        }
    }

    private static void AddEntities(
        MemoryContext context,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> origins,
        ICollection<EvidenceCandidate> output)
    {
        var scores = Scores(context.RelevantEntities.RankedItems);
        foreach (var entity in context.RelevantEntities.Items)
        {
            var source = StructuredOrigin(entity.SourceMessageIds, origins);
            output.Add(new EvidenceCandidate(
                $"entity:{entity.EntityId}",
                ScoreOrNull(scores, entity.EntityId),
                source.SessionId,
                source.TurnIndex,
                source.Timestamp,
                string.IsNullOrWhiteSpace(entity.Description)
                    ? $"[entity] {entity.Name} ({entity.Type})"
                    : $"[entity] {entity.Name} ({entity.Type}): {entity.Description}"));
        }
    }

    private static void AddFacts(
        MemoryContext context,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> origins,
        ICollection<EvidenceCandidate> output)
    {
        var scores = Scores(context.RelevantFacts.RankedItems);
        foreach (var fact in context.RelevantFacts.Items)
        {
            // A predicate-expanded fact is a relation drawn from the whole owner, so its provenance
            // may sit outside this question's message window. That is expected, not corruption, and
            // must not be resolved against a map that only covers this question — the throw in
            // StructuredOrigin stays intact for every fact that is *not* so marked.
            var expanded =
                fact.Metadata.TryGetValue(Fact.RetrievalSourceMetadataKey, out var retrievalSource) &&
                string.Equals(
                    retrievalSource?.ToString(),
                    Fact.RetrievalSourcePredicateExpansion,
                    StringComparison.Ordinal);
            var source = expanded
                ? new StructuredSource(null, null, null)
                : StructuredOrigin(fact.SourceMessageIds, origins);
            output.Add(new EvidenceCandidate(
                $"fact:{fact.FactId}",
                ScoreOrNull(scores, fact.FactId),
                source.SessionId,
                source.TurnIndex,
                source.Timestamp,
                $"[fact] {fact.Subject} {fact.Predicate} {fact.Object}"));
        }
    }

    private static void AddPreferences(
        MemoryContext context,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> origins,
        ICollection<EvidenceCandidate> output)
    {
        var scores = Scores(context.RelevantPreferences.RankedItems);
        foreach (var preference in context.RelevantPreferences.Items)
        {
            var source = StructuredOrigin(preference.SourceMessageIds, origins);
            output.Add(new EvidenceCandidate(
                $"preference:{preference.PreferenceId}",
                ScoreOrNull(scores, preference.PreferenceId),
                source.SessionId,
                source.TurnIndex,
                source.Timestamp,
                string.IsNullOrWhiteSpace(preference.Context)
                    ? $"[preference] {preference.PreferenceText}"
                    : $"[preference] {preference.PreferenceText} ({preference.Context})"));
        }
    }

    private static StructuredSource StructuredOrigin(
        IReadOnlyList<string> sourceMessageIds,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> origins)
    {
        if (sourceMessageIds.Count == 0)
            return new StructuredSource(null, null, null);

        var mapped = new List<LongMemEvalMessageOrigin>(sourceMessageIds.Count);
        foreach (var messageId in sourceMessageIds.Distinct(StringComparer.Ordinal))
        {
            if (!origins.TryGetValue(messageId, out var origin))
            {
                throw new InvalidOperationException(
                    $"Structured LongMemEval evidence could not map source message {messageId}.");
            }

            if (!origin.IsSyntheticBoundary && !origin.IsSyntheticFormatterPadding)
                mapped.Add(origin);
        }

        if (mapped.Count == 0)
            return new StructuredSource(null, null, null);
        var sessions = mapped
            .Select(origin => origin.SourceSessionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sessions.Length != 1)
            return new StructuredSource(null, null, null);

        // Extraction assigns every message in the source session to every learned item. A decisive
        // turn is observable only when exactly one real source message exists; never invent one.
        var exactTurn = mapped.Count == 1 ? mapped[0] : null;
        return new StructuredSource(
            sessions[0],
            exactTurn?.SourceTurnOrdinal,
            exactTurn is null ? null : ParseTimestamp(exactTurn.SourceTimestamp));
    }

    /// <summary>
    /// The similarity score for an item, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// <c>EvidenceCandidate.Score</c> is <c>double?</c> precisely so "not scored" and "scored zero"
    /// stay distinguishable, and the call sites were discarding that with
    /// <c>GetValueOrDefault</c> — which returns <c>0.0</c> and then widens silently.
    /// <para>
    /// It was harmless while only messages had ranked items: every other section had none, so all of
    /// them got the same 0.0 and nothing was ordered by it. Now that four more sections carry real
    /// scores, an unscored item would sort as though it had scored zero — below genuinely weak
    /// matches. A fact retrieved by predicate expansion is not a bad match; it is a match arrived at
    /// by a route with no comparable score, which is the same distinction <c>LimitBinding</c> makes
    /// by being null rather than false.
    /// </para>
    /// </remarks>
    private static double? ScoreOrNull(IReadOnlyDictionary<string, double> scores, string id) =>
        scores.TryGetValue(id, out var score) ? score : null;

    private static Dictionary<string, double> Scores(
        IReadOnlyList<MemoryContextRankedItem> rankedItems) =>
        rankedItems
            .GroupBy(item => item.ItemId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.ContextRank).First().Score,
                StringComparer.Ordinal);

    private static DateTimeOffset? ParseTimestamp(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            "yyyy/MM/dd (ddd) HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static EvidenceReference Reference(
        EvidenceCandidate candidate,
        int rank,
        int? answerContextOrder,
        string? content) =>
        new()
        {
            Id = candidate.Id,
            Rank = rank,
            SimilarityScore = candidate.Score,
            SourceSessionId = candidate.SourceSessionId,
            SourceTurnIndex = candidate.SourceTurnIndex,
            SourceTimestamp = candidate.SourceTimestamp,
            AnswerContextOrder = answerContextOrder,
            Content = content
        };

    private sealed record EvidenceCandidate(
        string Id,
        double? Score,
        string? SourceSessionId,
        int? SourceTurnIndex,
        DateTimeOffset? SourceTimestamp,
        string Content);

    private sealed record StructuredSource(
        string? SessionId,
        int? TurnIndex,
        DateTimeOffset? Timestamp);
}
