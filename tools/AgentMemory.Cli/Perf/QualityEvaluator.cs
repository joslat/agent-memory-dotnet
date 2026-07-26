using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Cli.Perf;

/// <summary>Scores for one judged case.</summary>
public sealed record QualityCaseResult(
    string CaseId,
    string Category,
    string Kind,
    double RecallAtK,
    double ReciprocalRank,
    int Retrieved,
    int RelevantExpected,
    IReadOnlyList<string> Violations);

/// <summary>Aggregate retrieval-quality scores for a run.</summary>
public sealed record QualityResult(
    double RecallAtK,
    double Mrr,
    int Cases,
    int CasesWithViolations,
    IReadOnlyDictionary<string, double> RecallByCategory,
    IReadOnlyList<QualityCaseResult> CaseResults)
{
    /// <summary>True when every relevant memory was retrieved and no forbidden memory appeared.</summary>
    public bool Clean => RecallAtK >= 1.0 && CasesWithViolations == 0;
}

/// <summary>
/// Runs the judged retrieval fixture and scores it. Deterministic and free — no model is involved
/// anywhere in seeding, retrieval, or scoring.
/// </summary>
/// <remarks>
/// <para>
/// This is the guard that the roadmap's quality-risk optimizations are blocked on. Counters can prove a
/// change made recall <em>cheaper</em>; only this can show it did not make recall <em>worse</em>. A
/// selective-recall policy that drops a category, or a ranking change that buries the right answer,
/// moves these numbers and moves no counter at all.
/// </para>
/// <para>
/// Seeded under its own owner id so it is fully isolated from the throughput fixture, whose items are
/// deliberately near-identical to one probe query — useful for driving recall to its limits, useless
/// for judging relevance.
/// </para>
/// </remarks>
public sealed class QualityEvaluator
{
    private readonly QualityFixture _fixture;
    private readonly IServiceProvider _services;
    private readonly int _dimensions;
    private readonly RecallOptions _recallOptions;

    public QualityEvaluator(
        QualityFixture fixture,
        IServiceProvider services,
        int dimensions,
        RecallOptions? recallOptions = null)
    {
        _fixture = fixture;
        _services = services;
        _dimensions = dimensions;
        _recallOptions = recallOptions ?? RecallOptions.Default;
    }

    public async Task SeedAsync(TextWriter log, CancellationToken cancellationToken)
    {
        var longTerm = _services.GetRequiredService<ILongTermMemoryService>();
        var clock = _services.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        foreach (var (topicName, topic) in _fixture.Topics)
        {
            foreach (var entity in topic.Entities)
            {
                await longTerm.AddEntityAsync(new Entity
                {
                    EntityId = entity.Id,
                    Name = entity.Name,
                    Type = "CONCEPT",
                    Description = entity.Text,
                    Confidence = 0.95,
                    OwnerId = _fixture.OwnerId,
                    CreatedAtUtc = now,
                    Embedding = DeterministicEmbeddingGenerator.Vector(entity.Text, _dimensions),
                }, cancellationToken).ConfigureAwait(false);
            }

            foreach (var fact in topic.Facts)
            {
                await longTerm.AddFactAsync(new Fact
                {
                    FactId = fact.Id,
                    Subject = fact.Subject,
                    Predicate = fact.Predicate,
                    Object = fact.Object,
                    Confidence = 0.9,
                    OwnerId = _fixture.OwnerId,
                    CreatedAtUtc = now,
                    Embedding = DeterministicEmbeddingGenerator.Vector(fact.Text, _dimensions),
                }, cancellationToken).ConfigureAwait(false);
            }

            foreach (var preference in topic.Preferences)
            {
                await longTerm.AddPreferenceAsync(new Preference
                {
                    PreferenceId = preference.Id,
                    Category = preference.Category,
                    PreferenceText = preference.Text,
                    Confidence = 0.9,
                    OwnerId = _fixture.OwnerId,
                    CreatedAtUtc = now,
                    Embedding = DeterministicEmbeddingGenerator.Vector(preference.Text, _dimensions),
                }, cancellationToken).ConfigureAwait(false);
            }

            _ = topicName;
        }

        // Seeding through AddEntityAsync/AddFactAsync/AddPreferenceAsync means dedup-on-create applies,
        // exactly as it would for a real caller. Verify every fixture id actually landed: a silently
        // deduplicated item would make its case unscoreable while the run still looked healthy.
        var missing = await FindMissingAsync(cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Quality fixture did not fully seed — missing {missing.Count} id(s): " +
                $"{string.Join(", ", missing.Take(10))}. Two fixture items are probably too similar and " +
                "were deduplicated on create; give them more distinct wording.");
        }

