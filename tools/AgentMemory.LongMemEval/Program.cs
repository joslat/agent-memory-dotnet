using System.Globalization;
using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

return await LongMemEvalProgram.RunAsync(args);

internal static class LongMemEvalProgram
{
    private const int DefaultQuestions = 10;
    private const int DefaultSeed = 42;
    private const int DefaultMaxRelevant = 30;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            PrintHelp();
            return 0;
        }
        if (args.Contains("--reference-arm", StringComparer.Ordinal))
        {
            // G4-REF. Dispatched before everything else so no AgentMemory service, container, or
            // embedding client is ever constructed for an arm that by definition has no memory.
            return await LongMemEvalReferenceArmProgram.RunAsync(args)
                .ConfigureAwait(false);
        }
        if (args.Contains("--surface-probe", StringComparer.Ordinal))
        {
            // K2. Read-only, credential-free: reports whether the reasoning-trace and GraphRAG
            // surfaces have anything to return, and checks index health first because a FAILED index
            // is indistinguishable from an empty corpus from the outside.
            return await LongMemEvalSurfaceProbeProgram.RunAsync(args).ConfigureAwait(false);
        }
        if (args.Contains("--predicate-distribution", StringComparer.Ordinal))
        {
            // J1.2. Read-only, and dispatched before any Azure environment is required: counting
            // relation names in an existing volume must not need the credentials of a paid run.
            return await LongMemEvalPredicateDistributionProgram.RunAsync(args)
                .ConfigureAwait(false);
        }
        // Diffs what the per-kind and unified extractors extract from identical messages. Answers the
        // "extraction-quality acceptance" question UseUnifiedExtraction's own doc names, which accuracy
        // cannot: it needs no judge, no answer model, no database and no cold build, and it reports
        // field completeness that no accuracy number would ever surface.
        if (args.Contains("--extraction-compare", StringComparer.Ordinal))
        {
            return await LongMemEvalExtractionCompareProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--list-prepared-corpora", StringComparer.Ordinal))
        {
            // Read-only and credential-free: answering "which frozen corpus should I reuse?" must not
            // need the credentials of a paid run, or it stops being asked.
            Console.WriteLine(LongMemEvalPreparedCorpusRegistry.Describe(
                LongMemEvalPreparedCorpusRegistry.Read(), DateTimeOffset.UtcNow));
            return 0;
        }

        if (args.Contains("--capture-headroom", StringComparer.Ordinal))
        {
            // 8.3c. Read-only, credential-free, and dispatched before any Azure environment is required:
            // this verb exists to decide whether a ~96M-input-token run could show anything, and a check
            // that needs the credentials of a paid run is a check nobody makes before buying.
            return LongMemEvalCaptureHeadroomProgram.Run(args);
        }

        if (args.Contains("--oracle-representation", StringComparer.Ordinal))
        {
            // P2. Extracts from the gold sessions only and answers from the structured rendering, so
            // recall stays at 100% and the only variable is the representation.
            return await LongMemEvalRepresentationProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--oracle-precision", StringComparer.Ordinal))
        {
            // P1. Adds distractor sessions to a context that already holds all the gold, so recall is
            // pinned at 100% and the only variable is how much wrong material sits beside the answer.
            return await LongMemEvalContextPrecisionProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--oracle-decomposition", StringComparer.Ordinal))
        {
            // B4. Needs answer + judge credentials but NO Neo4j, Docker or prepared corpus: the oracle
            // reads gold sessions from the dataset, so the question "does decomposing help?" is
            // answerable without paying for a build.
            return await LongMemEvalOracleDecompositionProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--typed-report", StringComparer.Ordinal))
        {
            // 25.7. Purely retrospective: reads reports already on disk, no provider call, no Neo4j.
            // Wires up a per-type reporting stack that was complete, tested and called by nothing.
            return await LongMemEvalTypedReportProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--probe-answer-determinism", StringComparer.Ordinal))
        {
            // 27.2. Answer calls only, no judge and no infrastructure. Asks whether the answer model
            // -- which the adapter currently invokes with NO ChatOptions, and which disagrees with
            // itself on 13 of 14 flipping questions under byte-identical retrieval -- can be pinned by
            // configuration on this deployment.
            return await LongMemEvalAnswerDeterminismProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--upstream-oracle", StringComparer.Ordinal))
        {
            // 28.2. AgentEval's oracle, now public. Runs before ours so the two can be compared on the
            // same level -- retirement of the hand-rolled one has to be earned, not assumed.
            return await LongMemEvalUpstreamOracleProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--time-grounded-oracle", StringComparer.Ordinal))
        {
            // 26.3. Prospective memory, measurable for the first time: AgentEval 0.21.0-beta ships a
            // time-grounded corpus. Oracle first -- gold context only, no Neo4j and no extraction --
            // because a question the model fails WITH the evidence cannot be fixed by any memory work.
            return await LongMemEvalTimeGroundedOracleProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--procedure-retrieval", StringComparer.Ordinal))
        {
            // 26.2. Procedural RETRIEVAL precision: does recall return the RIGHT procedure, and does it
            // stay quiet when none applies? Embedding calls only -- no chat model, no judge.
            return await ProcedureRetrievalProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--procedural-benefit", StringComparer.Ordinal))
        {
            // 7.6. The arms differ in exactly two things -- trace recall and promotion -- so that any
            // measured gap is attributable to memory rather than to a differently-equipped agent.
            return await ProceduralBenefitProgram.RunAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--prepared-pair", StringComparer.Ordinal))
        {
            return await LongMemEvalPreparedPairProgram.RunAsync(args)
                .ConfigureAwait(false);
        }


        try
        {
            var options = Parse(args);
            ValidateInputs(options);
            if (options.EvidenceDetail == LongMemEvalEvidenceDetail.Content)
            {
                Console.Error.WriteLine(
                    "longmemeval: warning: content evidence retains public dataset questions, recalled text, and model answers; keep the output gitignored.");
            }

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var embeddingDeployment =
                RequiredEnvironment("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
            var extractionDeployment =
                Environment.GetEnvironmentVariable("AZURE_OPENAI_EXTRACTION_DEPLOYMENT")
                ?? deployment;
            var azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new AzureKeyCredential(apiKey));
            using var answerChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var judgeChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var diagnosticChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var extractionChatClient = options.MemoryMode.UsesExtraction()
                ? new LongMemEvalChatCallMeter(new ProviderCompatibleExtractionChatClient(
                    azureClient.GetChatClient(extractionDeployment).AsIChatClient()))
                : null;
            var embeddingGenerator = azureClient
                .GetEmbeddingClient(embeddingDeployment)
                .AsIEmbeddingGenerator();
            var embeddingDimensions = await LongMemEvalRuntime
                .ProbeEmbeddingDimensionsAsync(embeddingGenerator)
                .ConfigureAwait(false);

            var benchmarkOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
                options.DatasetPath,
                options.Questions,
                options.Seed,
                options.JudgeRetryAttempts,
                options.EvidenceDetail,
                options.MaxRelevantMessages,
                includeQuestionTypes: LongMemEvalMemoryTypeSelection.TaskTypesFor(options.MemoryTypes));
            var evidenceIndex = LongMemEvalEvidenceIndex.Load(
                options.DatasetPath, benchmarkOptions);

            var runId = $"longmemeval-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            await using var profile = await LongMemEvalMemoryProfile
                .StartAsync(
                    embeddingGenerator,
                    extractionChatClient,
                    options.MemoryMode,
                    extractionDeployment,
                    embeddingDimensions,
                    Console.Out,
                    CancellationToken.None,
                    extractionSeed: options.ExtractionSeed)
                .ConfigureAwait(false);
            var adapter = new AgentMemoryLongMemEvalAdapter(
                profile.Services.GetRequiredService<IMemoryService>(),
                answerChatClient,
                runId,
                new LongMemEvalAdapterOptions
                {
                    MaxRelevantMessages = options.MaxRelevantMessages,
                    MemoryMode = options.MemoryMode,
                    AnswerSeed = options.AnswerSeed,
                    MinSimilarityScore = 0,
                    ModelId = deployment,
                    ExcludeSyntheticFormatterMessages = options.ExcludeSyntheticMessages,
                    MaxItemsPerSourceSession = options.MaxItemsPerSourceSession,
                    ChronologicalAnswerContext = options.ChronologicalAnswerContext,
                    EvidenceIndex = evidenceIndex,
                    EvidenceDetail = options.EvidenceDetail,
                    RequireGraphReadBack = options.MemoryMode.UsesExtraction(),
                    GraphProbe = options.MemoryMode.UsesExtraction()
                        ? new Neo4jLongMemEvalGraphProbe(
                            profile.Services.GetRequiredService<Neo4j.Driver.IDriver>())
                        : null,
                    ExtractionProgress = (completed, total) => Console.WriteLine(
                        $"longmemeval: extraction units {completed}/{total}.")
                });

            var runner = LongMemEvalBenchmarkRunner.Create(
                judgeChatClient, options.DatasetPath);
            var benchmarkConfig = new AgentBenchmarkConfig
            {
                AgentName = adapter.Name,
                ModelId = deployment,
                ReducerStrategy = $"AgentMemory {options.MemoryMode.ToString().ToLowerInvariant()} recall",
                MemoryProvider = "AgentMemory .NET / Neo4j 5.26"
            };

            Console.WriteLine(
                $"longmemeval: running {options.Questions} stratified questions, seed {options.Seed}, mode {options.MemoryMode.ToString().ToLowerInvariant()}, context cap {options.MaxRelevantMessages}.");
            // P5. The prepared-pair path is not the only one whose vector searches were unobserved;
            // this verb never had a listener either. It needs no sealed manifest and no reusable base,
            // which is the whole reason it can run at all right now.
            using var vectorYield = new LongMemEvalVectorYieldListener();
            var result = await runner
                .RunAsync(adapter, benchmarkConfig, benchmarkOptions)
                .ConfigureAwait(false);
            var postRunDiagnostics = await LongMemEvalPostRunDiagnostics.RunAsync(
                diagnosticChatClient,
                evidenceIndex,
                result.QuestionResults,
                adapter.QuestionTelemetry,
                options.OracleMode,
                options.JudgeRetryAttempts,
                retainContent: options.EvidenceDetail == LongMemEvalEvidenceDetail.Content)
                .ConfigureAwait(false);
            var answerCalls = answerChatClient.Snapshot();
            var judgeCalls = judgeChatClient.Snapshot();
            var diagnosticCalls = diagnosticChatClient.Snapshot();
            var extractionCalls = extractionChatClient?.Snapshot() ?? LongMemEvalChatCallSnapshot.Zero;
            var initialExtractionCalls =
                adapter.QuestionTelemetry.Sum(item => item.ExtractionUnits) * 4L;


            var validation = LongMemEvalRunValidator.Validate(
                options.Questions,
                result.TotalLlmCalls,
                adapter.QuestionTelemetry,
                result.QuestionResults,
                answerCalls,
                judgeCalls,
                extractionCalls,
                initialExtractionCalls,
                postRunDiagnostics.JudgeRetries.Count,
                options.JudgeRetryAttempts);
            var destination = ResolveOutput(options.OutputPath, runId);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var report = new
            {
                schemaVersion = 2,
                runId,
                generatedAtUtc = DateTimeOffset.UtcNow,
                accepted = validation.Accepted,
                validationIssues = validation.Issues,
                fingerprint = new
                {
                    dataset = Path.GetFileName(options.DatasetPath),
                    datasetSha256 = Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(
                            await File.ReadAllBytesAsync(options.DatasetPath).ConfigureAwait(false))),
                    questions = options.Questions,
                    seed = options.Seed,
                    stratified = true,
                    // A typed run answers a different question from an all-types run on the same seed
                    // and count, so it must never read as comparable to one. The mapping revision
                    // travels with it because the selection is an opinion that will be revised, and a
                    // per-type figure that cannot name the taxonomy it came from is unauditable.
                    memoryTypes = options.MemoryTypes.Count == 0
                        ? "all"
                        : string.Join(",", options.MemoryTypes.OrderBy(t => t, StringComparer.Ordinal)),
                    memoryTypeMapRevision = options.MemoryTypes.Count == 0
                        ? null
                        : LongMemEvalMemoryTypeMap.Default.Revision,
                    answerModel = deployment,
                    judgeModel = deployment,
                    maxRelevantMessages = options.MaxRelevantMessages,
                    operatingMode = options.MemoryMode.Fingerprint(),
                    // 27.2. A seeded run and an unseeded one have different answer-variance, so they
                    // must never be compared by accident. "unpinned-temperature-1" is the honest name
                    // for the default: this deployment refuses every temperature but its own.
                    answerSampling = options.AnswerSeed is { } seed
                        ? $"seeded-{seed}-temperature-1"
                        : "unpinned-temperature-1",
                    // G3B.1 changes which items fill the budget, so a filtered run must never be
                    // comparable to the control by accident.
                    syntheticFormatterExclusion = options.ExcludeSyntheticMessages
                        ? "excluded-candidate-x3"
                        : "control-unfiltered",
                    // G3B.3 reallocates the budget across sessions, so a capped run must never be
                    // comparable to an uncapped one by accident.
                    answerContextOrder = options.ChronologicalAnswerContext
                        ? "chronological"
                        : "retrieval-rank",
                    sessionBudgetCap = options.MaxItemsPerSourceSession == 0
                        ? "uncapped"
                        : $"max-{options.MaxItemsPerSourceSession}-items-per-source-session",
                    extractionModel = options.MemoryMode.UsesExtraction() ? extractionDeployment : null,
                    extractionTemperatureCompatibility = options.MemoryMode.UsesExtraction()
                        ? "explicit-zero-to-provider-default" : null,
                    extractionResponseFormat = options.MemoryMode.UsesExtraction()
                        ? "json-object" : null,
                    extractionSourceTime = options.MemoryMode.UsesExtraction()
                        ? "metadata-only-not-in-extraction-prompt" : null,
                    evidenceDetail = options.EvidenceDetail.ToString().ToLowerInvariant(),
                    oracleMode = options.OracleMode.ToString().ToLowerInvariant(),
                    judgeRetryAttempts = options.JudgeRetryAttempts,
                    embedding = new
                    {
                        provider = "Azure OpenAI",
                        deployment = embeddingDeployment,
                        dimensions = embeddingDimensions
                    },
                    judgeRequest = "AgentEval-source-native-null-temperature-256-tokens",
                    neo4jImage = "neo4j:5.26",
                    agentEval = typeof(ExternalBenchmarkOptions).Assembly.GetName().Version?.ToString(),
                    agentEvalDependency = "source-project:AgentEval.Memory"
                },
                agentMemory = new
                {
                    vectorYield = LongMemEvalVectorYieldSummary.From(vectorYield.Samples),
                    questions = adapter.QuestionTelemetry,
                    totalMessagesStored = adapter.QuestionTelemetry.Sum(item => item.MessagesStored),
                    totalItemsRetrieved = adapter.QuestionTelemetry.Sum(item => item.ItemsRetrieved),
                    totalExtractionUnits = adapter.QuestionTelemetry.Sum(item => item.ExtractionUnits),
                    totalRawMessagesRetrieved = adapter.QuestionTelemetry.Sum(item => item.RawMessagesRetrieved),
                    totalEntitiesRetrieved = adapter.QuestionTelemetry.Sum(item => item.EntitiesRetrieved),
                    totalFactsRetrieved = adapter.QuestionTelemetry.Sum(item => item.FactsRetrieved),
                    totalPreferencesRetrieved = adapter.QuestionTelemetry.Sum(item => item.PreferencesRetrieved),
                    graphRagQuestions = adapter.QuestionTelemetry.Count(item => item.GraphRagIncluded),
                    zeroStoreQuestions = adapter.QuestionTelemetry.Count(item => item.MessagesStored == 0),
                    zeroRecallQuestions = adapter.QuestionTelemetry.Count(item => item.ItemsRetrieved == 0),
                    // PLAN 4.2. Does the sufficiency signal ORDER answerable questions above
                    // unanswerable ones? Emitted on every run because it costs nothing -- no extra
                    // call, no rebuild, it reads two fields already recorded -- and because a number
                    // that appears only when someone remembers to ask for it never gets asked for.
                    // Null auc means one class was empty and the signal was never put to the test.
                    sufficiencyAuc = LongMemEvalSufficiencyReport.From(adapter.QuestionTelemetry),
                },
                callAccounting = new
                {
                    benchmarkLlmCalls = result.TotalLlmCalls,
                    diagnosticLlmCalls = postRunDiagnostics.DiagnosticLlmCalls,
                    totalLlmCalls = result.TotalLlmCalls + postRunDiagnostics.DiagnosticLlmCalls,
                    diagnosticCallsAffectScore = false,
                    observed = new
                    {
                        answer = Project(answerCalls),
                        judge = Project(judgeCalls),
                        extraction = Project(extractionCalls),
                        diagnostics = Project(diagnosticCalls)
                    },
                    extractionInitialExpectedCalls = initialExtractionCalls,
                    extractionRetryCalls = Math.Max(
                        0, extractionCalls.Calls - initialExtractionCalls)
                },
                postRunDiagnostics,
                result = validation.Accepted
                    ? LongMemEvalReportProjection.CreateAcceptedResult(
                        result, options.EvidenceDetail)
                    : null,
                diagnostic = validation.Accepted ? null : new
                {
                    result.BenchmarkId,
                    result.BenchmarkName,
                    result.Duration,
                    result.TotalLlmCalls,
                    questions = result.QuestionResults.Select((question, index) => new
                    {
                        question.QuestionId,
                        question.QuestionType,
                        question = options.EvidenceDetail == LongMemEvalEvidenceDetail.Content
                            ? question.Question
                            : null,
                        goldAnswer = options.EvidenceDetail == LongMemEvalEvidenceDetail.Content
                            ? question.GoldAnswer
                            : null,
                        agentResponse = options.EvidenceDetail == LongMemEvalEvidenceDetail.Content
                            ? question.AgentResponse
                            : null,
                        question.Correct,
                        question.RawScore,
                        judgeExplanation = options.EvidenceDetail == LongMemEvalEvidenceDetail.Content
                            ? question.JudgeExplanation
                            : null,
                        status = LongMemEvalRunValidator.Classify(
                            question,
                            adapter.QuestionTelemetry.FirstOrDefault(item =>
                                item.QuestionNumber == index + 1)),
                        question.Duration
                    })
                }
            };
            await File.WriteAllTextAsync(
                destination,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) +
                Environment.NewLine).ConfigureAwait(false);

            if (!validation.Accepted)
            {
                foreach (var issue in validation.Issues)
                    Console.Error.WriteLine($"longmemeval: validation: {issue}");
                Console.Error.WriteLine($"longmemeval: rejected diagnostic report {destination}");
                return 1;
            }

            Console.WriteLine(
                $"longmemeval: accuracy={result.OverallAccuracy:F1}% task_average={result.TaskAveragedAccuracy:F1}% questions={result.QuestionResults.Count} llm_calls={result.TotalLlmCalls}");
            Console.WriteLine($"longmemeval: report {destination}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Every option the plain verb accepts, including the sub-verb switches dispatched in RunAsync.
    /// </summary>
    /// <remarks>
    /// The verb switches are listed because dispatch happens before this parser runs, so an
    /// unrecognised one would otherwise fall through to the default verb and silently measure
    /// something else - the same failure mode `--mode` produced for `--memory-mode`.
    /// </remarks>
    private static readonly string[] KnownOptions =
    [
        "--reference-arm", "--surface-probe", "--predicate-distribution", "--prepared-pair",
        "--procedural-benefit", "--attempts",
        "--oracle-decomposition", "--max-sub-questions", "--question-ids", "--no-content",
        "--oracle-precision", "--distractor-sessions", "--gold-fraction", "--oracle-representation",
        "--capture-headroom", "--artifacts",
        "--probe-answer-determinism", "--repeats", "--probe-questions", "--include-text",
        "--answer-seed", "--typed-report", "--reports", "--arm",
        "--procedure-retrieval", "--min-scores", "--task", "--query-formulation", "--time-grounded-oracle", "--upstream-oracle",
        "--list-prepared-corpora",
        "--extraction-compare", "--help",
        "--chronological-context", "--dataset", "--evidence-detail",
        "--exclude-synthetic-messages", "--judge-retries", "--max-items-per-session",
        "--max-relevant", "--memory-mode", "--oracle", "--output", "--questions", "--seed",
        "--units", "--turns", "--repeat", "--extraction-seed", "--memory-types",
        // 30.6 sub-step 0. Listed here even though --extraction-compare dispatches before validation
        // runs: an option known to the parser but read by nobody is the exact defect 30.1 found in
        // --extraction-seed, and the mirror-image defect (read but unlisted) becomes real the moment
        // dispatch order changes. ExtractionCompareCommandLineTests holds both directions.
        "--vocabulary-ab", "--use-predicate-vocabulary",
    ];

    private static Options Parse(string[] args)
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

        return new Options(
            // Explicit --dataset wins; LONGMEMEVAL_DATASET is the standing setting; the known
            // checkout locations are the last resort. The path used to live only in shell history,
            // which is how it went missing.
            LongMemEvalDatasetLocator.Resolve(
                Value("--dataset"), Environment.GetEnvironmentVariable) ?? string.Empty,
            ParsePositive(Value("--questions"), DefaultQuestions, "--questions"),
            ParsePositive(Value("--seed"), DefaultSeed, "--seed"),
            ParsePositive(Value("--max-relevant"), DefaultMaxRelevant, "--max-relevant"),
            ParseEvidenceDetail(Value("--evidence-detail")),
            ParseOracleMode(Value("--oracle")),
            ParseMemoryMode(Value("--memory-mode")),
            ParseNonNegative(Value("--judge-retries"), 2, "--judge-retries"),
            Value("--output"),
            Array.IndexOf(args, "--exclude-synthetic-messages") >= 0,
            ParseNonNegative(Value("--max-items-per-session"), 0, "--max-items-per-session"),
            Array.IndexOf(args, "--chronological-context") >= 0,
            ParseMemoryTypes(Value("--memory-types")),
            // 27.2. Null unless asked for. Measured on this deployment to cut distinct answers from
            // 19-in-24 to 8-in-24; defaulting it on would make new runs incomparable with every
            // sealed measurement in the archive, which were all taken without it.
            Value("--answer-seed") is { } answerSeed
                ? ParseNonNegative(answerSeed, 0, "--answer-seed")
                : null,
            // 30.1. This verb accepted --extraction-seed in KnownOptions and then dropped it: the
            // argument validator let it through and nothing read it, so a run that asked to be seeded
            // silently was not. The seed's own doc says its effect must be MEASURED per deployment,
            // which requires being able to set it here at all.
            Value("--extraction-seed") is { } extractionSeed
                ? ParseSeedValue(extractionSeed, "--extraction-seed")
                : null);
    }

    /// <summary>Parses a sampling seed, which may legitimately be negative or zero.</summary>
    private static int ParseSeedValue(string value, string option) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"{option} must be an integer.");

    private static object Project(LongMemEvalChatCallSnapshot snapshot) => new
    {
        snapshot.Calls,
        snapshot.Failures,
        durationMs = snapshot.Duration.TotalMilliseconds
    };


    private static int ParsePositive(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
        return parsed;
    }

    private static LongMemEvalEvidenceDetail ParseEvidenceDetail(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "identifiers" => LongMemEvalEvidenceDetail.Identifiers,
            "none" => LongMemEvalEvidenceDetail.None,
            "content" => LongMemEvalEvidenceDetail.Content,
            _ => throw new ArgumentException(
                "--evidence-detail must be one of: none, identifiers, content.")
        };

    private static LongMemEvalOracleMode ParseOracleMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "none" => LongMemEvalOracleMode.None,
            "failed" => LongMemEvalOracleMode.Failed,
            "all" => LongMemEvalOracleMode.All,
            _ => throw new ArgumentException("--oracle must be one of: none, failed, all.")
        };

    private static LongMemEvalMemoryMode ParseMemoryMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "raw" => LongMemEvalMemoryMode.Raw,
            "structured" => LongMemEvalMemoryMode.Structured,
            "hybrid" => LongMemEvalMemoryMode.Hybrid,
            _ => throw new ArgumentException(
                "--memory-mode must be one of: raw, structured, hybrid.")
        };

    /// <summary>
    /// Parses <c>--memory-types episodic,temporal</c> into the requested memory types.
    /// </summary>
    /// <remarks>
    /// Empty means "every type", which is the sampling this harness has always done and the value
    /// every sealed base was recorded under. The selection is turned into task labels by
    /// <see cref="LongMemEvalMemoryTypeSelection"/>, from the same embedded mapping the per-type
    /// reports use -- a second hardcoded list here would drift, and the run would then sample one set
    /// of labels while reporting per-type figures computed from another.
    /// </remarks>
    private static IReadOnlyList<string> ParseMemoryTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var types = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (types.Length == 0)
            throw new ArgumentException("--memory-types requires at least one memory type.");
        // Validated here, before any container starts or any provider call is made: an unreachable
        // type must stop the run rather than silently widen it back to the full sample.
        LongMemEvalMemoryTypeSelection.TaskTypesFor(types);
        return types;
    }

    private static int ParseNonNegative(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ArgumentException($"{option} must be a non-negative integer.");
        return parsed;
    }

    private static void ValidateInputs(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.DatasetPath))
            throw new ArgumentException(
                "--dataset <longmemeval_s_cleaned.json> is required, or set "
                + $"{LongMemEvalDatasetLocator.PathVariable}.");
        if (!File.Exists(options.DatasetPath))
            throw new FileNotFoundException("LongMemEval dataset not found.", options.DatasetPath);
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is required; refusing to create a synthetic LongMemEval score.");

    private static string ResolveOutput(string? requested, string runId) =>
        Path.GetFullPath(requested ??
            Path.Combine("artifacts", "evaluation", runId, "report.json"));

    private static void PrintHelp() => Console.WriteLine(
        """
        AgentMemory LongMemEval (AgentEval.Memory local source)

        dotnet run --project tools/AgentMemory.LongMemEval -- \
          --dataset <longmemeval_s_cleaned.json> [--questions 10] [--seed 42] \
          [--max-relevant 30] [--memory-mode raw|structured|hybrid] \
          [--reference-arm no-memory|full-history] \
          [--prepared-pair] [--preflight-only] \
          [--preparation-workers 10] [--max-sessions-per-batch 4] \
          [--max-input-tokens 100000] \
          [--max-concurrent-batches-per-extraction 4] \
          [--max-concurrent-extraction-batches 12] \
          [--checkpoint-questions 3] [--checkpoint-timeout-seconds 3600] \
          [--diagnostic-question N --diagnostic-source-session N] \
          [--provider-no-progress-timeout-seconds 600] \
          [--evidence-detail none|identifiers|content] \
          [--exclude-synthetic-messages] [--max-items-per-session N] [--chronological-context] \
          [--memory-types episodic,temporal]           [--oracle none|failed|all] [--judge-retries 2] [--output <report.json>]

        --memory-types samples ONLY questions exercising the named memory types, so a per-type claim
        gets a per-type denominator. A 50-question stratified sample yields ~6 single-session-assistant
        questions; on 6, one item is 16.7 points, while two runs of an IDENTICAL config have measured
        25 points apart on 50 -- so a slice that small can only publish noise. Default: every type,
        which is the sampling every sealed base was recorded under. Types: semantic, episodic,
        temporal. metamemory arrives via abstention questions rather than a task label, and
        LongMemEval-S contains no procedural questions at any sample size.

        --exclude-synthetic-messages over-fetches 3x the message budget, drops only AgentEval's
        formatter boilerplate (session boundaries and padding), keeps retrieval order, and selects
        the first --max-relevant real source turns. Default off: unfiltered recall is the control.

        --reference-arm runs a control that uses no AgentMemory at all, on the identical sample, seed,
        answer deployment and judge, so an AgentMemory score has something to be measured against:
          no-memory     the question alone - the model's parametric floor.
          full-history  every real source turn in context, formatter boilerplate dropped - the ceiling.
        It starts no container and makes no embedding, extraction, storage or recall call. Whether the
        history fits is decided by the provider rejecting the prompt, never by a token estimate: in this
        dataset every question is 113k-128k estimated tokens, inside any estimator's own error bar.
        A question that does not fit is reported as skipped and excluded from fitted accuracy, never
        scored as wrong. Cannot be combined with --memory-mode, --prepared-pair,
        --exclude-synthetic-messages, or a non-none --oracle.

        --prepared-pair prepares structured memory once, freezes it, clones it, and evaluates isolated Structured and Hybrid arms.
        Supplying both diagnostic selectors with --prepared-pair runs exactly one extraction unit and can never emit a report or execute recall/judging.
        --preflight-only freezes the exact prepared-pair batch plan, proves zero provider calls/writes,
        prints source-session/call/token totals, cleans up, and emits no accepted report.
        --checkpoint-questions selects the highest-token frozen questions, executes the identical
        preparation path under a hard deadline, projects full cold-build time, cleans up, and emits no report.


        Requires AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT,
        and AZURE_OPENAI_EMBEDDING_DEPLOYMENT.
        Uses real LongMemEval data, a pinned Neo4j 5.26 container, real Azure OpenAI embeddings,
        and the same Azure deployment for answers and AgentEval's type-specific judge.
        Structured/hybrid extraction may use AZURE_OPENAI_EXTRACTION_DEPLOYMENT; it defaults to the answer deployment.
        """);

    private sealed record Options(
        string DatasetPath,
        int Questions,
        int Seed,
        int MaxRelevantMessages,
        LongMemEvalEvidenceDetail EvidenceDetail,
        LongMemEvalOracleMode OracleMode,
        LongMemEvalMemoryMode MemoryMode,
        int JudgeRetryAttempts,
        string? OutputPath,
        bool ExcludeSyntheticMessages,
        int MaxItemsPerSourceSession,
        bool ChronologicalAnswerContext,
        IReadOnlyList<string> MemoryTypes,
        int? AnswerSeed,
        int? ExtractionSeed);
}
