using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Services;

/// <summary>
/// The recall fan-out half of the assembler (Proposal M, 30.10).
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate file for a load-bearing reason, not for tidiness.</b>
/// <c>AsOfRecallDivergenceTests</c> establishes which options each recall path honours by splitting
/// the assembler's source at <c>AssembleContextAsOfAsync</c> and comparing option references either
/// side. Helpers that live physically below that point read as as-of code however plainly they are
/// live-only, and this block's references to <c>ExpandFactsByPredicate</c> and
/// <c>MaxExpandedFacts</c> silently erased two documented divergences the moment it was added.
/// </para>
/// <para>
/// Editing the expected list would have "fixed" that by asserting something false. Moving the code
/// to a file the guard does not read makes the guard correct again, because fan-out genuinely does
/// not run on the as-of path — see <c>VoidFanOutForAsOf</c> for what happens when a caller asks for
/// it there anyway.
/// </para>
/// </remarks>
internal sealed partial class MemoryContextAssembler
{
    /// <summary>
    /// The report an as-of recall returns when the caller supplied sub-queries anyway.
    /// </summary>
    /// <remarks>
    /// Design §5.5: the as-of path ignores fan-out and says so. A null report there would mean "the
    /// planner never ran", which is true but unhelpful — the caller explicitly asked for something
    /// and is entitled to know it was declined rather than forgotten.
    /// </remarks>
    private static RecallFanOutReport? VoidFanOutForAsOf(RecallRequest request) =>
        request.SubQueries is { Count: > 0 }
            ? new RecallFanOutReport
            {
                GateFired = false,
                DeriverId = "caller",
                VoidReason = "asof-not-supported",
            }
            : null;

    /// <summary>Whether the planner should run at all — the null-vs-declined boundary.</summary>
    /// <remarks>
    /// Caller-supplied sub-queries win even with the feature disabled, the same philosophy as
    /// <c>TemporalReferenceTime</c>: an explicit request is not something a global flag gets to veto.
    /// That is also what makes the mechanism reachable from the eval harness without touching the
    /// framework seam.
    /// </remarks>
    private bool ShouldConsiderFanOut(RecallRequest request) =>
        request.SubQueries is { Count: > 0 } || (_options.FanOut.Enabled && _subQueryDeriver is not null);

    /// <summary>One leg's raw result, before the budget has had its say.</summary>
    /// <remarks>
    /// Carries the contributed IDS rather than a count, because <c>SurvivedBudget</c> cannot be known
    /// until truncation has run — and reporting a pre-budget number as though it were a post-budget
    /// one is audit finding R4: two distinct quantities collapsed into a single prediction.
    /// </remarks>
    private sealed record LegContribution(
        MemoryTypeAffinity Affinity,
        string QueryText,
        int ItemsRetrieved,
        IReadOnlyList<string> ContributedIds);

    private readonly record struct FanOutOutcome(
        RecallFanOutReport? Report,
        IReadOnlyList<Entity> Entities,
        IReadOnlyList<Fact> Facts,
        IReadOnlyList<Preference> Preferences,
        IReadOnlyList<Message> Messages,
        IReadOnlyList<ReasoningTrace> Traces,
        IReadOnlyList<LegContribution> Legs);

