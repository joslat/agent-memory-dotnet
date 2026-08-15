using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Domain.Extraction;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Extraction.Llm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Compares what the per-kind and unified extractors actually EXTRACT from identical messages.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseUnifiedExtraction</c> ships <c>false</c> and its XML doc says it flips "once the unified path
/// passes live extraction-quality acceptance". Every quality number in the measurement plan was
/// produced by neither of these two paths — all 55 runs used the <i>multi-session batch</i> extractor
/// via <c>ExtractBatchAsync</c> — so the acceptance evidence never covered the flag.
/// </para>
/// <para>
/// <b>Accuracy is the wrong instrument for that question.</b> It runs the whole pipeline, needs a cold
/// build (~107 min), and is then swamped by a between-build variance of 4.48 points. The condition is
/// about <i>what gets extracted</i>, which can be compared directly and deterministically: same
/// messages into each extractor, then diff the output. No judge, no answer model, no database, no
/// noise floor.
/// </para>
/// <para>
/// It also reports something accuracy structurally cannot: whether the two paths extract the
/// <i>same memories at all</i>, and which fields each populates. <c>Subtype</c> and <c>Description</c>
/// are dropped by the unified prompt, and no accuracy figure would ever surface that.
/// </para>
/// </remarks>
internal static class LongMemEvalExtractionCompareProgram
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var datasetPath = Value(args, "--dataset")
            ?? throw new ArgumentException("--dataset <longmemeval_s_cleaned.json> is required.");
        var units = int.TryParse(Value(args, "--units"), out var parsed) ? parsed : 10;
        // A question's full history spans many sessions and can exceed 400 messages, which is neither
        // how extraction runs in production (it works per session) nor something a single prompt can
        // hold. Capping turns keeps each unit session-sized and, more importantly, keeps the two arms
        // comparable - both receive byte-identical input.
        var turns = int.TryParse(Value(args, "--turns"), out var parsedTurns) ? parsedTurns : 15;
        // --repeat runs ONE arm twice over identical input and reports how much it agrees with
        // itself. That self-agreement is the baseline every cross-extractor Jaccard in this plan
        // should have been read against, and it is the only way to tell whether --extraction-seed
        // actually buys reproducibility on this deployment.
        var repeat = args.Contains("--repeat", StringComparer.Ordinal);
        var extractionSeed = int.TryParse(Value(args, "--extraction-seed"), out var parsedExtraction)
            ? (int?)parsedExtraction : null;
        var seed = int.TryParse(Value(args, "--seed"), out var parsedSeed) ? parsedSeed : 42;
        var output = Value(args, "--output")
            ?? (repeat
                ? $"artifacts/evaluation/extraction-self-agreement-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json"
                : $"artifacts/evaluation/extraction-compare-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json");

        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(deployment))
        {
            Console.Error.WriteLine(
                "extraction-compare: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY and "
                + "AZURE_OPENAI_DEPLOYMENT must be set.");
            return 1;
        }

        var slices = LoadSlices(datasetPath, units, seed, turns);
        Console.WriteLine(
            $"extraction-compare: {slices.Count} units, seed {seed}, identical messages to both paths.");

        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        var extractionDeployment =
            Environment.GetEnvironmentVariable("AZURE_OPENAI_EXTRACTION_DEPLOYMENT") ?? deployment;
        // Same wrapper the prepared-pair path uses. The deployment rejects an explicit temperature of
        // 0 ("only the default (1) value is supported"), and this is where that is already handled -
        // reaching for a second workaround would have measured a different client than every other run.
        var chatClient = new ProviderCompatibleExtractionChatClient(
            azureClient.GetChatClient(extractionDeployment).AsIChatClient());

        if (repeat)
        {
            // 30.1. Which extractor is repeated matters and used to be unaskable: this arm was pinned
            // to PerKind, while every recorded quality number in the archive came from the
            // multi-session batch path. A seed that pins one says nothing about the other, so the arm
            // is now selectable and RECORDED. Default stays PerKind, so prior invocations mean what
            // they meant.
            var arm = ParseArm(Value(args, "--arm"));
            var first = await RunPathAsync(
                "run-1", slices, chatClient, extractionDeployment, arm, extractionSeed)
                .ConfigureAwait(false);
            var second = await RunPathAsync(
                "run-2", slices, chatClient, extractionDeployment, arm, extractionSeed)
                .ConfigureAwait(false);
            var selfJaccard = Jaccard(first, second);
            Console.WriteLine(
                $"SELF-AGREEMENT (arm={arm}, extraction seed={(extractionSeed?.ToString() ?? "none")}): "
                + $"run-1 {first.Facts.Count} facts, run-2 {second.Facts.Count} facts, "
                + $"Jaccard={selfJaccard:F3}");
            Console.WriteLine(
                "  Reference: three cold builds of one configuration agreed at Jaccard 0.17.");

            // 30.1 acceptance: the overlap number is recorded EITHER WAY. A seed that turns out to do
            // nothing on this deployment is a measured property of the provider, and a result that
            // only ever existed in a console scrollback is a result nobody can cite later.
            var repeatReport = new SelfAgreementReport
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Dataset = Path.GetFileName(datasetPath),
                Units = slices.Count,
                Seed = seed,
                TurnsPerUnit = turns,
                Arm = arm.ToString(),
                ExtractionSeed = extractionSeed,
                Run1 = Summarise(first),
                Run2 = Summarise(second),
                SelfJaccard = selfJaccard,
                SharedFactTriples = SharedTriples(first, second),
                UnionFactTriples = UnionTriples(first, second),
                UnseededColdBuildReference = 0.17,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(
                output,
                JsonSerializer.Serialize(
                    repeatReport, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);
            Console.WriteLine($"extraction-compare: wrote {output}");
            return 0;
        }

        var perKind = await RunPathAsync(
            "per-kind", slices, chatClient, extractionDeployment, Arm.PerKind).ConfigureAwait(false);
        var unifiedPath = await RunPathAsync(
            "unified", slices, chatClient, extractionDeployment, Arm.Unified).ConfigureAwait(false);
        // The third arm is the one that matters most for interpreting this plan: every recorded
        // quality number came from THIS extractor, and neither arm above describes it.
        var batchPath = await RunPathAsync(
            "multi-session-batch", slices, chatClient, extractionDeployment, Arm.Batch)
            .ConfigureAwait(false);

        var report = Compare(perKind, unifiedPath, batchPath, slices.Count, seed, turns, datasetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine($"extraction-compare: wrote {output}");
        Console.WriteLine(
            $"  per-kind : {perKind.Facts.Count} facts, {perKind.Entities.Count} entities, "
            + $"{perKind.Calls} calls");
        Console.WriteLine(
            $"  unified  : {unifiedPath.Facts.Count} facts, {unifiedPath.Entities.Count} entities, "
            + $"{unifiedPath.Calls} calls");
        Console.WriteLine(
            $"  batch    : {batchPath.Facts.Count} facts, {batchPath.Entities.Count} entities, "
            + $"{batchPath.Calls} calls");
        Console.WriteLine(
            $"  fact triple overlap (Jaccard): {report.FactTripleJaccard:F3}   "
            + $"shared {report.SharedFactTriples} of {report.UnionFactTriples}");
        return 0;
    }

    private static async Task<PathResult> RunPathAsync(
        string name,
        IReadOnlyList<IReadOnlyList<Message>> slices,
        IChatClient chatClient,
        string deployment,
        Arm arm,
        int? seed = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(chatClient);
        services.AddLlmExtraction(options =>
        {
            options.ModelId = deployment;
            options.Temperature = 0;
            options.MaxRetries = 2;
            options.UseJsonResponseFormat = true;
            // The ONLY difference between the two arms. Multi-session batching is deliberately off in
            // both: it is a third path, already covered by 55 runs, and mixing it in would confound
            // the comparison this probe exists to make.
            options.UseUnifiedExtraction = arm != Arm.PerKind;
            options.UseMultiSessionBatchExtraction = arm == Arm.Batch;
            options.Seed = seed;
        });
        var provider = services.BuildServiceProvider();

        var result = new PathResult(name);
        for (var index = 0; index < slices.Count; index++)
        {
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;
            var messages = slices[index];
            if (arm == Arm.Batch)
            {
                var extractor = sp.GetServices<IMultiSessionUnifiedMemoryExtractor>()
                    .First(candidate => candidate.IsEnabled);
                var request = new ExtractionRequest
                {
                    Messages = messages,
                    SessionId = messages[0].SessionId,
                };
                // One session per call here, so the batch prompt is compared on the SAME input as the
                // other two arms. Its cross-session attribution rules still apply - that is part of
                // what makes it a different extractor rather than a batching detail.
                var extracted = await extractor
                    .ExtractAsync([request], maxSessionsPerBatch: 1, maxInputTokens: 50_000)
                    .ConfigureAwait(false);
                var single = extracted.Values.FirstOrDefault() ?? new UnifiedExtractionResult();
                result.Add(single.Entities, single.Facts, single.Preferences,
                    single.Relationships, calls: 1);
            }
            else if (arm == Arm.Unified)
            {
                var extractor = sp.GetServices<IUnifiedMemoryExtractor>()
                    .First(candidate => candidate.IsEnabled);
                var extracted = await extractor.ExtractAsync(messages).ConfigureAwait(false);
                result.Add(extracted.Entities, extracted.Facts, extracted.Preferences,
                    extracted.Relationships, calls: 1);
            }
            else
            {
                // Four calls, one per kind - which is the cost half of the comparison and is counted
                // rather than assumed.
                var entities = await sp.GetRequiredService<IEntityExtractor>()
                    .ExtractAsync(messages).ConfigureAwait(false);
                var facts = await sp.GetRequiredService<IFactExtractor>()
                    .ExtractAsync(messages).ConfigureAwait(false);
                var preferences = await sp.GetRequiredService<IPreferenceExtractor>()
                    .ExtractAsync(messages).ConfigureAwait(false);
                var relationships = await sp.GetRequiredService<IRelationshipExtractor>()
                    .ExtractAsync(messages).ConfigureAwait(false);
                result.Add(entities, facts, preferences, relationships, calls: 4);
            }

            Console.WriteLine($"extraction-compare: {name} {index + 1}/{slices.Count}");
        }

        return result;
    }

    /// <summary>Canonicalized so "Alice" and "alice " are the same triple, exactly as the write path keys.</summary>
    private static string TripleKey(ExtractedFact fact) =>
        MemoryTripleCanonicalizer.CanonicalValue(fact.Subject) + '|'
        + MemoryTripleCanonicalizer.Canonical(fact.Predicate) + '|'
        + MemoryTripleCanonicalizer.CanonicalValue(fact.Object);

    private static ComparisonReport Compare(
        PathResult perKind, PathResult unified, PathResult batch,
        int units, int seed, int turns, string dataset)
    {
        var a = perKind.Facts.Select(TripleKey).ToHashSet(StringComparer.Ordinal);
        var b = unified.Facts.Select(TripleKey).ToHashSet(StringComparer.Ordinal);
        var shared = a.Intersect(b, StringComparer.Ordinal).Count();
        var union = a.Union(b, StringComparer.Ordinal).Count();

        return new ComparisonReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Dataset = Path.GetFileName(dataset),
            Units = units,
            Seed = seed,
            TurnsPerUnit = turns,
            PerKind = Summarise(perKind),
            Unified = Summarise(unified),
            MultiSessionBatch = Summarise(batch),
            BatchVsPerKindJaccard = Jaccard(batch, perKind),
            BatchVsUnifiedJaccard = Jaccard(batch, unified),
            SharedFactTriples = shared,
            UnionFactTriples = union,
            FactTripleJaccard = union == 0 ? 0 : (double)shared / union,
            OnlyInPerKind = a.Except(b, StringComparer.Ordinal).Take(40).ToArray(),
            OnlyInUnified = b.Except(a, StringComparer.Ordinal).Take(40).ToArray(),
        };
    }

    private static double Jaccard(PathResult left, PathResult right)
    {
        var a = left.Facts.Select(TripleKey).ToHashSet(StringComparer.Ordinal);
        var b = right.Facts.Select(TripleKey).ToHashSet(StringComparer.Ordinal);
        var union = a.Union(b, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)a.Intersect(b, StringComparer.Ordinal).Count() / union;
    }

    private static int SharedTriples(PathResult left, PathResult right) =>
        left.Facts.Select(TripleKey).ToHashSet(StringComparer.Ordinal)
            .Intersect(right.Facts.Select(TripleKey), StringComparer.Ordinal).Count();

    private static int UnionTriples(PathResult left, PathResult right) =>
        left.Facts.Select(TripleKey).ToHashSet(StringComparer.Ordinal)
            .Union(right.Facts.Select(TripleKey), StringComparer.Ordinal).Count();

    /// <summary>Parses <c>--arm per-kind|unified|batch</c>; absent keeps the historical PerKind.</summary>
    private static Arm ParseArm(string? value) => (value ?? "per-kind").ToLowerInvariant() switch
    {
        "per-kind" or "perkind" or "" => Arm.PerKind,
        "unified" => Arm.Unified,
        "batch" or "multi-session-batch" => Arm.Batch,
        var other => throw new ArgumentException(
            $"--arm must be per-kind, unified or batch; got '{other}'."),
    };

    private enum Arm
    {
        PerKind,
        Unified,
        Batch,
    }

    private static PathSummary Summarise(PathResult path) => new()
    {
        Path = path.Name,
        Calls = path.Calls,
        Entities = path.Entities.Count,
        Facts = path.Facts.Count,
        Preferences = path.Preferences.Count,
        Relationships = path.Relationships.Count,
        // FIELD COMPLETENESS - the half no accuracy number can report. The unified prompt asks for
        // neither subtype nor description, so these are expected to be 0 on that arm; measuring rather
        // than asserting it is the point.
        EntitiesWithSubtype = path.Entities.Count(e => !string.IsNullOrWhiteSpace(e.Subtype)),
        EntitiesWithDescription = path.Entities.Count(e => !string.IsNullOrWhiteSpace(e.Description)),
        EntitiesWithAliases = path.Entities.Count(e => e.Aliases.Count > 0),
        DistinctEntityTypes = path.Entities.Select(e => e.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.Ordinal)
            .ToArray(),
        MeanFactConfidence = path.Facts.Count == 0 ? 0 : path.Facts.Average(f => f.Confidence),
        DistinctFactConfidences = path.Facts.Select(f => f.Confidence).Distinct().Count(),
    };

    private static IReadOnlyList<IReadOnlyList<Message>> LoadSlices(
        string datasetPath, int units, int seed, int turns)
    {
        var options = LongMemEvalBenchmarkProtocol.CreateOptions(
            datasetPath, units, seed, judgeRetryAttempts: 0,
            LongMemEvalEvidenceDetail.Identifiers, maxRelevantMessages: 30);
        var index = LongMemEvalEvidenceIndex.Load(datasetPath, options);
        var slices = new List<IReadOnlyList<Message>>();
        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var question in index.Questions.Take(units))
        {
            var messages = new List<Message>();
            var turn = 0;
            foreach (var (user, assistant) in
                LongMemEvalBenchmarkProtocol.History(question).Take(turns))
            {
                // Deterministic ids and timestamps: this probe is a diff between two extractors over
                // IDENTICAL input, so anything varying per run would show up as a difference between
                // the paths when it is only a difference between invocations.
                messages.Add(new Message
                {
                    MessageId = $"{question.QuestionId}-u{turn:D4}",
                    ConversationId = question.QuestionId,
                    SessionId = question.QuestionId,
                    Role = "user",
                    Content = user,
                    TimestampUtc = stamp.AddMinutes(turn * 2),
                });
                messages.Add(new Message
                {
                    MessageId = $"{question.QuestionId}-a{turn:D4}",
                    ConversationId = question.QuestionId,
                    SessionId = question.QuestionId,
                    Role = "assistant",
                    Content = assistant,
                    TimestampUtc = stamp.AddMinutes((turn * 2) + 1),
                });
                turn++;
            }

            if (messages.Count > 0) slices.Add(messages);
        }

        return slices;
    }

    private static string? Value(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private sealed class PathResult(string name)
    {
        public string Name { get; } = name;
        public int Calls { get; private set; }
        public List<ExtractedEntity> Entities { get; } = [];
        public List<ExtractedFact> Facts { get; } = [];
        public List<ExtractedPreference> Preferences { get; } = [];
        public List<ExtractedRelationship> Relationships { get; } = [];

        public void Add(
            IReadOnlyList<ExtractedEntity> entities,
            IReadOnlyList<ExtractedFact> facts,
            IReadOnlyList<ExtractedPreference> preferences,
            IReadOnlyList<ExtractedRelationship> relationships,
            int calls)
        {
            Entities.AddRange(entities);
            Facts.AddRange(facts);
            Preferences.AddRange(preferences);
            Relationships.AddRange(relationships);
            Calls += calls;
        }
    }

    internal sealed record PathSummary
    {
        public required string Path { get; init; }
        public int Calls { get; init; }
        public int Entities { get; init; }
        public int Facts { get; init; }
        public int Preferences { get; init; }
        public int Relationships { get; init; }
        public int EntitiesWithSubtype { get; init; }
        public int EntitiesWithDescription { get; init; }
        public int EntitiesWithAliases { get; init; }
        public string[] DistinctEntityTypes { get; init; } = [];
        public double MeanFactConfidence { get; init; }
        public int DistinctFactConfidences { get; init; }
    }

    /// <summary>
    /// 30.1. One arm run twice over byte-identical input: how much an extractor agrees with itself.
    /// </summary>
    /// <remarks>
    /// This is the reference every cross-extractor Jaccard should have been read against, and the only
    /// instrument that can say whether a seed buys reproducibility on the deployment in use. Written to
    /// disk whatever the answer — "the seed did nothing here" is a measured property of the provider,
    /// and the pre-registered rule (seeded must beat unseeded by ≥3× or the seed is declared
    /// ineffective) needs both numbers on record to be decidable at all.
    /// </remarks>
    internal sealed record SelfAgreementReport
    {
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public string Dataset { get; init; } = string.Empty;
        public int Units { get; init; }
        public int Seed { get; init; }
        public int TurnsPerUnit { get; init; }

        /// <summary>Which extractor was repeated. Not decoration: the three are different code.</summary>
        public string Arm { get; init; } = string.Empty;

        /// <summary>The extraction sampling seed, or null for the unseeded control.</summary>
        public int? ExtractionSeed { get; init; }

        public required PathSummary Run1 { get; init; }
        public required PathSummary Run2 { get; init; }
        public double SelfJaccard { get; init; }
        public int SharedFactTriples { get; init; }
        public int UnionFactTriples { get; init; }

        /// <summary>
        /// Three cold builds of one configuration agreed at Jaccard 0.17 (7.5% common to all three).
        /// Carried here as context, NOT as the control: it is a different arm through a different
        /// pipeline. The same-build unseeded repeat is the control this number must be read against.
        /// </summary>
        public double UnseededColdBuildReference { get; init; }
    }

    internal sealed record ComparisonReport
    {
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public string Dataset { get; init; } = string.Empty;
        public int Units { get; init; }
        public int Seed { get; init; }
        public int TurnsPerUnit { get; init; }
        public required PathSummary PerKind { get; init; }
        public required PathSummary Unified { get; init; }
        public required PathSummary MultiSessionBatch { get; init; }
        public double BatchVsPerKindJaccard { get; init; }
        public double BatchVsUnifiedJaccard { get; init; }
        public int SharedFactTriples { get; init; }
        public int UnionFactTriples { get; init; }
        public double FactTripleJaccard { get; init; }
        public string[] OnlyInPerKind { get; init; } = [];
        public string[] OnlyInUnified { get; init; } = [];
    }
}
