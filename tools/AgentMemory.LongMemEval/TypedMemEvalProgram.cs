using System.Globalization;
using System.Text.Json;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.Abstractions.Services;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The <c>--typedmemeval</c> verb: runs one TypedMemEval vertical (or all five) against the
/// structured AgentMemory stack, the oracle ceiling, or the Prospective control arm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Citation rule (ADR-026):</b> results are cited as "TypedMemEval-&lt;Vertical&gt; (AgentEval)"
/// and are never summed or averaged with LongMemEval numbers. Nothing here hard-codes a corpus
/// hash or question count — identity comes from the result's own provenance, so the 0.23 corpus
/// revision changes file names and hashes without changing this code.
/// </para>
/// <para>
/// <c>--runs N</c> repeats the identical configuration sequentially and, past one run, prints the
/// <see cref="TypedMemEvalRunSet.Summarize"/> band — including <c>QuestionsWithFlips</c>, the
/// number that says whether a narrow band is evidence or coincidence. Banding requires a
/// <c>--random-seed</c>, because unseeded runs draw different questions and
/// <see cref="TypedMemEvalRunSet"/> rightly refuses to band different experiments.
/// </para>
/// <para>
/// The memory arm always runs the <b>structured</b> mode: it is the stack under measurement, and
/// the timestamped channel's point-in-time recall performs no semantic message search, so a raw or
/// hybrid arm under it could never fill a message budget honestly.
/// </para>
/// </remarks>
internal static class TypedMemEvalProgram
{
    /// <summary>Answer-context item budget, matching the main verb's default.</summary>
    private const int DefaultMaxRelevant = 30;

    /// <summary>
    /// Every option this verb accepts. An option listed here is a promise that the verb honours
    /// it — the guard tests hold each name against a property on the options record.
    /// </summary>
    internal static readonly string[] KnownOptions =
    [
        "--typedmemeval", "--max-questions", "--random-seed", "--answer-seed",
        "--runs", "--oracle", "--control",
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            using var answerChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var judgeChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());

