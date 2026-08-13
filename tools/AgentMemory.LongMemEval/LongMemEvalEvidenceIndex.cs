using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.LongMemEval;

internal enum LongMemEvalEvidenceDetail
{
    None,
    Identifiers,
    Content
}

/// <summary>
/// Evaluator-side index that aligns AgentEval's sampled/formatted history with the original LongMemEval
/// session and turn identifiers. Gold labels never leave this object and are never persisted to AgentMemory.
/// </summary>
internal sealed class LongMemEvalEvidenceIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<LongMemEvalEvidenceQuestion>> _questionsByHistory;
    private readonly IReadOnlyDictionary<string, LongMemEvalEvidenceQuestion> _questionsById;
    private readonly IReadOnlyList<LongMemEvalEvidenceQuestion> _questions;

    private LongMemEvalEvidenceIndex(
        Dictionary<string, List<LongMemEvalEvidenceQuestion>> questionsByHistory,
        IReadOnlyDictionary<string, LongMemEvalEvidenceQuestion> questionsById,
        IReadOnlyList<LongMemEvalEvidenceQuestion> questions)
    {
        _questionsByHistory = questionsByHistory;
        _questionsById = questionsById;
        _questions = questions;
    }

    public IReadOnlyList<LongMemEvalEvidenceQuestion> Questions => _questions;

    public static LongMemEvalEvidenceIndex Load(
        string datasetPath,
        ExternalBenchmarkOptions options) =>
        Create(LongMemEvalDataLoader.LoadFromFile(datasetPath, options), options);

    internal static LongMemEvalEvidenceIndex Create(
        IReadOnlyList<LongMemEvalEntry> entries,
        ExternalBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var byHistory = new Dictionary<string, List<LongMemEvalEvidenceQuestion>>(StringComparer.Ordinal);
        var byId = new Dictionary<string, LongMemEvalEvidenceQuestion>(StringComparer.Ordinal);
        var questions = new List<LongMemEvalEvidenceQuestion>(entries.Count);
        foreach (var entry in entries)
        {
            var formatted = LongMemEvalHistoryFormatter.Format(entry, options);
            var question = BuildQuestion(entry, formatted, options);
            var fingerprint = Fingerprint(formatted);
            if (!byHistory.TryGetValue(fingerprint, out var matching))
            {
                matching = [];
                byHistory.Add(fingerprint, matching);
            }

            matching.Add(question);
            if (!byId.TryAdd(question.QuestionId, question))
            {
                throw new InvalidOperationException(
                    $"LongMemEval evidence contains duplicate question id {question.QuestionId}.");
            }
            questions.Add(question);
        }

        return new LongMemEvalEvidenceIndex(byHistory, byId, questions.AsReadOnly());
    }

    public LongMemEvalEvidenceQuestion Resolve(
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        string prompt)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var fingerprint = Fingerprint(history);
        lock (_gate)
        {
            if (!_questionsByHistory.TryGetValue(fingerprint, out var candidates))
            {
                throw new InvalidOperationException(
                    "Injected LongMemEval history does not match the evaluator-side evidence index.");
            }

            var matches = candidates
                .Where(candidate => string.Equals(candidate.InvocationPrompt, prompt, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one indexed LongMemEval question for the injected history and prompt; found {matches.Length}.");
            }

            candidates.Remove(matches[0]);
            if (candidates.Count == 0)
                _questionsByHistory.Remove(fingerprint);
            return matches[0];
        }
    }

    public LongMemEvalEvidenceQuestion GetByQuestionId(string questionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        if (!_questionsById.TryGetValue(questionId, out var question))
        {
            throw new InvalidOperationException(
                $"No indexed LongMemEval question has id {questionId}.");
        }

        return question;
    }

    private static LongMemEvalEvidenceQuestion BuildQuestion(
        LongMemEvalEntry entry,
        IReadOnlyList<(string UserMessage, string AssistantResponse)> formatted,
        ExternalBenchmarkOptions options)
    {
        var sessions = entry.HaystackSessions
            ?? throw new InvalidOperationException($"LongMemEval question {entry.QuestionId} has no sessions.");
        var sessionIds = entry.HaystackSessionIds
            ?? throw new InvalidOperationException($"LongMemEval question {entry.QuestionId} has no session ids.");
        var sessionDates = entry.HaystackDates
            ?? throw new InvalidOperationException($"LongMemEval question {entry.QuestionId} has no session dates.");

        if (sessions.Count != sessionIds.Count ||
            sessions.Count != sessionDates.Count)
        {
            throw new InvalidOperationException(
                $"LongMemEval question {entry.QuestionId} has misaligned session ids, dates, or content.");
        }

        if (!options.PreserveSessionBoundaries)
        {
            throw new InvalidOperationException(
                "LongMemEval evidence tracing requires PreserveSessionBoundaries so source sessions remain unambiguous.");
        }

        var origins = new List<LongMemEvalMessageOrigin>(formatted.Count * 2);
        var formattedIndex = 0;
        var messageOrdinal = 0;

        for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
        {
            var session = sessions[sessionIndex];
            var sessionId = sessionIds[sessionIndex];
            var sourceTimestamp = sessionDates[sessionIndex];

            if (formattedIndex >= formatted.Count || !IsSessionBoundary(formatted[formattedIndex]))
                throw AlignmentFailure(entry.QuestionId);
            var boundary = formatted[formattedIndex++];
            origins.Add(Origin(boundary.UserMessage, "user", null, true, false, false));
            origins.Add(Origin(boundary.AssistantResponse, "assistant", null, true, false, false));

            // Segment the exact AgentEval output by its synthetic session boundaries, then map each
            // non-empty side back to the original source turn. AgentEval drops a leading assistant-only
            // continuation and pads a trailing user-only turn with a synthetic assistant acknowledgment.
            // Both are legitimate structured-history shapes and must retain unambiguous provenance.
            var usedSourceTurns = new HashSet<int>();
            // The source turn most recently matched, so a padding "I understand." can be validated
            // against what it actually follows rather than against the end of the session.
            var lastConsumedTurnIndex = -1;
            while (formattedIndex < formatted.Count && !IsSessionBoundary(formatted[formattedIndex]))
            {
                var formattedTurn = formatted[formattedIndex++];
                AddFormattedSide(formattedTurn.UserMessage, "user");
                AddFormattedSide(formattedTurn.AssistantResponse, "assistant");
            }

            void AddFormattedSide(string content, string role)
            {
                for (var turnIndex = 0; turnIndex < session.Count; turnIndex++)
                {
                    var source = session[turnIndex];
                    if (usedSourceTurns.Contains(turnIndex) ||
                        !string.Equals(source.Role, role, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(source.Content, content, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    usedSourceTurns.Add(turnIndex);
                    lastConsumedTurnIndex = turnIndex;
                    origins.Add(Origin(
                        source.Content,
                        source.Role,
                        turnIndex,
                        false,
                        false,
                        source.HasAnswer is true));
                    return;
                }

                // The formatter pads an unanswered user turn with "I understand." in TWO places, not
                // one: at the end of a session, and mid-session whenever two user turns are
                // consecutive (LongMemEvalHistoryFormatter flushes the pending user on the next user
                // turn). This previously accepted only the trailing case, so any question containing
                // back-to-back user turns failed alignment outright - 8 of the 500 dataset questions,
                // which is why the fixed ten never hit it.
                //
                // This is not a relaxation of the provenance guard: a mid-session pad has exactly the
                // same provenance as a trailing one - synthetic formatter output following a user
                // turn - so it is recorded as synthetic padding either way. What changes is that the
                // check now matches the formatter's real contract instead of a subset of it.
                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(content, "I understand.", StringComparison.Ordinal) &&
                    lastConsumedTurnIndex >= 0 &&
                    string.Equals(
                        session[lastConsumedTurnIndex].Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    origins.Add(Origin(content, role, null, false, true, false));
                    return;
                }

                throw AlignmentFailure(entry.QuestionId);
            }

            LongMemEvalMessageOrigin Origin(
                string content,
                string role,
                int? sourceTurnOrdinal,
                bool syntheticBoundary,
                bool syntheticFormatterPadding,
                bool hasAnswer) => new(
                    MessageOrdinal: messageOrdinal++,
                    SourceSessionId: sessionId,
                    SourceSessionOrdinal: sessionIndex,
                    SourceTurnOrdinal: sourceTurnOrdinal,
                    SourceTimestamp: sourceTimestamp,
                    Role: role,
                    FormattedContent: content,
                    IsSyntheticBoundary: syntheticBoundary,
                    IsSyntheticFormatterPadding: syntheticFormatterPadding,
                    HasAnswer: hasAnswer);
        }
        if (formattedIndex != formatted.Count || origins.Count != formatted.Count * 2)
            throw AlignmentFailure(entry.QuestionId);

        return new LongMemEvalEvidenceQuestion(
            entry.QuestionId,
            entry.QuestionType,
            entry.Question,
            BuildInvocationPrompt(entry),
            entry.Answer,
            entry.QuestionDate ?? string.Empty,
            entry.IsAbstention,
            (entry.AnswerSessionIds ?? []).ToHashSet(StringComparer.Ordinal),
            sessions.Sum(session => session.Count(turn => turn.HasAnswer is true)),
            origins.AsReadOnly());
    }

    private static string BuildInvocationPrompt(LongMemEvalEntry entry) =>
        string.IsNullOrEmpty(entry.QuestionDate)
            ? entry.Question
            : $"Current Date: {entry.QuestionDate}\n\n{entry.Question}";

    private static bool IsSessionBoundary((string UserMessage, string AssistantResponse) turn) =>
        turn.UserMessage.StartsWith("--- Session ", StringComparison.Ordinal) &&
        turn.UserMessage.EndsWith(" ---", StringComparison.Ordinal) &&
        string.Equals(
            turn.AssistantResponse,
            "Understood. Starting a new conversation session.",
            StringComparison.Ordinal);

    private static InvalidOperationException AlignmentFailure(string questionId) => new(
        $"AgentEval formatted history could not be aligned to source turns for LongMemEval question {questionId}.");

    internal static string Fingerprint(
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history)
    {
        var builder = new StringBuilder();
        foreach (var (user, assistant) in history)
        {
            Append(user);
            Append(assistant);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));

        void Append(string value) => builder.Append(value.Length).Append(':').Append(value).Append('|');
    }
}

internal sealed record LongMemEvalEvidenceQuestion(
    string QuestionId,
    string QuestionType,
    string Question,
    string InvocationPrompt,
    string GoldAnswer,
    string QuestionDate,
    bool IsAbstention,
    IReadOnlySet<string> AnswerSessionIds,
    int AnnotatedGoldTurnCount,
    IReadOnlyList<LongMemEvalMessageOrigin> Messages)
;

internal sealed record LongMemEvalMessageOrigin(
    int MessageOrdinal,
    string SourceSessionId,
    int SourceSessionOrdinal,
    int? SourceTurnOrdinal,
    string SourceTimestamp,
    string Role,
    string FormattedContent,
    bool IsSyntheticBoundary,
    bool IsSyntheticFormatterPadding,
    bool HasAnswer);

public sealed record LongMemEvalRankedEvidence(
    string MessageId,
    int RetrievalRank,
    int ContextRank,
    double SimilarityScore,
    string SourceSessionId,
    int SourceSessionOrdinal,
    int? SourceTurnOrdinal,
    string SourceTimestamp,
    string Role,
    bool IsSyntheticBoundary,
    bool IsSyntheticFormatterPadding,
    bool GoldSessionHit,
    bool GoldTurnHit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Content);

public sealed record LongMemEvalRetrievalEvidence(
    int K,
    int AnswerPromptCharacters,
    int EstimatedAnswerPromptTokens,
    int DistinctSourceSessions,
    int MaxItemsFromSingleSession,
    int GoldSessionsRequired,
    int GoldSessionsHit,
    double? GoldSessionRecallAtK,
    int AnnotatedGoldTurns,
    int GoldTurnsHit,
    bool? GoldTurnHitAtK,
    int? FirstGoldSessionRank,
    int? FirstGoldTurnRank,
    double? ReciprocalRank,
    IReadOnlyList<LongMemEvalRankedEvidence> RankedItems,
    bool GoldAttributionObservable = true)
{
    /// <param name="configuredMessageBudget">
    /// The recall budget's message allowance. Gold attribution is resolved through recalled raw
    /// messages, so when this is zero — as in Structured mode — retrieval was never given the chance
    /// to hit a gold turn and the gold metrics are <b>not observable</b> rather than zero. Reporting
    /// 0.0 here previously caused every failed structured question to be classified a
    /// <c>retrieval-miss</c>, manufacturing a product defect out of a harness limitation.
    /// </param>
    internal static LongMemEvalRetrievalEvidence Build(
        LongMemEvalEvidenceQuestion question,
        IReadOnlyList<Message> recalled,
        IReadOnlyList<MemoryContextRankedItem> rankedItems,
        IReadOnlyDictionary<string, LongMemEvalMessageOrigin> originsByMessageId,
        LongMemEvalEvidenceDetail detail,
        int answerPromptCharacters,
        int configuredMessageBudget,
        IReadOnlyCollection<string>? structuredSourceMessageIds = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(recalled);
        ArgumentNullException.ThrowIfNull(rankedItems);
        ArgumentNullException.ThrowIfNull(originsByMessageId);
        if (answerPromptCharacters < 0)
            throw new ArgumentOutOfRangeException(nameof(answerPromptCharacters));

        if (rankedItems.Count != recalled.Count)
        {
            throw new InvalidOperationException(
                $"Ranked retrieval evidence contained {rankedItems.Count} entries for {recalled.Count} recalled messages.");
        }

        var recalledById = recalled.ToDictionary(message => message.MessageId, StringComparer.Ordinal);
        var evidence = new List<LongMemEvalRankedEvidence>(rankedItems.Count);
        foreach (var ranked in rankedItems.OrderBy(item => item.ContextRank))
        {
            if (!recalledById.TryGetValue(ranked.ItemId, out var message) ||
                !originsByMessageId.TryGetValue(ranked.ItemId, out var origin))
            {
                throw new InvalidOperationException(
                    $"Ranked LongMemEval item {ranked.ItemId} could not be mapped to its source turn.");
            }

            evidence.Add(new LongMemEvalRankedEvidence(
                ranked.ItemId,
                ranked.RetrievalRank,
                ranked.ContextRank,
                ranked.Score,
                origin.SourceSessionId,
                origin.SourceSessionOrdinal,
                origin.SourceTurnOrdinal,
                origin.SourceTimestamp,
                origin.Role,
                origin.IsSyntheticBoundary,
                origin.IsSyntheticFormatterPadding,
                question.AnswerSessionIds.Contains(origin.SourceSessionId) &&
                    !origin.IsSyntheticBoundary &&
                    !origin.IsSyntheticFormatterPadding,
                origin.HasAnswer,
                detail == LongMemEvalEvidenceDetail.Content ? message.Content : null));
        }

        var sourceSessionCounts = evidence
            .GroupBy(item => item.SourceSessionId, StringComparer.Ordinal)
            .Select(group => group.Count())
            .ToArray();
        var goldSessionsHit = evidence
            .Where(item => item.GoldSessionHit)
            .Select(item => item.SourceSessionId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var annotatedGoldTurns = question.AnnotatedGoldTurnCount;
        var goldTurnsHit = evidence.Count(item => item.GoldTurnHit);
        var firstGoldSessionRank = evidence
            .Where(item => item.GoldSessionHit)
            .Select(item => (int?)item.ContextRank)
            .FirstOrDefault();
        var firstGoldTurnRank = evidence
            .Where(item => item.GoldTurnHit)
            .Select(item => (int?)item.ContextRank)
            .FirstOrDefault();

        // 22.3. Gold attribution USED to ride entirely on recalled raw messages, so a structured run
        // -- which has no message budget -- reported every gold metric as unobservable. That was
        // honest but blinding: it left GoldSessionRecallAtK null on 1,476 of 1,476 structured
        // question-records, and gold-session recall is exactly COVERAGE, which the completeness sweep
        // then measured to be worth eighty points while nothing in the harness could see it.
        //
        // Structured items carry SourceMessageIds of their own, so the attribution is resolvable
        // without raw messages: map each retrieved entity/fact/preference back through its provenance
        // to a source session. The union with the message-derived sessions below is what makes
        // coverage observable on BOTH arms and therefore comparable between them.
        var structuredGoldSessions = new HashSet<string>(StringComparer.Ordinal);
        var structuredSessionsSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var messageId in structuredSourceMessageIds ?? [])
        {
            if (!originsByMessageId.TryGetValue(messageId, out var origin)) continue;
            structuredSessionsSeen.Add(origin.SourceSessionId);
            if (question.AnswerSessionIds.Contains(origin.SourceSessionId) &&
                !origin.IsSyntheticBoundary &&
                !origin.IsSyntheticFormatterPadding)
            {
                structuredGoldSessions.Add(origin.SourceSessionId);
            }
        }

        // Union, not sum: a session reached through both a recalled message and a retrieved fact is
        // one session covered, and adding it twice would report recall above 1.0 on the hybrid arm.
        var goldSessionsCovered = evidence
            .Where(item => item.GoldSessionHit)
            .Select(item => item.SourceSessionId)
            .Concat(structuredGoldSessions)
            .ToHashSet(StringComparer.Ordinal)
            .Count;

        // Observable when EITHER channel could have hit: a message budget, or structured items whose
        // provenance resolves. A structured run with no resolvable provenance is still unobservable
        // rather than zero -- the distinction the original guard existed to protect.
        var observable = configuredMessageBudget > 0 || structuredSessionsSeen.Count > 0;

        return new LongMemEvalRetrievalEvidence(
            K: recalled.Count,
            AnswerPromptCharacters: answerPromptCharacters,
            EstimatedAnswerPromptTokens: (answerPromptCharacters + 3) / 4,
            DistinctSourceSessions: sourceSessionCounts.Length,
            MaxItemsFromSingleSession: sourceSessionCounts.DefaultIfEmpty(0).Max(),
            GoldSessionsRequired: question.AnswerSessionIds.Count,
            GoldSessionsHit: goldSessionsCovered,
            GoldSessionRecallAtK: !observable || question.AnswerSessionIds.Count == 0
                ? null
                : (double)goldSessionsCovered / question.AnswerSessionIds.Count,
            AnnotatedGoldTurns: annotatedGoldTurns,
            GoldTurnsHit: goldTurnsHit,
            GoldTurnHitAtK: !observable || annotatedGoldTurns == 0 ? null : goldTurnsHit > 0,
            FirstGoldSessionRank: firstGoldSessionRank,
            FirstGoldTurnRank: firstGoldTurnRank,
            ReciprocalRank: observable && firstGoldSessionRank is int rank ? 1d / rank : null,
            RankedItems: detail == LongMemEvalEvidenceDetail.None
                ? Array.Empty<LongMemEvalRankedEvidence>()
                : evidence.AsReadOnly(),
            GoldAttributionObservable: observable);
    }
}