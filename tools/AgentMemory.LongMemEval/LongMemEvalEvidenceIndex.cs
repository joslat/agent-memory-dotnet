using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
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
        => CreateCore(entries, options, entry =>
        {
            var formatted = LongMemEvalHistoryFormatter.Format(entry, options);
            return (formatted, Fingerprint(formatted), BuildInvocationPrompt(entry));
        });

    /// <summary>
    /// Builds the index for entries a runner will inject through the <b>timestamped</b> channel
    /// (<c>ITimestampedHistoryInjectableAgent</c>), i.e. any run whose temporal grounding is not
    /// <see cref="TemporalGroundingMode.None"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things differ from <see cref="Create"/>, each forced by what the runner actually sends.
    /// The formatted content comes from <c>FormatTimestamped</c> — under TimestampsOnly the session
    /// boundary marker drops its date, so an index built from the dated <c>Format</c> output would
    /// never align. The invocation prompt is grounding-aware — under TimestampsOnly the runner sends
    /// the bare question, because printing "Current Date:" would hand back the crutch the mode
    /// removes. And the fingerprint is salted with the history's QueryTime — Prospective pair arms
    /// share one haystack and one question text and differ only in the query instant, so a
    /// fingerprint over the turns alone cannot tell the arms apart.
    /// </para>
    /// </remarks>
    internal static LongMemEvalEvidenceIndex CreateTimestamped(
        IReadOnlyList<LongMemEvalEntry> entries,
        ExternalBenchmarkOptions options)
        => CreateCore(entries, options, entry =>
        {
            var history = LongMemEvalHistoryFormatter.FormatTimestamped(entry, options);
            var formatted = history.Turns
                .Select(turn => (turn.UserMessage, turn.AssistantResponse))
                .ToArray();
            return (formatted,
                Fingerprint(formatted, history.QueryTime),
                BuildTimestampedInvocationPrompt(entry, options));
        });

    /// <summary>
    /// Builds the evidence index for one TypedMemEval vertical from its embedded corpus, selecting
    /// and formatting entries exactly as <c>TypedMemEvalRunner</c> will for the same facade options.
    /// </summary>
    /// <remarks>
    /// This is the construction path the envelope pipeline was missing: <see cref="Load"/> requires
    /// a dataset <i>file</i>, TypedMemEval corpora are embedded resources, and without an index the
    /// adapter attaches no evidence envelope — every typed run reported attribution
    /// <c>Unobserved</c>. Question counts, ids and hashes are all read from the corpus at run time,
    /// never hard-coded, so the 0.23 corpus revision changes nothing here.
    /// </remarks>
    internal static LongMemEvalEvidenceIndex CreateTypedMemEval(
        TypedMemEvalVertical vertical,
        TypedMemEvalOptions? facade = null)
    {
        facade ??= new TypedMemEvalOptions();
        facade.Validate();
        var descriptor = TypedMemEvalVerticals.For(vertical);
        var options = TypedMemEvalOptionMapping.ToExternalOptions(facade, descriptor);
        var entries = TypedMemEvalCorpus.Load(vertical, options);
        return options.TemporalGrounding == TemporalGroundingMode.None
            ? Create(entries, options)
            : CreateTimestamped(entries, options);
    }

    private static LongMemEvalEvidenceIndex CreateCore(
        IReadOnlyList<LongMemEvalEntry> entries,
        ExternalBenchmarkOptions options,
        Func<LongMemEvalEntry,
            (IReadOnlyList<(string UserMessage, string AssistantResponse)> Formatted,
             string Fingerprint,
             string InvocationPrompt)> format)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var byHistory = new Dictionary<string, List<LongMemEvalEvidenceQuestion>>(StringComparer.Ordinal);
        var byId = new Dictionary<string, LongMemEvalEvidenceQuestion>(StringComparer.Ordinal);
        var questions = new List<LongMemEvalEvidenceQuestion>(entries.Count);
        foreach (var entry in entries)
        {
            var (formatted, fingerprint, invocationPrompt) = format(entry);
            var question = BuildQuestion(entry, formatted, options, invocationPrompt);
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
        return ResolveCore(Fingerprint(history), prompt);
    }

    /// <summary>
    /// Resolves a question injected through the timestamped channel. The query instant is part of
    /// the identity: Prospective pair arms share one haystack and one question text, so without it
    /// the two arms are a single fingerprint and neither can be attributed.
    /// </summary>
    public LongMemEvalEvidenceQuestion Resolve(
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        DateTimeOffset queryTime,
        string prompt)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return ResolveCore(Fingerprint(history, queryTime), prompt);
    }

    private LongMemEvalEvidenceQuestion ResolveCore(string fingerprint, string prompt)
    {
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
        ExternalBenchmarkOptions options,
        string invocationPrompt)
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
            invocationPrompt,
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

    /// <summary>
    /// The prompt <c>TypedMemEvalRunner.BuildPrompt</c> sends for a structurally-injected question:
    /// under TimestampsOnly the query time arrives through the typed channel and the prompt is the
    /// bare question; under TimestampsAndText the "Current Date:" prefix survives.
    /// </summary>
    private static string BuildTimestampedInvocationPrompt(
        LongMemEvalEntry entry, ExternalBenchmarkOptions options) =>
        options.TemporalGrounding == TemporalGroundingMode.TimestampsOnly
            ? entry.Question
            : BuildInvocationPrompt(entry);

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
        => Fingerprint(history, salt: null);

    /// <summary>
    /// Fingerprint for a timestamped history: the turns <b>plus the query instant</b>. Two
    /// Prospective pair arms are the same turns asked at two different instants, so the instant is
    /// part of the question's identity, not metadata about it.
    /// </summary>
    internal static string Fingerprint(
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        DateTimeOffset queryTime)
        => Fingerprint(
            history,
            $"query-time:{queryTime.UtcTicks.ToString(CultureInfo.InvariantCulture)}");

    private static string Fingerprint(
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        string? salt)
    {
        var builder = new StringBuilder();
        foreach (var (user, assistant) in history)
        {
            Append(user);
            Append(assistant);
        }

        if (salt is not null)
            Append(salt);

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
        // 27.1. TURN attribution, the half 22.3 left blind. Session coverage became observable on the
        // structured arm; turn coverage did not, because `goldTurnsHit` counted only `evidence`, which
        // is built from recalled RAW messages. A structured run has no message budget, so that count
        // was 0 on every structured question -- correct answers and wrong ones alike.
        //
        // That mattered more than it looks. Turn coverage is the one retrieval signal that still
        // separates hybrid successes from hybrid failures (0.937 against 0.667, where SESSION coverage
        // separates them far less), and it was unobservable on the arm that actually ships. Any test of
        // query formulation -- the last untested retrieval lever -- would have been unable to see its
        // own effect.
        var structuredGoldTurns = new HashSet<string>(StringComparer.Ordinal);
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

            // Same predicate the message channel uses for GoldTurnHit (origin.HasAnswer), so a turn
            // reached through a fact and a turn reached through a message count identically.
            if (origin.HasAnswer &&
                !origin.IsSyntheticBoundary &&
                !origin.IsSyntheticFormatterPadding)
            {
                structuredGoldTurns.Add(messageId);
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

        // Union by message id for the same reason sessions union rather than sum: on the hybrid arm a
        // gold turn is routinely reached through BOTH a recalled message and a fact extracted from it,
        // and counting it twice would report turn coverage above 1.0.
        var goldTurnsCovered = evidence
            .Where(item => item.GoldTurnHit)
            .Select(item => item.MessageId)
            .Concat(structuredGoldTurns)
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
            GoldTurnsHit: goldTurnsCovered,
            GoldTurnHitAtK: !observable || annotatedGoldTurns == 0 ? null : goldTurnsCovered > 0,
            FirstGoldSessionRank: firstGoldSessionRank,
            // Deliberately message-derived only, and therefore null on a pure structured run. A rank
            // means "position in the answer context", and structured items are ranked within their own
            // sections rather than in one ordering shared with messages. Synthesising a cross-section
            // rank would produce a number that looks comparable between arms and is not; the honest
            // report is absence. GoldTurnsHit above is a COUNT and has no such problem.
            FirstGoldTurnRank: firstGoldTurnRank,
            ReciprocalRank: observable && firstGoldSessionRank is int rank ? 1d / rank : null,
            RankedItems: detail == LongMemEvalEvidenceDetail.None
                ? Array.Empty<LongMemEvalRankedEvidence>()
                : evidence.AsReadOnly(),
            GoldAttributionObservable: observable);
    }
}