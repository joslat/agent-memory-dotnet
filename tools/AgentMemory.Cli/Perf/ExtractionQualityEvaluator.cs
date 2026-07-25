using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

/// <summary>Precision and recall for one memory kind.</summary>
public sealed record KindScore(int Expected, int Produced, int Matched)
{
    /// <summary>Of what was produced, how much was wanted. 1.0 when nothing was produced and none was wanted.</summary>
    public double Precision => Produced == 0 ? (Expected == 0 ? 1.0 : 0.0) : (double)Matched / Produced;

    /// <summary>Of what was wanted, how much was produced. 1.0 when nothing was wanted.</summary>
    public double Recall => Expected == 0 ? 1.0 : (double)Matched / Expected;
}

/// <summary>Scores for one judged extraction case.</summary>
public sealed record ExtractionCaseResult(
    string CaseId,
    string Category,
    bool ExpectNothing,
    KindScore Entities,
    KindScore Facts,
    KindScore Preferences,
    bool FalsePositive,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unexpected)
{
    public bool Clean => Missing.Count == 0 && Unexpected.Count == 0 && !FalsePositive;
}

/// <summary>Aggregate extraction-quality scores for a run.</summary>
public sealed record ExtractionQualityResult(
    double EntityPrecision, double EntityRecall,
    double FactPrecision, double FactRecall,
    double PreferencePrecision, double PreferenceRecall,
    int Cases,
    int ExpectNothingCases,
    int FalsePositives,
    IReadOnlyList<ExtractionCaseResult> CaseResults)
{
    /// <summary>False-positive rate over the cases where the correct behaviour is to learn nothing.</summary>
    public double FalsePositiveRate =>
        ExpectNothingCases == 0 ? 0.0 : (double)FalsePositives / ExpectNothingCases;

    public bool Clean => CaseResults.All(c => c.Clean);
}

/// <summary>
/// Runs the judged extraction fixture and scores it. Deterministic and free — the model is scripted
/// and matching is normalized string comparison.
/// </summary>
public sealed class ExtractionQualityEvaluator
{
    private readonly ExtractionQualityFixture _fixture;
    private readonly IServiceProvider _services;
    private readonly IDriver _driver;

    public ExtractionQualityEvaluator(
        ExtractionQualityFixture fixture, IServiceProvider services, IDriver driver)
    {
        _fixture = fixture;
        _services = services;
        _driver = driver;
    }

    public async Task<ExtractionQualityResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        var memory = _services.GetRequiredService<IMemoryService>();
        var clock = _services.GetRequiredService<IClock>();
        var results = new List<ExtractionCaseResult>();

        foreach (var testCase in _fixture.Cases)
        {
            // Own owner and session per case. Extraction deduplicates and resolves against existing
            // memory, so sharing an owner would let case N's facts merge into case N-1's and the score
            // would depend on fixture ORDER — which is exactly the kind of hidden coupling that makes a
            // quality number untrustworthy.
            var owner = $"{_fixture.OwnerPrefix}-{testCase.Id}";
            var sessionId = $"{owner}-session";
            var now = clock.UtcNow;

            var messages = testCase.Messages.Select((m, i) => new Message
            {
                MessageId = $"{owner}-msg-{i}",
                SessionId = sessionId,
                ConversationId = $"{owner}-conv",
                Role = m.Role,
                Content = m.Content,
                TimestampUtc = now.AddSeconds(i),
            }).ToList();

            await memory.ExtractAndPersistAsync(new ExtractionRequest
            {
                Messages = messages,
                SessionId = sessionId,
                UserId = owner,
            }, cancellationToken).ConfigureAwait(false);

            var learned = await ReadLearnedAsync(owner, cancellationToken).ConfigureAwait(false);
            results.Add(Score(testCase, learned));
        }

