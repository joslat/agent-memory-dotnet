using AgentMemory.Core.Memory;

namespace AgentMemory.LongMemEval;

/// <summary>A question whose answer is something the assistant did: <c>assistant | predicate | object</c>.</summary>
internal sealed record EpisodicGold(string QuestionId, string Predicate, string Object);

/// <summary>What happened to one episodic gold fact.</summary>
internal enum EpisodicRecallOutcome
{
    /// <summary>Stored and reached the retrieved context. The only success.</summary>
    Recalled,

    /// <summary>In the graph, absent from the context — a RETRIEVAL failure.</summary>
    StoredNotRetrieved,

    /// <summary>Never in the graph — an EXTRACTION failure. Retrieval cannot be blamed.</summary>
    NotStored,

    /// <summary>Retrieved but not stored: the two probes disagree, so the measurement is broken.</summary>
    Incoherent,
}

/// <summary>One gold fact's verdict.</summary>
internal sealed record EpisodicRecallVerdict(
    string QuestionId, bool Stored, bool Retrieved, EpisodicRecallOutcome Outcome);

/// <summary>
/// Measures whether episodic memory is stored and retrieved, without a judge or an answer model.
/// </summary>
/// <remarks>
/// <para>
/// LongMemEval asks what the <b>user</b> said and did, so it can only charge for episodic recall and
/// never reward it — capture was measured consuming 32.3% of the structured retrieval budget and
/// +23.1% answer-prompt tokens for no accuracy movement. Answering "does episodic memory work" with an
/// accuracy delta would inherit a ~4 point same-base floor and a ~9 point cross-build one and could
/// not resolve the effect at n=50.
/// </para>
/// <para>
/// <b>So this does not measure answers at all.</b> It asks two questions the accuracy number merges:
/// was the fact <i>stored</i>, and did it <i>reach the retrieved context</i>. Those failures call for
/// opposite fixes — one belongs to the extractor, the other to retrieval — and are reported apart.
/// </para>
/// </remarks>
internal static class LongMemEvalEpisodicRecall
{
    /// <summary>
    /// The canonical subject every episodic fact is written under, per <c>ExtractionPromptSemantics</c>.
    /// </summary>
    private static readonly string EpisodicSubject =
        MemoryTripleCanonicalizer.CanonicalValue("assistant");

    internal static EpisodicRecallVerdict Evaluate(
        EpisodicGold gold,
        IReadOnlyList<(string Subject, string Predicate, string Object)> stored,
        IReadOnlyList<(string Subject, string Predicate, string Object)> retrieved)
    {
        var stored_ = stored.Any(triple => Matches(gold, triple));
        var retrieved_ = retrieved.Any(triple => Matches(gold, triple));

        // Retrieved-without-stored cannot happen when both probes read the same graph, so it means
        // they disagree - a scope mismatch, a stale read, or a harness bug. Reported rather than
        // counted as success, because a broken measurement that looks good is the worst outcome here.
        var outcome = (stored_, retrieved_) switch
        {
            (true, true) => EpisodicRecallOutcome.Recalled,
            (true, false) => EpisodicRecallOutcome.StoredNotRetrieved,
            (false, false) => EpisodicRecallOutcome.NotStored,
            (false, true) => EpisodicRecallOutcome.Incoherent,
        };

        return new EpisodicRecallVerdict(gold.QuestionId, stored_, retrieved_, outcome);
    }

    /// <summary>
    /// Whether a triple is the episodic memory the gold names.
    /// </summary>
    /// <remarks>
    /// The subject must canonicalize to <c>assistant</c>. A user-subject fact carrying the same
    /// predicate and object is a <i>different memory</i>, and accepting it would let semantic recall
    /// masquerade as episodic recall — the confusion this whole phase exists to avoid. Predicate and
    /// object go through the same canonicalizer the write path keys on, so capitalisation and spacing
    /// cannot turn a stored fact into a reported extraction failure.
    /// </remarks>
    private static bool Matches(
        EpisodicGold gold, (string Subject, string Predicate, string Object) triple) =>
        string.Equals(
            MemoryTripleCanonicalizer.CanonicalValue(triple.Subject),
            EpisodicSubject, StringComparison.Ordinal)
        && string.Equals(
            MemoryTripleCanonicalizer.Canonical(triple.Predicate),
            MemoryTripleCanonicalizer.Canonical(gold.Predicate), StringComparison.Ordinal)
        && string.Equals(
            MemoryTripleCanonicalizer.CanonicalValue(triple.Object),
            MemoryTripleCanonicalizer.CanonicalValue(gold.Object), StringComparison.Ordinal);

    internal static EpisodicRecallSummary Summarise(IReadOnlyList<EpisodicRecallVerdict> verdicts)
    {
        var stored = verdicts.Count(v => v.Stored);
        return new EpisodicRecallSummary(
            verdicts.Count,
            verdicts.Count(v => v.Outcome == EpisodicRecallOutcome.Recalled),
            verdicts.Count(v => v.Outcome == EpisodicRecallOutcome.StoredNotRetrieved),
            verdicts.Count(v => v.Outcome == EpisodicRecallOutcome.NotStored),
            verdicts.Count(v => v.Outcome == EpisodicRecallOutcome.Incoherent),
            // Denominator is what was STORED, not every question. Charging retrieval for the
            // extractor's misses would misattribute the failure and make a retrieval fix look
            // ineffective when the memory was never there to find.
            stored == 0 ? 0 : (double)verdicts.Count(v => v.Outcome == EpisodicRecallOutcome.Recalled) / stored);
    }
}

/// <summary>Episodic recall, with extraction and retrieval failures kept apart.</summary>
internal sealed record EpisodicRecallSummary(
    int Total,
    int Recalled,
    int StoredNotRetrieved,
    int NotStored,
    int Incoherent,
    double RetrievalRecall);