    /// <summary>
    /// Derives (or accepts) sub-queries, retrieves each, and merges them into the monolithic sections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cost when the gate declines is <b>zero</b>: no embedding, no search, one token scan. Cost when
    /// it fires is bounded by <c>MaxSubQueries</c> embeddings plus their section searches.
    /// </para>
    /// <para>
    /// A leg whose embedding fails is skipped and counted rather than throwing. The recall the caller
    /// asked for has already succeeded by this point, and a failed enhancement must not take it down —
    /// but it must not vanish either, so the count reaches <c>VoidReason</c>.
    /// </para>
    /// </remarks>
    private async Task<FanOutOutcome> RunFanOutAsync(
        RecallRequest request,
        RecallOptions recallOpts,
        MemoryScope? scope,
        double minScore,
        IScoredLongTermSearch? scoredLongTerm,
        IScoredMessageSearch? scoredMessages,
        IScoredTraceSearch? scoredTraces,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<(Entity Entity, double Score)> entityScores,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<(Fact Fact, double Score)> factScores,
        IReadOnlyList<Preference> preferences,
        IReadOnlyList<(Preference Preference, double Score)> preferenceScores,
        IReadOnlyList<Message> messages,
        IReadOnlyList<(Message Message, double Score)> messageScores,
        IReadOnlyList<ReasoningTrace> traces,
        IReadOnlyList<(ReasoningTrace Trace, double Score)> traceScores,
        CancellationToken cancellationToken)
    {
        var fanOutOptions = _options.FanOut;

        // R8. Checked BEFORE the gate, the deriver and any embedding. A host implementing only the
        // public ILongTermMemoryService seam can never satisfy the internal scored cast, so with
        // fan-out enabled it would pay one derivation and MaxSubQueries embeddings on every fired
        // recall and merge exactly nothing -- silently, because a zero-yield report is
        // indistinguishable from "the store had nothing". Voiding here says why instead, and costs
        // the caller nothing.
        if (scoredLongTerm is null)
        {
            return new FanOutOutcome(
                new RecallFanOutReport
                {
                    GateFired = false,
                    VoidReason = "provider-unsupported",
                },
                entities, facts, preferences, messages, traces, []);
        }

        var firedRules = Array.Empty<string>();
        string deriverId;
        IReadOnlyList<RecallSubQuery> legs;

        if (request.SubQueries is { Count: > 0 } supplied)
        {
            // Known LOW, recorded rather than chased: with MaxSubQueries at 0 the Take below yields an
            // empty set, the leg loop does not execute, and the report comes back fired-with-no-legs
            // and no VoidReason -- readable only by someone who already suspects the cap. The
            // validator added for R6 rejects a zero cap at startup, so this is unreachable through
            // configuration; it survives only for a caller constructing options in code.
            deriverId = "caller";
            legs = supplied.Count > fanOutOptions.MaxSubQueries
                ? supplied.Take(fanOutOptions.MaxSubQueries).ToArray()
                : supplied;
        }
        else
        {
            var gate = RecallFanOutPlanner.EvaluateGate(request.Query, fanOutOptions);
            firedRules = gate.Rules;

            // Signal W, evaluated AFTER the monolithic sections resolved rather than before, because
            // it is a statement about what they came back with: nothing scored well, so the blended
            // query may have been the wrong shape. Only consulted when no pre-retrieval rule fired --
            // a query already known to be compound does not need a second reason.
            var scoreObserved = false;
            var weak = false;
            if (!gate.Fired)
            {
                weak = EvaluateWeakTopScore(
                    fanOutOptions, entityScores, factScores, preferenceScores, out scoreObserved);
            }

            if (weak)
            {
                firedRules = [.. gate.Rules, "W"];
            }
            else if (!gate.Fired)
            {
                // Ran and DECLINED. Distinct from never-ran (a null report), and it cost one token scan.
                // When W was configured but no section published a score, that is recorded rather than
                // read as a confident decline -- an unscored provider must never produce a fake fire OR
                // a fake all-clear.
                var declinedRules = fanOutOptions.WeakTopScoreThreshold is not null && !scoreObserved
                    ? new[] { "W-unscored" }
                    : gate.Rules;

                return new FanOutOutcome(
                    new RecallFanOutReport { GateFired = false, FiredRules = declinedRules },
                    entities, facts, preferences, messages, traces, []);
            }

            deriverId = _subQueryDeriver!.DeriverId;
            legs = await _subQueryDeriver
                .DeriveAsync(request.Query ?? string.Empty, fanOutOptions.MaxSubQueries, cancellationToken)
                .ConfigureAwait(false);

            if (legs.Count == 0)
            {
                // Fired, then derived nothing. VOIDED rather than reported as a zero-yield fan-out: no
                // leg ever ran, so a zero here would not be a measurement of anything.
                return new FanOutOutcome(
                    new RecallFanOutReport
                    {
                        GateFired = true,
                        FiredRules = gate.Rules,
                        DeriverId = deriverId,
                        VoidReason = "derivation-failed",
                    },
                    entities, facts, preferences, messages, traces, []);
            }
        }

        var contributions = new List<LegContribution>(legs.Count);
        var embeddingFailures = 0;

        // R1. The accumulators are (item, score) PAIRS carried forward across legs, not bare item
        // lists. The previous shape re-fed each merge the ORIGINAL monolithic score list, so every
        // leg merged against the pre-fan-out state and all but the last leg's contributions were
        // silently discarded while the item accumulators were dutifully written and never read.
        var entityPairs = PairWithScores(entities, entityScores).ToList();
        var preferencePairs = PairWithScores(preferences, preferenceScores).ToList();
        var factPairs = PairWithScores(facts, factScores).ToList();
        var messagePairs = PairWithScores(messages, messageScores).ToList();
        var tracePairs = PairWithScores(traces, traceScores).ToList();

        // R2. Facts carry an unscored predicate-expansion tail by design (Items is a superset of
        // Scored). Merging on the score list alone DELETED that tail the moment fan-out fired, so a
        // leg that found nothing made the context smaller. Held aside here and re-appended after.
        var scoredFactIds = factPairs.Select(pair => pair.Item.FactId).ToHashSet(StringComparer.Ordinal);
        var expansionTail = facts.Where(fact => !scoredFactIds.Contains(fact.FactId)).ToList();

        foreach (var leg in legs)
        {
            var embedding = leg.QueryEmbedding;
            if (embedding is null || embedding.Length == 0)
            {
                try
                {
                    embedding = await _embeddingOrchestrator
                        .EmbedQueryAsync(leg.QueryText, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception,
                        "Fan-out leg embedding failed for affinity {Affinity}; the leg is skipped.",
                        leg.Affinity);
                    embeddingFailures++;
                    contributions.Add(new LegContribution(leg.Affinity, leg.QueryText, 0, []));
                    continue;
                }
            }

            if (embedding is null || embedding.Length == 0)
            {
                embeddingFailures++;
                contributions.Add(new LegContribution(leg.Affinity, leg.QueryText, 0, []));
                continue;
            }

            var retrieved = 0;
            var contributed = new List<string>();

            foreach (var section in SubQueryAffinityMap.SectionsFor(leg.Affinity))
            {
                if (section == "entities" && recallOpts.MaxEntities > 0)
                {
                    var legRows = await scoredLongTerm.SearchEntitiesWithScoresAsync(
                        embedding, recallOpts.MaxEntities, minScore, scope, cancellationToken)
                        .ConfigureAwait(false);
                    retrieved += legRows.Count;
                    var merge = RecallFanOutMerge.MergeScored(
                        entityPairs, legRows, static e => e.EntityId, recallOpts.MaxEntities);
                    entityPairs = merge.Merged.ToList();
                    contributed.AddRange(merge.UniqueIds);
                }
                else if (section == "facts" && recallOpts.MaxFacts > 0)
                {
                    var legRows = await scoredLongTerm.SearchFactsWithScoresAsync(
                        embedding, recallOpts.MaxFacts, minScore, scope,
                        expandByPredicate: false, expansionLimit: 0,
                        questionRelations: Array.Empty<string>(), cancellationToken)
                        .ConfigureAwait(false);
                    retrieved += legRows.Facts.Count;
                    var merge = RecallFanOutMerge.MergeScored(
                        factPairs, legRows.Scored, static f => f.FactId, recallOpts.MaxFacts);
                    factPairs = merge.Merged.ToList();
                    contributed.AddRange(merge.UniqueIds);
                }
                else if (section == "messages" && recallOpts.MaxRelevantMessages > 0
                         && scoredMessages is not null)
                {
                    // R3. This arm and the trace arm below were PUBLISHED in the affinity map and
                    // never implemented, so an Episodic or Procedural leg paid for a live embedding
                    // and could not retrieve anything in any mode -- reporting ItemsRetrieved=0,
                    // indistinguishable from "the store had nothing". Advertised destinations that
                    // silently retrieve nothing are the exact mechanism-substituted shape the router
                    // audit existed to kill.
                    //
                    // Session-scoped, matching the monolithic message search: a fan-out must not widen
                    // the session boundary the ordinary path respects.
                    var legRows = await scoredMessages.SearchMessagesWithScoresAsync(
                        request.SessionId, embedding, recallOpts.MaxRelevantMessages, minScore,
                        cancellationToken).ConfigureAwait(false);
                    retrieved += legRows.Count;
                    var merge = RecallFanOutMerge.MergeScored(
                        messagePairs, legRows, static m => m.MessageId, recallOpts.MaxRelevantMessages);
                    messagePairs = merge.Merged.ToList();
                    contributed.AddRange(merge.UniqueIds);
                }
                else if (section == "traces" && recallOpts.MaxTraces > 0 && scoredTraces is not null)
                {
                    var legRows = await scoredTraces.SearchSimilarTracesWithScoresAsync(
                        embedding, recallOpts.SuccessfulTracesOnly, recallOpts.MaxTraces,
                        recallOpts.EffectiveTraceMinScore, scope, cancellationToken)
                        .ConfigureAwait(false);
                    retrieved += legRows.Count;
                    var merge = RecallFanOutMerge.MergeScored(
                        tracePairs, legRows, static t => t.TraceId, recallOpts.MaxTraces);
                    tracePairs = merge.Merged.ToList();
                    contributed.AddRange(merge.UniqueIds);
                }
                else if (section == "preferences" && recallOpts.MaxPreferences > 0)
                {
                    var legRows = await scoredLongTerm.SearchPreferencesWithScoresAsync(
                        embedding, recallOpts.MaxPreferences, minScore, scope, cancellationToken)
                        .ConfigureAwait(false);
                    retrieved += legRows.Count;
                    var merge = RecallFanOutMerge.MergeScored(
                        preferencePairs, legRows, static p => p.PreferenceId, recallOpts.MaxPreferences);
                    preferencePairs = merge.Merged.ToList();
                    contributed.AddRange(merge.UniqueIds);
                }
            }

            contributions.Add(
                new LegContribution(leg.Affinity, leg.QueryText, retrieved, contributed));
        }

        // The expansion tail rejoins under its own allowance, exactly as the monolithic path composes
        // it: MaxFacts scored rows plus up to MaxExpandedFacts unscored ones.
        var mergedFacts = factPairs.Select(pair => pair.Item).ToList();
        if (expansionTail.Count > 0)
        {
            var tailCap = recallOpts.ExpandFactsByPredicate ? recallOpts.MaxExpandedFacts : expansionTail.Count;
            mergedFacts.AddRange(expansionTail.Take(Math.Max(0, tailCap)));
        }

        return new FanOutOutcome(
            new RecallFanOutReport
            {
                GateFired = true,
                FiredRules = firedRules,
                DeriverId = deriverId,
                VoidReason = embeddingFailures > 0
                    ? string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"embedding-failed:{embeddingFailures}/{legs.Count}")
                    : null,
            },
            entityPairs.Select(pair => pair.Item).ToList(),
            mergedFacts,
            preferencePairs.Select(pair => pair.Item).ToList(),
            messagePairs.Select(pair => pair.Item).ToList(),
            tracePairs.Select(pair => pair.Item).ToList(),
            contributions);
    }

    /// <summary>
    /// Signal W — the best monolithic score is below the configured floor.
    /// </summary>
    /// <remarks>
    /// <paramref name="scoreObserved"/> exists so an unscored provider can be told apart from a
    /// genuinely weak result. Without it, a provider that publishes no scores at all would look
    /// exactly like one whose every score sat below the threshold, and W would either fire on
    /// nothing or decline on nothing — both fabrications.
    /// </remarks>
    private static bool EvaluateWeakTopScore(
        RecallFanOutOptions options,
        IReadOnlyList<(Entity Entity, double Score)> entityScores,
        IReadOnlyList<(Fact Fact, double Score)> factScores,
        IReadOnlyList<(Preference Preference, double Score)> preferenceScores,
        out bool scoreObserved)
    {
        scoreObserved = false;
        if (options.WeakTopScoreThreshold is not { } threshold) return false;

        var best = double.MinValue;

        foreach (var scored in entityScores) { scoreObserved = true; if (scored.Score > best) best = scored.Score; }
        foreach (var scored in factScores) { scoreObserved = true; if (scored.Score > best) best = scored.Score; }
        foreach (var scored in preferenceScores) { scoreObserved = true; if (scored.Score > best) best = scored.Score; }

        // No score anywhere: W cannot form an opinion, and inventing one either way would be worse
        // than staying silent.
        if (!scoreObserved) return false;

        return best < threshold;
    }

    private static SubQueryYield EmptyYield(RecallSubQuery leg) => new()
    {
        Affinity = leg.Affinity,
        QueryText = leg.QueryText,
        ItemsRetrieved = 0,
        UniqueContributions = 0,
        SurvivedBudget = 0,
    };

    /// <summary>
    /// Re-pairs a section with its scores, deriving them from rank when the section is unscored.
    /// </summary>
    /// <remarks>
    /// The fallback descends by position rather than using a constant. A constant would make every
    /// monolithic row tie with every other, and the merge's tie-break would then be free to reorder a
    /// section the blended query had already ranked.
    /// </remarks>
    private static IReadOnlyList<(T Item, double Score)> PairWithScores<T>(
        IReadOnlyList<T> items, IReadOnlyList<(T Item, double Score)> scores)
    {
        if (scores.Count > 0) return scores;

        var paired = new List<(T, double)>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            paired.Add((items[index], 1.0 - (index / (double)Math.Max(items.Count, 1))));
        }

        return paired;
    }

}