        return Aggregate(results);
    }

    /// <summary>What the system actually ended up knowing for this case's owner.</summary>
    /// <remarks>
    /// <para>
    /// Read back from the graph, deliberately, <b>not</b> from <c>ExtractionResult</c>. That type's
    /// <c>Entities</c>/<c>Facts</c>/<c>Preferences</c> are populated from the pipeline's <c>Raw*</c>
    /// collections — everything the extractor returned, <em>before</em> confidence filtering, entity
    /// resolution and dedup. Scoring against it would mean a sub-threshold item counted as "learned"
    /// when the pipeline correctly discarded it, and would make the whole persistence half of the
    /// pipeline invisible to this guard.
    /// </para>
    /// <para>
    /// That distinction is the entire point here: ranks 2, 4 and 8 change what the system <em>learns</em>,
    /// not what an extractor <em>proposes</em>.
    /// </para>
    /// </remarks>
    private async Task<LearnedMemory> ReadLearnedAsync(string owner, CancellationToken cancellationToken)
    {
        await using var session = _driver.AsyncSession();
        var cursor = await session.RunAsync(
            @"MATCH (n)
              WHERE n.owner_id = $owner AND (n:Entity OR n:Fact OR n:Preference)
              RETURN labels(n)[0] AS label,
                     n.name       AS name,
                     n.subject    AS subject,
                     n.predicate  AS predicate,
                     n.object     AS object,
                     n.preference AS preference",
            new { owner }).ConfigureAwait(false);

        var entities = new List<string>();
        var facts = new List<string>();
        var preferences = new List<string>();

        var records = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            switch (record["label"]?.ToString())
            {
                case "Entity":
                    if (record["name"]?.ToString() is { Length: > 0 } name) entities.Add(Normalize(name));
                    break;
                case "Fact":
                    facts.Add(Normalize(
                        $"{record["subject"]}|{record["predicate"]}|{record["object"]}"));
                    break;
                case "Preference":
                    if (record["preference"]?.ToString() is { Length: > 0 } pref) preferences.Add(Normalize(pref));
                    break;
            }
        }

        return new LearnedMemory(entities, facts, preferences);
    }

    private sealed record LearnedMemory(
        List<string> Entities, List<string> Facts, List<string> Preferences);

    /// <summary>
    /// Scores one case against what the system actually learned. See <see cref="ReadLearnedAsync"/>
    /// for why that is read from the graph rather than taken from <c>ExtractionResult</c>.
    /// </summary>
    private static ExtractionCaseResult Score(ExtractionCase testCase, LearnedMemory learned)
    {
        var producedEntities = learned.Entities;
        var producedFacts = learned.Facts;
        var producedPrefs = learned.Preferences;

        var expectedEntities = testCase.ExpectEntities.Select(Normalize).ToList();
        var expectedFacts = testCase.ExpectFacts.Select(f => Normalize(f.ToString())).ToList();
        var expectedPrefs = testCase.ExpectPreferences.Select(Normalize).ToList();

        var entities = ScoreKind(expectedEntities, producedEntities);
        var facts = ScoreKind(expectedFacts, producedFacts);
        var prefs = ScoreKind(expectedPrefs, producedPrefs);

        var missing = Missing(expectedEntities, producedEntities, "entity")
            .Concat(Missing(expectedFacts, producedFacts, "fact"))
            .Concat(Missing(expectedPrefs, producedPrefs, "preference"))
            .ToList();

        var unexpected = Missing(producedEntities, expectedEntities, "entity")
            .Concat(Missing(producedFacts, expectedFacts, "fact"))
            .Concat(Missing(producedPrefs, expectedPrefs, "preference"))
            .ToList();

        // A false positive is learning ANYTHING on a turn that should have taught us nothing. This is
        // the number a salience gate must not move: skipping turns is only safe if the turns skipped
        // were genuinely empty.
        var producedAnything = producedEntities.Count + producedFacts.Count + producedPrefs.Count > 0;
        var falsePositive = testCase.ExpectNothing && producedAnything;

        return new ExtractionCaseResult(
            testCase.Id, testCase.Category, testCase.ExpectNothing,
            entities, facts, prefs, falsePositive, missing, unexpected);
    }

    private static KindScore ScoreKind(List<string> expected, List<string> produced)
    {
        var producedSet = produced.ToHashSet(StringComparer.Ordinal);
        var matched = expected.Count(producedSet.Contains);
        return new KindScore(expected.Count, produced.Count, matched);
    }

    private static IEnumerable<string> Missing(List<string> wanted, List<string> got, string kind)
    {
        var gotSet = got.ToHashSet(StringComparer.Ordinal);
        return wanted.Where(w => !gotSet.Contains(w)).Select(w => $"{kind}:{w}");
    }

    /// <summary>
    /// Case- and whitespace-insensitive comparison. Deliberately simple and documented: a matcher that
    /// is clever is a matcher whose verdicts cannot be predicted from the fixture, and a quality gate
    /// whose result cannot be predicted is not a gate.
    /// </summary>
    private static string Normalize(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ExtractionQualityResult Aggregate(List<ExtractionCaseResult> results)
    {
        double Avg(Func<ExtractionCaseResult, double> selector) =>
            results.Count == 0 ? 0 : results.Average(selector);

        return new ExtractionQualityResult(
            EntityPrecision: Avg(r => r.Entities.Precision),
            EntityRecall: Avg(r => r.Entities.Recall),
            FactPrecision: Avg(r => r.Facts.Precision),
            FactRecall: Avg(r => r.Facts.Recall),
            PreferencePrecision: Avg(r => r.Preferences.Precision),
            PreferenceRecall: Avg(r => r.Preferences.Recall),
            Cases: results.Count,
            ExpectNothingCases: results.Count(r => r.ExpectNothing),
            FalsePositives: results.Count(r => r.FalsePositive),
            CaseResults: results);
    }
}