        log.WriteLine($"perf: seeded quality fixture — {_fixture.AllSeededIds().Count()} judged items " +
                      $"across {_fixture.Topics.Count} topics.");
    }

    private async Task<List<string>> FindMissingAsync(CancellationToken cancellationToken)
    {
        // Straight to the repositories: this checks that the node exists under the id the fixture
        // declares, which is what scoring matches on. The service layer offers no by-id read.
        var entities = _services.GetRequiredService<IEntityRepository>();
        var facts = _services.GetRequiredService<IFactRepository>();
        var preferences = _services.GetRequiredService<IPreferenceRepository>();
        var missing = new List<string>();

        foreach (var (_, topic) in _fixture.Topics)
        {
            foreach (var entity in topic.Entities)
                if (await entities.GetByIdAsync(entity.Id, cancellationToken).ConfigureAwait(false) is null)
                    missing.Add(entity.Id);
            foreach (var fact in topic.Facts)
                if (await facts.GetByIdAsync(fact.Id, cancellationToken).ConfigureAwait(false) is null)
                    missing.Add(fact.Id);
            foreach (var preference in topic.Preferences)
                if (await preferences.GetByIdAsync(preference.Id, cancellationToken).ConfigureAwait(false) is null)
                    missing.Add(preference.Id);
        }

        return missing;
    }

    public async Task<QualityResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        var memory = _services.GetRequiredService<IMemoryService>();
        var embedder = _services.GetRequiredService<IEmbeddingOrchestrator>();
        var results = new List<QualityCaseResult>();

        foreach (var testCase in _fixture.Cases)
        {
            var embedding = await embedder.EmbedQueryAsync(testCase.Query, cancellationToken).ConfigureAwait(false);

            var recall = await memory.RecallAsync(new RecallRequest
            {
                SessionId = _fixture.SessionId,
                UserId = _fixture.OwnerId,
                Query = testCase.Query,
                QueryEmbedding = embedding,
                Options = _recallOptions,
            }, cancellationToken).ConfigureAwait(false);

            results.Add(Score(testCase, RankedIds(recall.Context, testCase.Kind)));
        }

        return Aggregate(results);
    }

    /// <summary>
    /// The retrieved ids for the case's kind, in the order the memory layer ranked them.
    /// </summary>
    private static IReadOnlyList<string> RankedIds(MemoryContext context, string kind) => kind switch
    {
        "entity" => context.RelevantEntities.Items.Select(e => e.EntityId).ToList(),
        "fact" => context.RelevantFacts.Items.Select(f => f.FactId).ToList(),
        "preference" => context.RelevantPreferences.Items.Select(p => p.PreferenceId).ToList(),
        _ => throw new ArgumentException($"Unknown fixture kind '{kind}'. Use entity, fact, or preference."),
    };

    private static QualityCaseResult Score(QualityCase testCase, IReadOnlyList<string> retrieved)
    {
        var relevant = testCase.Relevant.ToHashSet(StringComparer.Ordinal);

        // Recall@K — of the memories that SHOULD have come back, how many did. This is the metric a
        // selective-recall policy breaks: it drops items and no counter notices.
        var found = retrieved.Count(relevant.Contains);
        var recallAtK = relevant.Count == 0 ? 1.0 : (double)found / relevant.Count;

        // Reciprocal rank — how far down the first correct answer sat. This is the metric a ranking
        // change breaks while Recall@K stays a perfect 1.0.
        var firstHit = -1;
        for (var i = 0; i < retrieved.Count; i++)
        {
            if (relevant.Contains(retrieved[i])) { firstHit = i; break; }
        }
        var reciprocalRank = firstHit < 0 ? 0.0 : 1.0 / (firstHit + 1);

        // Forbidden ids that appeared anyway — the failure a perfect Recall@K hides.
        var violations = testCase.MustNotRetrieve
            .Where(retrieved.Contains)
            .ToList();

        return new QualityCaseResult(
            testCase.Id, testCase.Category, testCase.Kind,
            recallAtK, reciprocalRank, retrieved.Count, relevant.Count, violations);
    }

    private static QualityResult Aggregate(List<QualityCaseResult> results)
    {
        if (results.Count == 0)
            return new QualityResult(0, 0, 0, 0, new Dictionary<string, double>(), results);

        var byCategory = results
            .GroupBy(r => r.Category, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Average(r => r.RecallAtK), StringComparer.Ordinal);

        return new QualityResult(
            RecallAtK: results.Average(r => r.RecallAtK),
            Mrr: results.Average(r => r.ReciprocalRank),
            Cases: results.Count,
            CasesWithViolations: results.Count(r => r.Violations.Count > 0),
            RecallByCategory: byCategory,
            CaseResults: results);
    }
}