            // The oracle arm reads projected gold directly: no memory store, no embeddings, no
            // container. Everything below the profile exists only for the memory arm.
            LongMemEvalMemoryProfile? profile = null;
            LongMemEvalChatCallMeter? extractionChatClient = null;
            string? extractionDeployment = null;
            try
            {
                if (!options.Oracle)
                {
                    var embeddingDeployment = RequiredEnvironment("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
                    extractionDeployment =
                        Environment.GetEnvironmentVariable("AZURE_OPENAI_EXTRACTION_DEPLOYMENT")
                        ?? deployment;
                    extractionChatClient = new LongMemEvalChatCallMeter(
                        new ProviderCompatibleExtractionChatClient(
                            azureClient.GetChatClient(extractionDeployment).AsIChatClient()));
                    var embeddingGenerator = azureClient
                        .GetEmbeddingClient(embeddingDeployment)
                        .AsIEmbeddingGenerator();
                    var embeddingDimensions = await LongMemEvalRuntime
                        .ProbeEmbeddingDimensionsAsync(embeddingGenerator)
                        .ConfigureAwait(false);
                    profile = await LongMemEvalMemoryProfile
                        .StartAsync(
                            embeddingGenerator,
                            extractionChatClient,
                            LongMemEvalMemoryMode.Structured,
                            extractionDeployment,
                            embeddingDimensions,
                            Console.Out,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                foreach (var vertical in options.Verticals)
                {
                    await RunVerticalAsync(
                            vertical, options, answerChatClient, judgeChatClient, deployment, profile)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (profile is not null)
                    await profile.DisposeAsync().ConfigureAwait(false);
                extractionChatClient?.Dispose();
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"typedmemeval: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunVerticalAsync(
        TypedMemEvalVertical vertical,
        TypedMemEvalRunOptions options,
        IChatClient answerChatClient,
        IChatClient judgeChatClient,
        string deployment,
        LongMemEvalMemoryProfile? profile)
    {
        var descriptor = TypedMemEvalVerticals.For(vertical);
        var results = new List<ExternalBenchmarkResult>(options.Runs);
        for (var runIndex = 1; runIndex <= options.Runs; runIndex++)
        {
            var facade = BuildFacade(options);
            var runner = new TypedMemEvalRunner(judgeChatClient);
            var startedUtc = DateTimeOffset.UtcNow;
            Console.WriteLine(
                $"typedmemeval: {descriptor.DisplayName} run {runIndex}/{options.Runs} — " +
                $"{(options.Oracle ? "oracle (GoldOnly)" : "structured memory arm")}" +
                $"{(options.Control ? ", control arm (TimestampsAndText)" : string.Empty)}, " +
                $"seed {options.RandomSeed?.ToString(CultureInfo.InvariantCulture) ?? "unseeded"}, " +
                $"max questions {options.MaxQuestions?.ToString(CultureInfo.InvariantCulture) ?? "all"}.");

            ExternalBenchmarkResult result;
            if (options.Oracle)
            {
                result = await runner
                    .RunOracleAsync(
                        answerChatClient, vertical, facade, LongMemEvalOracleOptions.GoldOnly)
                    .ConfigureAwait(false);
            }
            else
            {
                // Fresh adapter and fresh evidence index per run: Resolve consumes its entries, and
                // a distinct runId keeps every run's owners and sessions isolated in the shared
                // store. The container itself is reused — isolation is by scope, not by teardown.
                var runId =
                    $"typedmemeval-{descriptor.Slug}{(options.Control ? "-control" : string.Empty)}" +
                    $"-run{runIndex}-{startedUtc:yyyyMMddTHHmmssZ}";
                var adapter = new AgentMemoryLongMemEvalAdapter(
                    profile!.Services.GetRequiredService<IMemoryService>(),
                    answerChatClient,
                    runId,
                    new LongMemEvalAdapterOptions
                    {
                        MaxRelevantMessages = DefaultMaxRelevant,
                        MemoryMode = LongMemEvalMemoryMode.Structured,
                        MinSimilarityScore = 0,
                        ModelId = deployment,
                        EvidenceIndex = LongMemEvalEvidenceIndex.CreateTypedMemEval(vertical, facade),
                        EvidenceDetail = LongMemEvalEvidenceDetail.Identifiers,
                        RequireGraphReadBack = true,
                        GraphProbe = new Neo4jLongMemEvalGraphProbe(
                            profile.Services.GetRequiredService<global::Neo4j.Driver.IDriver>()),
                        ExtractionProgress = (completed, total) => Console.WriteLine(
                            $"typedmemeval: extraction units {completed}/{total}.")
                    });
                result = await runner.RunAsync(adapter, vertical, facade).ConfigureAwait(false);
            }

            var destination = Persist(result, descriptor, options, runIndex, startedUtc);
            PrintRun(result, destination);
            results.Add(result);
        }

        if (results.Count > 1)
            PrintBand(TypedMemEvalRunSet.Summarize(results));
    }

    private static TypedMemEvalOptions BuildFacade(TypedMemEvalRunOptions options) => new()
    {
        MaxQuestions = options.MaxQuestions,
        RandomSeed = options.RandomSeed,
        AnswerSeed = options.AnswerSeed,
        // Null takes the vertical's own required grounding — the probe arm. The control arm is the
        // family's published pairing: same corpus, same hash, dates re-added to the text, and the
        // run labelled so it can never be banded with the probe by accident.
        TemporalGrounding = options.Control ? TemporalGroundingMode.TimestampsAndText : null,
        ControlArm = options.Control
    };

    /// <summary>
    /// Writes the runner's own result JSON under <c>artifacts/evaluation/</c>, named by vertical,
    /// arm, seed, run index, and UTC stamp — everything needed to pair files into a band later.
    /// </summary>
    private static string Persist(
        ExternalBenchmarkResult result,
        TypedMemEvalVerticalDescriptor descriptor,
        TypedMemEvalRunOptions options,
        int runIndex,
        DateTimeOffset startedUtc)
    {
        var name =
            $"typedmemeval-{descriptor.Slug}" +
            (options.Control ? "-control" : string.Empty) +
            (options.Oracle ? "-oracle" : string.Empty) +
            $"-seed{options.RandomSeed?.ToString(CultureInfo.InvariantCulture) ?? "unseeded"}" +
            $"-run{runIndex}" +
            $"-{startedUtc:yyyyMMddTHHmmssZ}.json";
        var destination = Path.GetFullPath(Path.Combine("artifacts", "evaluation", name));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(
            destination,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);
        return destination;
    }

    private static void PrintRun(ExternalBenchmarkResult result, string destination)
    {
        if (result.TypedOutcomes is { } typed)
        {
            var counts = typed.Outcomes;
            Console.WriteLine(
                $"typedmemeval: N={counts.N} correct={counts.Correct} wrong={counts.Wrong} " +
                $"abstained={counts.Abstained} missed={counts.Missed} premature={counts.Premature} " +
                $"inconclusive={counts.Inconclusive} unrun={counts.Unrun}");
            Console.WriteLine(
                $"typedmemeval: attribution observed share {typed.Attribution.ObservedShare:P0} " +
                $"(present {typed.Attribution.EvidencePresent}, absent {typed.Attribution.EvidenceAbsent}, " +
                $"unobserved {typed.Attribution.Unobserved})");
            if (typed.Coverage.MeanOverGoldBearing is { } realised)
            {
                var floor = typed.Coverage.CalibratedFloorMean;
                Console.WriteLine(
                    $"typedmemeval: realised coverage (gold-bearing mean) {realised:F3}" +
                    (floor is { } f
                        ? $" vs calibrated BM25 floor {f:F3}"
                        : string.Empty));
            }
        }

        Console.WriteLine($"typedmemeval: report {destination}");
    }

    private static void PrintBand(TypedMemEvalRunSetSummary band)
    {
        Console.WriteLine(
            $"typedmemeval: band over {band.Runs} runs of {band.Vertical} " +
            $"(corpus {band.CorpusSha256})");
        foreach (var pair in band.Outcomes.OrderBy(pair => pair.Key))
        {
            Console.WriteLine(
                $"typedmemeval:   {pair.Key,-12} {pair.Value.Minimum}..{pair.Value.Maximum} " +
                $"(mean {pair.Value.Mean.ToString("F1", CultureInfo.InvariantCulture)}, " +
                $"width {pair.Value.Width})");
        }

        Console.WriteLine(
            $"typedmemeval:   questions compared {band.QuestionsCompared}; " +
            $"questions with flips {band.QuestionsWithFlips}" +
            (band.QuestionsWithFlips > 0
                ? $" [{string.Join(", ", band.FlippedQuestionIds)}]"
                : string.Empty));
        if (band.AtMinimumRunCount)
        {
            Console.WriteLine(
                "typedmemeval:   two runs is the banding floor (three recommended); a zero-width " +
                "band here can be coincidence, so read it as weak evidence.");
        }
    }

    internal static TypedMemEvalRunOptions Parse(string[] args)
    {
        LongMemEvalArgumentValidator.Validate(args, KnownOptions);

        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }

        var verticals = ParseVerticals(
            Value("--typedmemeval")
            ?? throw new ArgumentException(
                "--typedmemeval requires a vertical " +
                $"({VerticalSlugs()}) or 'all'."));
        var options = new TypedMemEvalRunOptions(
            verticals,
            ParsePositive(Value("--max-questions"), "--max-questions"),
            ParseAnyInteger(Value("--random-seed"), "--random-seed"),
            ParseAnyInteger(Value("--answer-seed"), "--answer-seed"),
            ParsePositive(Value("--runs"), "--runs") ?? 1,
            Array.IndexOf(args, "--oracle") >= 0,
            Array.IndexOf(args, "--control") >= 0);

        // Validated at parse time, before any container, client, or provider call exists: a run
        // set that cannot be banded, or a control arm with no pair to control, must stop here.
        if (options.Runs > 1 && options.RandomSeed is null)
        {
            throw new ArgumentException(
                "--runs above 1 requires --random-seed: unseeded runs draw different questions, and " +
                "TypedMemEvalRunSet.Summarize refuses to band different samples.");
        }
        if (options.Control &&
            (options.Verticals.Count != 1 ||
             options.Verticals[0] != TypedMemEvalVertical.Prospective))
        {
            throw new ArgumentException(
                "--control is the Prospective pair's control arm (dates re-added to the text); " +
                "combine it with --typedmemeval prospective.");
        }

        return options;
    }

    private static IReadOnlyList<TypedMemEvalVertical> ParseVerticals(string value)
    {
        if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
            return TypedMemEvalVerticals.All.Select(descriptor => descriptor.Vertical).ToArray();

        var descriptor = TypedMemEvalVerticals.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return descriptor is null
            ? throw new ArgumentException(
                $"Unknown TypedMemEval vertical '{value}'. " +
                $"--typedmemeval must name a vertical ({VerticalSlugs()}) or 'all'.")
            : [descriptor.Vertical];
    }

    private static string VerticalSlugs() =>
        string.Join("|", TypedMemEvalVerticals.All.Select(descriptor => descriptor.Slug));

    private static int? ParsePositive(string? value, string option)
    {
        if (value is null) return null;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            throw new ArgumentException($"{option} must be a positive integer.");
        }

        return parsed;
    }

    /// <summary>
    /// A sampling seed is not a count: zero and negative values are legal, and a value the parser
    /// cannot read must stop the run rather than fall back to "unseeded" while the operator
    /// believes otherwise.
    /// </summary>
    private static int? ParseAnyInteger(string? value, string option)
    {
        if (value is null) return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"{option} must be an integer.");
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is required; refusing to create a synthetic TypedMemEval score.");

    internal sealed record TypedMemEvalRunOptions(
        IReadOnlyList<TypedMemEvalVertical> Verticals,
        int? MaxQuestions,
        int? RandomSeed,
        int? AnswerSeed,
        int Runs,
        bool Oracle,
        bool Control);
}
