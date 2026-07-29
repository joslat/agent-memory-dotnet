using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentMemory.Abstractions.Services;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.LongMemEval;

internal static class LongMemEvalPreparedPairProgram
{
    private const int DefaultQuestions = 10;
    private const int DefaultSeed = 42;
    private const int DefaultMaxRelevant = 30;

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            Validate(options);
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
                options.MaxRelevantMessages);
            var datasetSha256 = Convert.ToHexStringLower(
                SHA256.HashData(
                    await File.ReadAllBytesAsync(options.DatasetPath).ConfigureAwait(false)));
            var agentEvalRevision = AgentEvalRevision();
            var expectation = LongMemEvalPreparationFingerprint.Expect(
                datasetSha256,
                agentEvalRevision,
                deployment,
                deployment,
                extractionDeployment,
                embeddingDeployment,
                embeddingDimensions,
                options.MaxRelevantMessages);
            var preparationId =
                $"longmemeval-prepared-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            var overall = Stopwatch.StartNew();

            await using var volumes = await LongMemEvalPreparedVolumes
                .CreateAsync(preparationId, CancellationToken.None)
                .ConfigureAwait(false);
            using var extractionCalls = new LongMemEvalChatCallMeter(
                new ProviderCompatibleExtractionChatClient(
                    azureClient.GetChatClient(extractionDeployment).AsIChatClient()));
            LongMemEvalPreparationManifest manifest;
            IReadOnlyList<LongMemEvalQuestionTelemetry> preparationTelemetry;
            var profileStartup = Stopwatch.StartNew();
            var baseStopMilliseconds = 0d;
            var manifestSealMilliseconds = 0d;
            var baseVolumeName = volumes.BeginBasePreparation();
            LongMemEvalMemoryProfile? baseProfile = null;
            try
            {
                baseProfile = await LongMemEvalMemoryProfile.StartAsync(
                        embeddingGenerator,
                        extractionCalls,
                        LongMemEvalMemoryMode.Structured,
                        extractionDeployment,
                        embeddingDimensions,
                        Console.Out,
                        CancellationToken.None,
                        baseVolumeName)
                    .ConfigureAwait(false);
                profileStartup.Stop();

                var evidenceIndex = LongMemEvalEvidenceIndex.Load(
                    options.DatasetPath,
                    benchmarkOptions);
                var questions = evidenceIndex.Questions.ToArray();
                if (questions.Length != options.Questions)
                {
                    throw new InvalidOperationException(
                        $"Prepared LongMemEval selected {questions.Length} questions; expected {options.Questions}.");
                }

                var driver = baseProfile.Services.GetRequiredService<IDriver>();
                var adapter = new AgentMemoryLongMemEvalAdapter(
                    baseProfile.Services.GetRequiredService<IMemoryService>(),
                    extractionCalls,
                    preparationId,
                    new LongMemEvalAdapterOptions
                    {
                        MemoryMode = LongMemEvalMemoryMode.Structured,
                        MaxRelevantMessages = options.MaxRelevantMessages,
                        MinSimilarityScore = 0,
                        ModelId = deployment,
                        EvidenceIndex = evidenceIndex,
                        EvidenceDetail = options.EvidenceDetail,
                        RequireGraphReadBack = true,
                        GraphProbe = new Neo4jLongMemEvalGraphProbe(driver),
                        PreparationOnly = true,
                        ExtractionProgress = (completed, total) => Console.WriteLine(
                            $"longmemeval: preparation extraction units {completed}/{total}.")
                    });

                for (var index = 0; index < questions.Length; index++)
                {
                    var question = questions[index];
                    await adapter.ResetSessionAsync().ConfigureAwait(false);
                    adapter.InjectConversationHistory(
                        LongMemEvalBenchmarkProtocol.History(question));
                    _ = await adapter.InvokeAsync(question.InvocationPrompt)
                        .ConfigureAwait(false);
                    Console.WriteLine(
                        $"longmemeval: prepared question {index + 1}/{questions.Length}.");
                }

                preparationTelemetry = adapter.QuestionTelemetry;
                ValidatePreparationTelemetry(preparationTelemetry, questions.Length);
                var initialExtractionCalls =
                    preparationTelemetry.Sum(item => item.ExtractionUnits) * 4L;
                var extractionSnapshot = extractionCalls.Snapshot();
                if (extractionSnapshot.Calls != initialExtractionCalls ||
                    extractionSnapshot.Failures != 0)
                {
                    throw new InvalidOperationException(
                        $"Prepared LongMemEval extraction accounting mismatch: observed {extractionSnapshot.Calls} calls and {extractionSnapshot.Failures} failures; expected exactly {initialExtractionCalls} calls and zero failures.");
                }

                var preparedQuestions = questions.Select((question, index) =>
                {
                    var telemetry = preparationTelemetry[index];
                    var history = LongMemEvalBenchmarkProtocol.History(question);
                    var sourceSessions = question.Messages
                        .Where(message =>
                            !message.IsSyntheticBoundary &&
                            !message.IsSyntheticFormatterPadding)
                        .Select(message => message.SourceSessionOrdinal)
                        .Distinct()
                        .Count();
                    if (telemetry.ExtractionUnits != sourceSessions)
                    {
                        throw new InvalidOperationException(
                            $"Prepared LongMemEval source-session count mismatch at question {index + 1}.");
                    }

                    return new LongMemEvalPreparedQuestion(
                        index + 1,
                        question.QuestionId,
                        LongMemEvalEvidenceIndex.Fingerprint(history),
                        LongMemEvalPreparationManifest.Hash(
                            $"{preparationId}-session-{index + 1:D4}|{preparationId}-owner-{index + 1:D4}"),
                        telemetry.MessagesStored,
                        sourceSessions,
                        telemetry.ExtractionUnits,
                        telemetry.GraphReadBack
                        ?? throw new InvalidOperationException(
                            $"Prepared LongMemEval question {index + 1} has no graph snapshot."));
                }).ToArray();
                manifest = LongMemEvalPreparationManifest.Create(
                    preparationId,
                    datasetSha256,
                    agentEvalRevision,
                    preparationId,
                    deployment,
                    deployment,
                    extractionDeployment,
                    embeddingDeployment,
                    embeddingDimensions,
                    options.MaxRelevantMessages,
                    expectation.ExtractionSourceTime,
                    preparedQuestions,
                    initialExtractionCalls);

                var seal = Stopwatch.StartNew();
                var store = new Neo4jLongMemEvalPreparationStore(driver);
                await store.SealAsync(manifest).ConfigureAwait(false);
                var sealedManifest = await store.ReadAsync(preparationId).ConfigureAwait(false);
                seal.Stop();
                manifestSealMilliseconds = seal.Elapsed.TotalMilliseconds;
                if (!string.Equals(
                        sealedManifest.Fingerprint,
                        manifest.Fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Prepared LongMemEval manifest read-back did not match the sealed fingerprint.");
                }
            }
            finally
            {
                var stop = Stopwatch.StartNew();
                if (baseProfile is not null)
                    await baseProfile.DisposeAsync().ConfigureAwait(false);
                stop.Stop();
                baseStopMilliseconds = stop.Elapsed.TotalMilliseconds;
                volumes.MarkBaseContainerStopped();
            }

            var cloneTimings = await volumes.CloneFrozenBaseAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var structured = await RunArmAsync(
                    LongMemEvalMemoryMode.Structured,
                    volumes.StructuredVolumeName,
                    manifest,
                    expectation,
                    preparationId,
                    options,
                    benchmarkOptions,
                    azureClient,
                    embeddingGenerator,
                    extractionDeployment,
                    deployment,
                    embeddingDimensions)
                .ConfigureAwait(false);
            var hybrid = await RunArmAsync(
                    LongMemEvalMemoryMode.Hybrid,
                    volumes.HybridVolumeName,
                    manifest,
                    expectation,
                    preparationId,
                    options,
                    benchmarkOptions,
                    azureClient,
                    embeddingGenerator,
                    extractionDeployment,
                    deployment,
                    embeddingDimensions)
                .ConfigureAwait(false);
            overall.Stop();

            var accepted =
                structured.Validation.Accepted &&
                hybrid.Validation.Accepted &&
                string.Equals(
                    structured.ManifestFingerprint,
                    hybrid.ManifestFingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(
                    structured.ManifestFingerprint,
                    manifest.Fingerprint,
                    StringComparison.Ordinal);
            var issues = structured.Validation.Issues
                .Select(issue => $"structured: {issue}")
                .Concat(hybrid.Validation.Issues.Select(issue => $"hybrid: {issue}"))
                .ToList();
            if (!string.Equals(
                    structured.ManifestFingerprint,
                    hybrid.ManifestFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    structured.ManifestFingerprint,
                    manifest.Fingerprint,
                    StringComparison.Ordinal))
            {
                issues.Add("Prepared clone manifest fingerprints do not match the sealed base.");
            }

            var destination = ResolveOutput(options.OutputPath, preparationId);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var extractionSnapshotFinal = extractionCalls.Snapshot();
            var report = new
            {
                schemaVersion = 3,
                runId = preparationId,
                generatedAtUtc = DateTimeOffset.UtcNow,
                accepted,
                validationIssues = issues,
                fingerprint = new
                {
                    dataset = Path.GetFileName(options.DatasetPath),
                    datasetSha256,
                    questions = options.Questions,
                    seed = options.Seed,
                    stratified = true,
                    answerModel = deployment,
                    judgeModel = deployment,
                    extractionModel = extractionDeployment,
                    embeddingModel = embeddingDeployment,
                    embeddingDimensions,
                    maxRelevantMessages = options.MaxRelevantMessages,
                    operatingModes = new[]
                    {
                        LongMemEvalMemoryMode.Structured.Fingerprint(),
                        LongMemEvalMemoryMode.Hybrid.Fingerprint()
                    },
                    extractionSourceTime = expectation.ExtractionSourceTime,
                    evidenceDetail = options.EvidenceDetail.ToString().ToLowerInvariant(),
                    oracleMode = options.OracleMode.ToString().ToLowerInvariant(),
                    judgeRetryAttempts = options.JudgeRetryAttempts,
                    neo4jImage = "neo4j:5.26",
                    agentEval = agentEvalRevision,
                    agentEvalDependency = "source-project:AgentEval.Memory"
                },
                preparation = new
                {
                    count = 1,
                    manifest.SchemaVersion,
                    manifest.PreparationId,
                    manifest.Fingerprint,
                    manifest.DatasetSha256,
                    manifest.AgentEvalRevision,
                    manifest.MessagesPrepared,
                    manifest.ExtractionUnitsPrepared,
                    manifest.InitialExtractionCalls,
                    questions = manifest.Questions,
                    extractionObserved = Project(extractionSnapshotFinal),
                    extractionRetryCalls =
                        Math.Max(0, extractionSnapshotFinal.Calls - manifest.InitialExtractionCalls),
                    timings = new
                    {
                        profileStartupMs = profileStartup.Elapsed.TotalMilliseconds,
                        storageAndEmbeddingMs = preparationTelemetry.Sum(item =>
                            item.StageTimings?.StorageMs ?? 0),
                        extractionAndPersistenceMs = preparationTelemetry.Sum(item =>
                            item.StageTimings?.ExtractionPersistenceMs ?? 0),
                        graphReadBackMs = preparationTelemetry.Sum(item =>
                            item.StageTimings?.GraphReadBackMs ?? 0),
                        manifestSealAndReadBackMs = manifestSealMilliseconds,
                        baseVolumeStopMs = baseStopMilliseconds,
                        structuredCloneMs = cloneTimings.StructuredMilliseconds,
                        hybridCloneMs = cloneTimings.HybridMilliseconds
                    }
                },
                arms = new
                {
                    structured = ProjectArm(structured, options.EvidenceDetail),
                    hybrid = ProjectArm(hybrid, options.EvidenceDetail)
                },
                totalWallMs = overall.Elapsed.TotalMilliseconds,
                timingScope =
                    "Local Docker and provider characterization only; not deployment latency. Provider aggregate duration is reported separately and is not added to wall time."
            };
            await File.WriteAllTextAsync(
                destination,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }) +
                Environment.NewLine).ConfigureAwait(false);

            if (!accepted)
            {
                foreach (var issue in issues)
                    Console.Error.WriteLine($"longmemeval: validation: {issue}");
                Console.Error.WriteLine(
                    $"longmemeval: rejected prepared-pair diagnostic report {destination}");
                return 1;
            }

            Console.WriteLine(
                $"longmemeval: prepared pair accepted; structured={structured.Result.OverallAccuracy:F1}% hybrid={hybrid.Result.OverallAccuracy:F1}%.");
            Console.WriteLine($"longmemeval: report {destination}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: prepared pair failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<PreparedArmExecution> RunArmAsync(
        LongMemEvalMemoryMode mode,
        string volumeName,
        LongMemEvalPreparationManifest expectedManifest,
        LongMemEvalPreparationExpectation expectation,
        string scopeRunId,
        PreparedPairOptions options,
        ExternalBenchmarkOptions benchmarkOptions,
        AzureOpenAIClient azureClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        string extractionDeployment,
        string deployment,
        int embeddingDimensions)
    {
        using var answerCalls = new LongMemEvalChatCallMeter(
            azureClient.GetChatClient(deployment).AsIChatClient());
        using var judgeCalls = new LongMemEvalChatCallMeter(
            azureClient.GetChatClient(deployment).AsIChatClient());
        using var diagnosticCalls = new LongMemEvalChatCallMeter(
            azureClient.GetChatClient(deployment).AsIChatClient());
        using var evaluationExtractionCalls = new LongMemEvalChatCallMeter(
            new ProviderCompatibleExtractionChatClient(
                azureClient.GetChatClient(extractionDeployment).AsIChatClient()));
        var total = Stopwatch.StartNew();
        var profileStartup = Stopwatch.StartNew();
        await using var profile = await LongMemEvalMemoryProfile.StartAsync(
                embeddingGenerator,
                evaluationExtractionCalls,
                mode,
                extractionDeployment,
                embeddingDimensions,
                Console.Out,
                CancellationToken.None,
                volumeName)
            .ConfigureAwait(false);
        profileStartup.Stop();

        var validationTiming = Stopwatch.StartNew();
        var driver = profile.Services.GetRequiredService<IDriver>();
        var manifest = await new Neo4jLongMemEvalPreparationStore(driver)
            .ReadAsync(expectedManifest.PreparationId)
            .ConfigureAwait(false);
        var state = new LongMemEvalPreparedState(
            manifest,
            scopeRunId,
            expectation);
        validationTiming.Stop();
        var evidenceIndex = LongMemEvalEvidenceIndex.Load(
            options.DatasetPath,
            benchmarkOptions);
        var adapter = new AgentMemoryLongMemEvalAdapter(
            profile.Services.GetRequiredService<IMemoryService>(),
            answerCalls,
            scopeRunId,
            new LongMemEvalAdapterOptions
            {
                MemoryMode = mode,
                PreparedMemory = true,
                PreparedState = state,
                MaxRelevantMessages = options.MaxRelevantMessages,
                MinSimilarityScore = 0,
                ModelId = deployment,
                EvidenceIndex = evidenceIndex,
                EvidenceDetail = options.EvidenceDetail,
                RequireGraphReadBack = true,
                GraphProbe = new Neo4jLongMemEvalGraphProbe(driver)
            });
        var runner = LongMemEvalBenchmarkRunner.Create(
            judgeCalls,
            options.DatasetPath);
        var result = await runner.RunAsync(
                adapter,
                new AgentBenchmarkConfig
                {
                    AgentName = adapter.Name,
                    ModelId = deployment,
                    ReducerStrategy =
                        $"AgentMemory prepared {mode.ToString().ToLowerInvariant()} recall",
                    MemoryProvider = "AgentMemory .NET / Neo4j 5.26 frozen clone"
                },
                benchmarkOptions)
            .ConfigureAwait(false);
        var diagnostics = await LongMemEvalPostRunDiagnostics.RunAsync(
                diagnosticCalls,
                evidenceIndex,
                result.QuestionResults,
                adapter.QuestionTelemetry,
                options.OracleMode,
                options.JudgeRetryAttempts,
                retainContent: options.EvidenceDetail == LongMemEvalEvidenceDetail.Content)
            .ConfigureAwait(false);
        var answerSnapshot = answerCalls.Snapshot();
        var judgeSnapshot = judgeCalls.Snapshot();
        var diagnosticSnapshot = diagnosticCalls.Snapshot();
        var extractionSnapshot = evaluationExtractionCalls.Snapshot();
        var validation = LongMemEvalRunValidator.Validate(
            options.Questions,
            result.TotalLlmCalls,
            adapter.QuestionTelemetry,
            result.QuestionResults,
            answerSnapshot,
            judgeSnapshot,
            extractionSnapshot,
            expectedInitialExtractionCalls: 0);
        total.Stop();
        return new PreparedArmExecution(
            mode,
            manifest.Fingerprint,
            adapter.QuestionTelemetry,
            result,
            diagnostics,
            validation,
            answerSnapshot,
            judgeSnapshot,
            diagnosticSnapshot,
            extractionSnapshot,
            new PreparedArmTimings(
                profileStartup.Elapsed.TotalMilliseconds,
                validationTiming.Elapsed.TotalMilliseconds,
                adapter.QuestionTelemetry.Sum(item =>
                    item.StageTimings?.RetrievalMs ?? 0),
                adapter.QuestionTelemetry.Sum(item =>
                    item.StageTimings?.AnswerMs ?? 0),
                total.Elapsed.TotalMilliseconds));
    }

    private static object ProjectArm(
        PreparedArmExecution arm,
        LongMemEvalEvidenceDetail evidenceDetail) =>
        new
        {
            mode = arm.Mode.ToString().ToLowerInvariant(),
            arm.ManifestFingerprint,
            accepted = arm.Validation.Accepted,
            validationIssues = arm.Validation.Issues,
            messagesPrepared = arm.Telemetry.Sum(item => item.MessagesPrepared),
            messagesStoredDuringEvaluation = arm.Telemetry.Sum(item => item.MessagesStored),
            extractionUnitsPrepared = arm.Telemetry.Sum(item => item.ExtractionUnitsPrepared),
            extractionUnitsDuringEvaluation = arm.Telemetry.Sum(item => item.ExtractionUnits),
            itemsRetrieved = arm.Telemetry.Sum(item => item.ItemsRetrieved),
            rawMessagesRetrieved = arm.Telemetry.Sum(item => item.RawMessagesRetrieved),
            entitiesRetrieved = arm.Telemetry.Sum(item => item.EntitiesRetrieved),
            factsRetrieved = arm.Telemetry.Sum(item => item.FactsRetrieved),
            preferencesRetrieved = arm.Telemetry.Sum(item => item.PreferencesRetrieved),
            questions = arm.Telemetry,
            timings = new
            {
                arm.Timings.ProfileStartupMs,
                arm.Timings.PreparedStateValidationMs,
                arm.Timings.RecallMs,
                arm.Timings.AnswerMs,
                judgeProviderMs = arm.JudgeCalls.Duration.TotalMilliseconds,
                arm.Timings.TotalEvaluationMs
            },
            callAccounting = new
            {
                benchmarkLlmCalls = arm.Result.TotalLlmCalls,
                diagnosticLlmCalls = arm.Diagnostics.DiagnosticLlmCalls,
                observed = new
                {
                    answer = Project(arm.AnswerCalls),
                    judge = Project(arm.JudgeCalls),
                    extraction = Project(arm.ExtractionCalls),
                    diagnostics = Project(arm.DiagnosticCalls)
                }
            },
            postRunDiagnostics = arm.Diagnostics,
            result = arm.Validation.Accepted
                ? LongMemEvalReportProjection.CreateAcceptedResult(
                    arm.Result,
                    evidenceDetail)
                : null
        };

    private static object Project(LongMemEvalChatCallSnapshot snapshot) => new
    {
        snapshot.Calls,
        snapshot.Failures,
        durationMs = snapshot.Duration.TotalMilliseconds
    };

    private static void ValidatePreparationTelemetry(
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry,
        int expectedQuestions)
    {
        if (telemetry.Count != expectedQuestions ||
            telemetry.Any(item =>
                !string.Equals(item.Status, "prepared", StringComparison.Ordinal) ||
                item.MessagesStored <= 0 ||
                item.ExtractionUnits <= 0 ||
                item.ItemsRetrieved != 0 ||
                item.GraphReadBack is null ||
                item.GraphReadBack.TotalLearned == 0 ||
                !item.GraphReadBack.CompleteProvenance))
        {
            throw new InvalidOperationException(
                "LongMemEval preparation did not prove nonzero storage, extraction, and complete graph read-back for every question.");
        }
    }

    private static string AgentEvalRevision()
    {
        var assembly = typeof(ExternalBenchmarkOptions).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    private static PreparedPairOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }

        return new PreparedPairOptions(
            Value("--dataset") ?? string.Empty,
            ParsePositive(Value("--questions"), DefaultQuestions, "--questions"),
            ParsePositive(Value("--seed"), DefaultSeed, "--seed"),
            ParsePositive(Value("--max-relevant"), DefaultMaxRelevant, "--max-relevant"),
            ParseEvidenceDetail(Value("--evidence-detail")),
            ParseOracleMode(Value("--oracle")),
            ParseNonNegative(Value("--judge-retries"), 2, "--judge-retries"),
            Value("--output"));
    }

    private static void Validate(PreparedPairOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DatasetPath))
            throw new ArgumentException("--dataset <longmemeval_s_cleaned.json> is required.");
        if (!File.Exists(options.DatasetPath))
            throw new FileNotFoundException("LongMemEval dataset not found.", options.DatasetPath);
    }

    private static int ParsePositive(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
        return parsed;
    }

    private static int ParseNonNegative(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ArgumentException($"{option} must be a non-negative integer.");
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

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is required; refusing to create a synthetic LongMemEval score.");

    private static string ResolveOutput(string? requested, string runId) =>
        Path.GetFullPath(requested ??
            Path.Combine(
                "artifacts",
                "evaluation",
                runId,
                "prepared-pair-report.json"));

    private sealed record PreparedPairOptions(
        string DatasetPath,
        int Questions,
        int Seed,
        int MaxRelevantMessages,
        LongMemEvalEvidenceDetail EvidenceDetail,
        LongMemEvalOracleMode OracleMode,
        int JudgeRetryAttempts,
        string? OutputPath);

    private sealed record PreparedArmTimings(
        double ProfileStartupMs,
        double PreparedStateValidationMs,
        double RecallMs,
        double AnswerMs,
        double TotalEvaluationMs);

    private sealed record PreparedArmExecution(
        LongMemEvalMemoryMode Mode,
        string ManifestFingerprint,
        IReadOnlyList<LongMemEvalQuestionTelemetry> Telemetry,
        ExternalBenchmarkResult Result,
        LongMemEvalPostRunDiagnosticsResult Diagnostics,
        LongMemEvalRunValidation Validation,
        LongMemEvalChatCallSnapshot AnswerCalls,
        LongMemEvalChatCallSnapshot JudgeCalls,
        LongMemEvalChatCallSnapshot DiagnosticCalls,
        LongMemEvalChatCallSnapshot ExtractionCalls,
        PreparedArmTimings Timings);
}
