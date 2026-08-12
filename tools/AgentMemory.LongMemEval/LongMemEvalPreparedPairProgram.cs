using AgentMemory.Abstractions.Options;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentMemory.Core.Memory;
using AgentMemory.Extraction.Llm;
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
    private const int DefaultPreparationWorkers = 10;
    private const int DefaultMaxSessionsPerBatch = 4;
    private const int DefaultMaxInputTokens = 100_000;
    private const int DefaultMaxConcurrentBatchesPerExtraction = 4;
    private const int DefaultMaxConcurrentExtractionBatches = 12;
    private const int DefaultCheckpointTimeoutSeconds = 3_600;
    private const int DefaultProviderNoProgressTimeoutSeconds = 600;
    private const double ColdBuildSpeedTargetMilliseconds = 900_000d;

    private const int FixedTenExpectedSourceSessions = 474;

    /// <summary>
    /// How much of a corpus may be missing to provider content refusals before it stops being usable.
    /// </summary>
    /// <remarks>
    /// A handful of sessions out of thousands is noise; hundreds is a different corpus wearing the
    /// same name. 2% is deliberately low — the observed rate on this dataset is a fraction of one
    /// percent, so a run approaching this limit has something new happening and should stop.
    /// </remarks>
    private const double MaximumRefusedSessionShare = 0.02;
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            Validate(options);
            var diagnosticEvidenceIndex = PreflightDiagnosticSelection(options);
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
                options.MaxRelevantMessages,
                // Typed sampling reaches the PREPARATION, not only the evaluation: an episodic-only
                // sample selects different questions, whose conversation histories have to be ingested
                // for the corpus to answer them at all. This is why an episodic arm cannot reuse a
                // stratified corpus and needs its own cold build.
                includeQuestionTypes: LongMemEvalMemoryTypeSelection.TaskTypesFor(options.MemoryTypes),
                // The unanswerable class. Without it the sufficiency AUC has nothing to order
                // against: a stratified 50 gave 32 present and 1 absent, so the metric rested on one
                // observation and the two arms reported 0.969 and 0.094 from the same corpus.
                abstentionPolicy: options.AbstentionPolicy,
                abstentionTargetProportion: options.AbstentionProportion);
            var datasetSha256 = Convert.ToHexStringLower(
                SHA256.HashData(
                    await File.ReadAllBytesAsync(options.DatasetPath).ConfigureAwait(false)));
            // Checked here because this is the last cheap moment: a cold build is 7-9 hours, and a
            // dataset that is not the one every sealed corpus used produces numbers comparable to
            // nothing. Warns rather than fails -- a different variant is legitimate work, and the
            // actual sha travels in the fingerprint -- but it must never pass silently.
            if (LongMemEvalDatasetLocator.DescribeMismatch(options.DatasetPath, datasetSha256)
                is { } datasetWarning)
            {
                Console.Error.WriteLine(datasetWarning);
            }
            var agentEvalRevision = AgentEvalRevision();
            var expectation = LongMemEvalPreparationFingerprint.Expect(
                datasetSha256,
                agentEvalRevision,
                deployment,
                deployment,
                extractionDeployment,
                embeddingDeployment,
                embeddingDimensions,
                options.MaxRelevantMessages,
                extractionResponseContract: options.IsDiagnostic
                    ? "json-object"
                    : LlmMultiSessionExtractionResponseContract.Version,
                useUnifiedExtraction: !options.IsDiagnostic,
                useMultiSessionBatchExtraction: !options.IsDiagnostic,
                preparationWorkers: options.IsDiagnostic ? 1 : options.PreparationWorkers,
                maxSessionsPerBatch: options.MaxSessionsPerBatch,
                maxInputTokens: options.MaxInputTokens,
                maxConcurrentBatchesPerExtraction:
                    options.IsDiagnostic ? 1 : options.MaxConcurrentBatchesPerExtraction,
                maxConcurrentExtractionBatches:
                    options.IsDiagnostic ? 0 : options.MaxConcurrentExtractionBatches);
            var preparationId =
                $"longmemeval-prepared-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            var overall = Stopwatch.StartNew();

            // G3B.12-R. Reuse attaches to a retained cold build instead of paying 121 provider calls
            // to rebuild one — and because extraction is non-deterministic, a rebuild would not
            // reproduce the graph being investigated anyway.
            var reusing = !string.IsNullOrWhiteSpace(options.ReusePreparedVolume);

            // Before anything is created or adopted, so this run's own volumes can never be
            // candidates. Retaining a build without ever sweeping is a disk leak, and a killed run
            // never gets to clean up after itself.
            if (!options.NoOrphanSweep)
            {
                await LongMemEvalOrphanSweep
                    .RunAsync(options.ReusePreparedVolume, Console.Out, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await using var volumes = reusing
                ? await LongMemEvalPreparedVolumes
                    .AdoptAsync(
                        options.ReusePreparedVolume!,
                        CancellationToken.None,
                        retain: options.RetainPreparedVolumes)
                    .ConfigureAwait(false)
                : await LongMemEvalPreparedVolumes
                    .CreateAsync(
                        preparationId,
                        CancellationToken.None,
                        retain: options.RetainPreparedVolumes)
                    .ConfigureAwait(false);
            if (options.RetainPreparedVolumes)
            {
                // Printed so the retained build can be re-attached and inspected, and so the operator
                // knows cleanup is now theirs.
                Console.WriteLine(
                    "longmemeval: retaining prepared volumes (cleanup is now manual): " +
                    $"{volumes.BaseVolumeName}, {volumes.StructuredVolumeName}, {volumes.HybridVolumeName}");
            }
            using var extractionCalls = new LongMemEvalChatCallMeter(
                new ProviderCompatibleExtractionChatClient(
                    azureClient.GetChatClient(extractionDeployment).AsIChatClient()));
            LongMemEvalPreparationManifest manifest;
            IReadOnlyList<LongMemEvalRefusedEvidence.RefusedSession> refusedEvidence = [];
            IReadOnlyList<LongMemEvalPreparedCorpusDrift.Difference> reuseDrift = [];
            IReadOnlyList<LongMemEvalQuestionTelemetry> preparationTelemetry;
            LongMemEvalPreparedBatchExecution? batchExecution = null;
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
                        baseVolumeName,
                        enableBatchedPreparation: !options.IsDiagnostic,
                        multiSessionBatch: options.MultiSessionBatch,
                        maxConcurrentBatchesPerExtraction:
                            options.IsDiagnostic ? 1 : options.MaxConcurrentBatchesPerExtraction,
                        maxConcurrentExtractionBatches:
                            options.IsDiagnostic ? 0 : options.MaxConcurrentExtractionBatches,
                        usePredicateVocabulary: options.UsePredicateVocabulary,
                        assistantContent: options.AssistantContent)
                    .ConfigureAwait(false);
                profileStartup.Stop();

                var evidenceIndex = diagnosticEvidenceIndex ??
                    LongMemEvalEvidenceIndex.Load(
                        options.DatasetPath, benchmarkOptions);
                var questions = evidenceIndex.Questions.ToArray();
                if (questions.Length != options.Questions)
                {
                    throw new InvalidOperationException(
                        $"Prepared LongMemEval selected {questions.Length} questions; expected {options.Questions}.");
                }

                var driver = baseProfile.Services.GetRequiredService<IDriver>();
                if (reusing)
                {
                    // Reuse: the retained volume describes itself. preparationId MUST come from the
                    // sealed manifest and never be generated - the per-question scope hashes derive
                    // from it, so a generated one makes every question trip
                    // prepared-manifest-mismatch. Preparation is skipped entirely; the clone and
                    // both evaluation arms below are unchanged.
                    var reuseStore = new Neo4jLongMemEvalPreparationStore(driver);
                    preparationId = await reuseStore
                        .ReadSealedPreparationIdAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    manifest = await reuseStore
                        .ReadAsync(preparationId).ConfigureAwait(false);

                    // A reused run performs no preparation, so its preparation timings are genuinely
                    // empty rather than zeroed-out real work. Reporting them as empty keeps a reused
                    // run from being mistaken for a cold build that happened to be instant.
                    preparationTelemetry = Array.Empty<LongMemEvalQuestionTelemetry>();

                    // The check that makes reuse safe. VerifyIntegrity only proves the manifest is
                    // self-consistent; nothing compared it to THIS run. A corpus ingested under other
                    // settings evaluates perfectly well and reports the wrong configuration over it.
                    reuseDrift = LongMemEvalPreparedCorpusDrift.Compare(
                        manifest,
                        new PreparedCorpusIdentity
                        {
                            DatasetSha256 = datasetSha256,
                            ExtractionModelId = extractionDeployment,
                            EmbeddingModelId = embeddingDeployment,
                            EmbeddingDimensions = embeddingDimensions,
                            AssistantContent = options.AssistantContent.ToString(),
                            UsePredicateVocabulary = options.UsePredicateVocabulary,
                            ExtractionVocabularySha256 = MemoryPredicateSeedVocabulary.Fingerprint,
                            QueryRelationLexiconSha256 = MemoryRelationSeedTable.Fingerprint,
                            QuestionSeed = options.Seed,
                            QuestionCount = options.Questions,
                            MemoryTypes = options.MemoryTypes,
                            AbstentionPolicy = options.AbstentionPolicy.ToString(),
                        });

                    if (reuseDrift.Count > 0 && !options.AllowStalePrepared)
                    {
                        throw new InvalidOperationException(
                            LongMemEvalPreparedCorpusDrift.Explain(
                                options.ReusePreparedVolume!, manifest.PreparedAtUtc, reuseDrift));
                    }
                    if (reuseDrift.Count > 0)
                    {
                        Console.Error.WriteLine(
                            $"longmemeval: WARNING - reusing a corpus that drifted on {reuseDrift.Count} "
                            + "ingestion-affecting field(s); --allow-stale-prepared was passed, and the "
                            + "drift is recorded in the report.");
                        foreach (var difference in reuseDrift)
                            Console.Error.WriteLine($"  - {difference}");
                    }

                    Console.WriteLine(
                        $"longmemeval: reusing prepared build {preparationId}; " +
                        $"fingerprint {manifest.Fingerprint}; prepared {manifest.PreparedAtUtc}; " +
                        "no extraction will run.");
                }
                else
                {
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
                        DiagnosticSourceSessionOrdinal = options.DiagnosticSourceSessionOrdinal,
                        ExtractionProgress = (completed, total) => Console.WriteLine(
                            $"longmemeval: preparation extraction units {completed}/{total}.")
                    });

                var questionIndexes = options.IsDiagnostic
                    ? new[] { options.DiagnosticQuestionPosition!.Value - 1 }
                    : Array.Empty<int>();
                foreach (var index in questionIndexes)
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

                if (options.IsDiagnostic)
                {
                    var diagnosticSnapshot = extractionCalls.Snapshot();
                    if (diagnosticSnapshot.Calls != 4 ||
                        diagnosticSnapshot.Failures != 0)
                    {
                        throw new InvalidOperationException(
                            $"Diagnostic extraction accounting mismatch: observed " +
                            $"{diagnosticSnapshot.Calls} calls and " +
                            $"{diagnosticSnapshot.Failures} failures; expected exactly " +
                            "4 calls and zero failures.");
                    }
                    var purposes = string.Join(
                        ", ",
                        diagnosticSnapshot.CallDetails
                            .GroupBy(detail => detail.Purpose)
                            .OrderBy(group => group.Key, StringComparer.Ordinal)
                            .Select(group => $"{group.Key}={group.Count()}"));
                    Console.WriteLine(
                        $"longmemeval: diagnostic-only extraction completed for question " +
                        $"{options.DiagnosticQuestionPosition}, source session " +
                        $"{options.DiagnosticSourceSessionOrdinal}: 4 calls / 0 failures; " +
                        $"purposes {purposes}; no report, clone, recall, answer, or judge executed.");
                    return 0;
                }
                var plans = LongMemEvalPreparedBatchExecutor.Preflight(
                    baseProfile.Services,
                    preparationId,
                    evidenceIndex,
                    questions,
                    options.MaxSessionsPerBatch,
                    options.MaxInputTokens);
                var plannedCalls = plans.Sum(plan => (long)plan.BatchCount);
                var plannedSourceSessions =
                    plans.Sum(plan => plan.SourceSessionCount);
                var plannedInputTokens =
                    plans.Sum(plan => plan.TotalEstimatedInputTokens);
                // The canonical guard pins the STRATIFIED ten to a recorded plan, so a change in
                // sampling or batching cannot pass unnoticed. A typed sample is a different plan by
                // construction -- --memory-types episodic selects different questions, with a
                // different number of source sessions -- so the canonical number does not describe
                // it. Skipped rather than adjusted, and said out loud: silently dropping a guard that
                // exists to catch sampling drift would be worse than not having it.
                // Canonical means "the plan every recorded run used": default size and seed, no type
                // filter, and abstention left as-sampled. Anything that changes WHICH questions are
                // drawn produces a different plan, so the recorded source-session count cannot
                // describe it. Enumerated rather than inferred, so adding a third sampling knob later
                // fails this comparison loudly instead of silently inheriting the canonical count.
                var canonicalSampling = options.MemoryTypes.Count == 0
                    && options.AbstentionPolicy == AbstentionSamplingPolicy.AsSampled;
                var canonicalPlan = options.Questions == DefaultQuestions
                    && options.Seed == DefaultSeed
                    && canonicalSampling;
                if (canonicalPlan && plannedSourceSessions != FixedTenExpectedSourceSessions)
                {
                    throw new InvalidOperationException(
                        $"Canonical fixed-ten preflight produced {plannedSourceSessions} " +
                        $"source sessions; expected exactly {FixedTenExpectedSourceSessions}.");
                }
                if (!canonicalSampling)
                {
                    var how = options.MemoryTypes.Count > 0
                        ? $"types={string.Join("+", options.MemoryTypes)}"
                        : string.Empty;
                    var abstention = options.AbstentionPolicy == AbstentionSamplingPolicy.AsSampled
                        ? string.Empty
                        : $"abstention={options.AbstentionPolicy}";
                    Console.WriteLine(
                        "longmemeval: non-canonical sample ("
                        + string.Join(", ", new[] { how, abstention }.Where(s => s.Length > 0))
                        + $") -- the canonical fixed-ten source-session guard "
                        + $"({FixedTenExpectedSourceSessions}) does not apply, because this sample "
                        + "selects different questions by construction.");
                }
                Console.WriteLine(
                    $"longmemeval: frozen preparation preflight {plannedCalls} calls for " +
                    $"{plannedSourceSessions} source sessions and " +
                    $"{plannedInputTokens} estimated input tokens.");
                if (options.PreflightOnly)
                {
                    var preflightSnapshot = extractionCalls.Snapshot();
                    if (preflightSnapshot.Calls != 0 ||
                        preflightSnapshot.Failures != 0)
                    {
                        throw new InvalidOperationException(
                            "Preflight-only execution performed provider work.");
                    }
                    Console.WriteLine(
                        "longmemeval: preflight-only accepted; zero provider calls, " +
                        "zero graph writes, no report, clone, recall, answer, or judge executed.");
                    return 0;
                }
                if (options.CheckpointQuestions is int checkpointQuestions)
                {
                    var checkpointIndexes = LongMemEvalPreparedBatchExecutor
                        .SelectCheckpointQuestionIndexes(plans, checkpointQuestions);
                    var checkpointCalls = checkpointIndexes.Sum(
                        index => (long)plans[index].BatchCount);
                    var checkpointSourceSessions = checkpointIndexes.Sum(
                        index => (long)plans[index].SourceSessionCount);
                    var checkpointInputTokens = checkpointIndexes.Sum(
                        index => plans[index].TotalEstimatedInputTokens);
                    var checkpointFingerprint = Convert.ToHexStringLower(
                        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            schema = 1,
                            datasetSha256,
                            agentEvalRevision,
                            answerModelId = deployment,
                            extractionModelId = extractionDeployment,
                            embeddingModelId = embeddingDeployment,
                            embeddingDimensions,
                            options.MaxRelevantMessages,
                            options.PreparationWorkers,
                            // Both provider-concurrency knobs belong here: they determine the wall
                            // time this checkpoint projects, so omitting them let two runs with
                            // different concurrency share a fingerprint and have their projections
                            // compared as though equivalent.
                            options.MaxConcurrentBatchesPerExtraction,
                            options.MaxConcurrentExtractionBatches,
                            options.MaxSessionsPerBatch,
                            options.MaxInputTokens,
                            options.CheckpointTimeoutSeconds,
                            projectionSafetyMargin = 1.25d,
                            coldBuildSpeedTargetMilliseconds =
                                ColdBuildSpeedTargetMilliseconds,
                            questions = plans.Select((plan, index) => new
                            {
                                questionNumber = index + 1,
                                sourceSessions = plan.SourceSessionCount,
                                calls = plan.BatchCount,
                                estimatedInputTokens = plan.TotalEstimatedInputTokens
                            }).ToArray(),
                            selectedQuestionNumbers = checkpointIndexes
                                .Select(index => index + 1)
                                .ToArray()
                        })));
                    Console.WriteLine(
                        $"longmemeval: checkpoint {checkpointFingerprint}; questions " +
                        $"{string.Join(',', checkpointIndexes.Select(index => index + 1))}; " +
                        $"{checkpointCalls} calls, {checkpointSourceSessions} source sessions, " +
                        $"{checkpointInputTokens} estimated input tokens; " +
                        $"deadline {options.CheckpointTimeoutSeconds}s.");

                    var checkpointWall = Stopwatch.StartNew();
                    var checkpointExecution = await RunPreparedWithDiagnosticsAsync(
                            cancellationToken => LongMemEvalPreparedBatchExecutor.ExecuteAsync(
                                baseProfile.Services,
                                extractionCalls,
                                preparationId,
                                evidenceIndex,
                                questions,
                                plans,
                                deployment,
                                options.EvidenceDetail,
                                options.MaxRelevantMessages,
                                options.PreparationWorkers,
                                options.MaxSessionsPerBatch,
                                options.MaxInputTokens,
                                checkpointIndexes,
                                cancellationToken),
                            extractionCalls,
                            checkpointCalls,
                            TimeSpan.FromSeconds(options.CheckpointTimeoutSeconds),
                            baseProfile.Services,
                            TimeSpan.FromSeconds(options.ProviderNoProgressTimeoutSeconds),
                            "checkpoint",
                            Console.Out)
                        .ConfigureAwait(false);
                    checkpointWall.Stop();
                    ValidateCheckpointTelemetry(
                        checkpointExecution.Telemetry, questions, plans, checkpointIndexes);
                    var checkpointSnapshot = extractionCalls.Snapshot();
                    if (checkpointExecution.PlannedCalls != checkpointCalls ||
                        checkpointExecution.EstimatedInputTokens != checkpointInputTokens ||
                        checkpointSnapshot.Calls != checkpointCalls ||
                        checkpointSnapshot.CompletedCalls != checkpointCalls ||
                        checkpointSnapshot.RetryCalls != 0 ||
                        checkpointSnapshot.MaximumConcurrency <= 1 ||
                        checkpointSnapshot.MaximumConcurrency >
                        options.MaxConcurrentExtractionBatches ||
                        checkpointSnapshot.Failures != 0 ||
                        checkpointExecution.MaximumConcurrency <= 0 ||
                        checkpointExecution.MaximumConcurrency >
                        Math.Min(checkpointQuestions, options.PreparationWorkers))
                    {
                        throw new InvalidOperationException(
                            "LongMemEval checkpoint accounting or concurrency guard failed.");
                    }

                    var projectedMilliseconds = LongMemEvalPreparedBatchExecutor
                        .ProjectFullPreparationMilliseconds(
                            plannedCalls,
                            plannedSourceSessions,
                            plannedInputTokens,
                            checkpointCalls,
                            checkpointSourceSessions,
                            checkpointInputTokens,
                            checkpointWall.Elapsed.TotalMilliseconds,
                            profileStartup.Elapsed.TotalMilliseconds);
                    Console.WriteLine(
                        $"longmemeval: checkpoint completed in " +
                        $"{checkpointWall.Elapsed.TotalMilliseconds:F2} ms wall; " +
                        $"{checkpointSnapshot.Duration.TotalMilliseconds:F2} ms aggregate provider; " +
                        $"maximum provider concurrency {checkpointSnapshot.MaximumConcurrency}; " +
                        $"maximum preparation concurrency {checkpointExecution.MaximumConcurrency}; " +
                        $"conservative full cold-build projection {projectedMilliseconds:F2} ms.");
                    Console.WriteLine(
                        $"longmemeval: cold-build speed target met: " +
                        $"{projectedMilliseconds <= ColdBuildSpeedTargetMilliseconds}.");
                    Console.WriteLine(
                        "longmemeval: checkpoint accepted; no manifest, clone, recall, " +
                        "answer, judge, or report executed.");
                    return 0;
                }
                batchExecution = await RunPreparedWithDiagnosticsAsync(
                        cancellationToken => LongMemEvalPreparedBatchExecutor.ExecuteAsync(
                            baseProfile.Services,
                            extractionCalls,
                            preparationId,
                            evidenceIndex,
                            questions,
                            plans,
                            deployment,
                            options.EvidenceDetail,
                            options.MaxRelevantMessages,
                            options.PreparationWorkers,
                            options.MaxSessionsPerBatch,
                            options.MaxInputTokens,
                            questionIndexes: null,
                            cancellationToken),
                        extractionCalls,
                        plannedCalls,
                        TimeSpan.FromSeconds(options.CheckpointTimeoutSeconds),
                        baseProfile.Services,
                        TimeSpan.FromSeconds(options.ProviderNoProgressTimeoutSeconds),
                        "fixed-ten preparation",
                        Console.Out)
                    .ConfigureAwait(false);
                preparationTelemetry = batchExecution.Telemetry;
                ValidatePreparationTelemetry(preparationTelemetry, questions.Length);
                var initialExtractionCalls = batchExecution.PlannedCalls;
                var extractionSnapshot = extractionCalls.Snapshot();
                // Routed through the SAME decision as the per-question guard, so the two cannot
                // drift apart. They already had: the per-question one was refined to accept excess
                // calls a recorded split or retry explains, this one still demanded exact equality,
                // and a 75-minute rebuild died at 682 calls against 680 planned with failures 0 and
                // retries 0. Split sub-calls run under their own Activity, so the activity-based
                // retry counter cannot see them - a split appears as bare extra calls that neither
                // counter accounts for. One guard accepted that shape and the other rejected it.
                var batchDiagnostics = baseProfile.Services
                    .GetRequiredService<LlmExtractionBatchDiagnostics>().Snapshot();
                var recordedSplits = batchDiagnostics.Splits;

                // A corpus with gaps must never look complete. The provider refuses some content
                // outright -- two sessions of this public research dataset tripped an Azure policy --
                // and skipping them is far better than losing a 616-call preparation to it. But the
                // loss is real: those sessions contributed nothing to the graph, so any question
                // depending on them is unanswerable for a reason that has nothing to do with recall.
                //
                // Tolerated up to a small share and REFUSED beyond it, because the failure changes
                // character with scale: a handful of sessions out of 2,418 is noise, and hundreds is a
                // different corpus wearing the same name.
                if (batchDiagnostics.SessionsRefused > 0)
                {
                    var refusedShare = (double)batchDiagnostics.SessionsRefused / Math.Max(1, plannedSourceSessions);
                    Console.Error.WriteLine(
                        $"longmemeval: WARNING - the provider refused {batchDiagnostics.ContentRejections} "
                        + $"source session(s) on content grounds, {batchDiagnostics.SessionsRefused} of "
                        + $"{plannedSourceSessions} ({refusedShare:P1}). Their content is absent from this "
                        + "corpus, and it is recorded in the manifest so a later reuse cannot mistake it "
                        + "for complete.");
                    // The ids, so the refusal is investigable. Content filters recur, and "something
                    // was refused" is not a report anyone can act on or raise with a provider.
                    //
                    // And, more important than the count: did it COST anything? A refused session
                    // holding a question's gold evidence makes that question unanswerable from memory
                    // for a reason unrelated to retrieval, so scoring it wrong attributes a
                    // content-policy decision to recall quality.
                    refusedEvidence = LongMemEvalRefusedEvidence.Analyse(
                        batchDiagnostics.RefusedSessionIds ?? [], questions);
                    foreach (var refused in refusedEvidence)
                    {
                        var cost = refused.HeldGoldEvidence
                            ? $"HELD GOLD EVIDENCE for question {refused.QuestionId}"
                            : refused.QuestionId is null
                                ? "unresolved question - treat as unknown, not harmless"
                                : $"context only for question {refused.QuestionId}";
                        Console.Error.WriteLine($"  - refused session: {refused.SessionId} ({cost})");
                    }
                    if (refusedShare > MaximumRefusedSessionShare)
                    {
                        throw new InvalidOperationException(
                            $"Provider content refusals cost {batchDiagnostics.SessionsRefused} of "
                            + $"{plannedSourceSessions} source sessions ({refusedShare:P1}), above the "
                            + $"{MaximumRefusedSessionShare:P0} tolerance. A corpus missing that much is a "
                            + "different corpus, and measuring against it would attribute the loss to recall.");
                    }
                }
                var successfulCalls = extractionSnapshot.Calls - extractionSnapshot.Failures;
                var accountingAcceptable =
                    AgentMemoryLongMemEvalAdapter.IsBatchAccountingAcceptable(
                        successfulCalls,
                        successfulCalls,
                        otherCalls: 0,
                        recordedSplits,
                        extractionSnapshot.RetryCalls,
                        checked((int)initialExtractionCalls));
                if (!accountingAcceptable ||
                    extractionSnapshot.CompletedCalls != extractionSnapshot.Calls ||
                    extractionSnapshot.MaximumConcurrency <= 1 ||
                    extractionSnapshot.MaximumConcurrency > options.MaxConcurrentExtractionBatches)
                {
                    throw new InvalidOperationException(
                        $"Prepared LongMemEval extraction accounting mismatch: started/completed " +
                        $"{extractionSnapshot.Calls}/{extractionSnapshot.CompletedCalls}, failures " +
                        $"{extractionSnapshot.Failures}, retries {extractionSnapshot.RetryCalls}, maximum " +
                        $"provider concurrency {extractionSnapshot.MaximumConcurrency}; expected exactly " +
                        $"at least {initialExtractionCalls} successful calls with any excess " +
                        $"explained by a recorded split or retry (splits={recordedSplits}), every " +
                        $"started call completed, and concurrency 2..{options.MaxConcurrentExtractionBatches}.");
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
                    initialExtractionCalls,
                    useJsonResponseFormat: expectation.UseJsonResponseFormat,
                    extractionResponseContract: expectation.ExtractionResponseContract,
                    useUnifiedExtraction: true,
                    useMultiSessionBatchExtraction: true,
                    preparationWorkers: options.PreparationWorkers,
                    maxSessionsPerBatch: options.MaxSessionsPerBatch,
                    maxInputTokens: options.MaxInputTokens,
                    maxConcurrentBatchesPerExtraction: options.MaxConcurrentBatchesPerExtraction,
                    maxConcurrentExtractionBatches: options.MaxConcurrentExtractionBatches,
                    // Schema 6. The corpus now describes how it was ingested, so a later reuse can be
                    // refused instead of silently measuring a graph built under other settings.
                    assistantContent: options.AssistantContent.ToString(),
                    usePredicateVocabulary: options.UsePredicateVocabulary,
                    extractionVocabularySha256: MemoryPredicateSeedVocabulary.Fingerprint,
                    queryRelationLexiconSha256: MemoryRelationSeedTable.Fingerprint,
                    abstentionPolicy: options.AbstentionPolicy.ToString(),
                    refusedSourceSessions: checked((int)batchDiagnostics.SessionsRefused),
                    preparedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                    description: options.Description ?? string.Empty,
                    memoryTypes: options.MemoryTypes,
                    questionSeed: options.Seed);

                var seal = Stopwatch.StartNew();
                var store = new Neo4jLongMemEvalPreparationStore(driver);
                await store.SealAsync(manifest).ConfigureAwait(false);
                // Catalogued after sealing, never before: an entry for a corpus that failed to seal
                // would advertise something unusable, and the registry's only job is to be trustworthy
                // enough to pick from.
                if (options.RetainPreparedVolumes)
                    LongMemEvalPreparedCorpusRegistry.Record(baseVolumeName, manifest, Console.Out);
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

            var runId = ResolveRunId(preparationId, reusing, DateTimeOffset.UtcNow);
            var destination = ResolveOutput(options.OutputPath, runId);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var extractionSnapshotFinal = extractionCalls.Snapshot();
            var report = new
            {
                schemaVersion = 3,
                runId,
                // The preparation this run measured, which for a reused run is an earlier run's.
                scopeRunId = preparationId,
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
                    extractionResponseFormat = expectation.UseJsonResponseFormat
                        ? expectation.ExtractionResponseContract
                        : "unspecified",
                    extractionExecution = "unified-multi-session-batch",
                    // The harness asks for temperature 0; this deployment refuses it and the
                    // compatibility wrapper drops the request, so extraction runs at the provider
                    // default. Recorded because three builds of one configuration shared only 7.5% of
                    // their stored triples, and an artifact that implies determinism it never had is
                    // worse than one that admits the gap.
                    extractionTemperatureHonoured = !ProviderCompatibleExtractionChatClient.TemperatureRequestWasDropped,
                    preparationWorkers = options.PreparationWorkers,
                    // Null on a reused run: this run observed no preparation concurrency because it
                    // performed no preparation. The nullable type is compiler-verified.
                    maximumObservedPreparationConcurrency =
                        batchExecution?.MaximumConcurrency,
                    maxSessionsPerBatch = options.MaxSessionsPerBatch,
                    maxInputTokens = options.MaxInputTokens,
                    maxConcurrentBatchesPerExtraction = options.MaxConcurrentBatchesPerExtraction,
                    maxConcurrentExtractionBatches = options.MaxConcurrentExtractionBatches,
                    preparationWatchdogSeconds = options.CheckpointTimeoutSeconds,
                    providerNoProgressWatchdogSeconds = options.ProviderNoProgressTimeoutSeconds,
                    // Retrieval-side settings belong in the fingerprint: they change the score, and
                    // without them two runs over the same frozen graph are indistinguishable in the
                    // artifact - which is precisely the comparison reuse exists to make.
                    expandFactsByPredicate = options.ExpandFactsByPredicate,
                    resolveQueryRelations = options.ResolveQueryRelations,
                    usePredicateVocabulary = options.UsePredicateVocabulary,
                    // Fingerprinted for the same reason the vocabulary is: it changes what
                    // gets stored, so two bases built under different modes are not
                    // comparable and must not be confusable in an artifact.
                    assistantContent = options.AssistantContent.ToString(),
                    // Records WHICH extractor ran. Without it, a single-session-unified run and a
                    // multi-session batch run are indistinguishable in the artifact despite using
                    // different code and different prompts.
                    multiSessionBatch = options.MultiSessionBatch,
                    maxItemsPerSourceSession = options.MaxItemsPerSourceSession,
                    // K6. Additive on top of the mode budget, so a score compared against a run
                    // without it is confounded with the larger context. Recorded here so no later
                    // reader can mistake the two runs for a controlled comparison.
                    graphRagItems = options.GraphRagItems,
                    // The vocabulary decides what is stored and the lexicon decides what is
                    // retrieved, so a run under a different table is not comparable to this one.
                    // Without these the artifact would not record which tables produced it.
                    extractionVocabularySha256 = MemoryPredicateSeedVocabulary.Fingerprint,
                    queryRelationLexiconSha256 = MemoryRelationSeedTable.Fingerprint,
                    // Empty on a cold build and on a matching reuse. Non-empty only when
                    // --allow-stale-prepared was used, so a result taken over a drifted corpus carries
                    // that fact with it instead of looking like any other run.
                    preparedCorpusDrift = reuseDrift.Select(d => d.ToString()).ToArray(),
                    // Empty on a clean build. Non-empty means questions in THIS sample lost evidence
                    // to a content policy, which is the difference between a number that is low and a
                    // number that is measuring the wrong thing.
                    refusedSessionsHoldingGoldEvidence = refusedEvidence
                        .Where(r => r.HeldGoldEvidence)
                        .Select(r => new { r.SessionId, r.QuestionId })
                        .ToArray(),
                    preparedCorpusPreparedAtUtc = reusing ? manifest.PreparedAtUtc : null,
                    preparedCorpusDescription = reusing ? manifest.Description : options.Description,
                    evidenceDetail = options.EvidenceDetail.ToString().ToLowerInvariant(),
                    oracleMode = options.OracleMode.ToString().ToLowerInvariant(),
                    judgeRetryAttempts = options.JudgeRetryAttempts,
                    neo4jImage = "neo4j:5.26",
                    agentEval = agentEvalRevision,
                    agentEvalDependency = "source-project:AgentEval.Memory"
                },
                preparation = LongMemEvalReportProjection.CreatePreparationSection(
                    manifest,
                    batchExecution,
                    preparationTelemetry,
                    Project(extractionSnapshotFinal),
                    extractionSnapshotFinal.Calls,
                    new LongMemEvalPreparationTimings(
                        profileStartup.Elapsed.TotalMilliseconds,
                        reusing ? null : manifestSealMilliseconds,
                        baseStopMilliseconds,
                        cloneTimings.StructuredMilliseconds,
                        cloneTimings.HybridMilliseconds),
                    options.ReusePreparedVolume),
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

            // Accepting a scored empty retrieval must never be quiet. It is a real failure of the
            // memory system that the run now measures instead of aborting on, and a reader comparing
            // two accuracies deserves to know one arm answered a question from nothing.
            foreach (var arm in new[] { ("structured", structured), ("hybrid", hybrid) })
            {
                var empties = arm.Item2.Telemetry
                    .Where(item => LongMemEvalRunValidator.IsScoredEmptyRetrieval(
                        item.Status, item.GraphReadBack))
                    .ToArray();
                foreach (var item in empties)
                {
                    Console.Error.WriteLine(
                        $"longmemeval: WARNING: {arm.Item1}: question {item.QuestionId} " +
                        $"(position {item.QuestionNumber}) retrieved NOTHING from a graph proven to " +
                        $"hold {item.GraphReadBack!.TotalLearned} learned items. Scored as a memory " +
                        "failure rather than aborting the run; this is a real retrieval defect.");
                }
            }

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

            // The headline is the line everyone quotes, and it used to say nothing about WHICH
            // extraction configuration produced it. This benchmark runs with UseUnifiedExtraction
            // ON (LongMemEvalMemoryProfile sets it from enableBatchedPreparation), while the shipped
            // default in LlmExtractionOptions is OFF. Every quality number here therefore describes a
            // configuration a default install does not get, and that went unstated for an entire
            // measurement track. It is stated here, next to the number, because a caveat that lives
            // only in a plan document does not travel with the result.
            Console.WriteLine(
                $"longmemeval: measured with UseUnifiedExtraction={manifest.UseUnifiedExtraction}, " +
                $"UseMultiSessionBatchExtraction={manifest.UseMultiSessionBatchExtraction} " +
                "(shipped default for both is false).");
            Console.WriteLine($"longmemeval: report {destination}");
            return 0;
        }
        catch (Exception exception)
        {
            // The whole chain, not just the outermost message. A stage wrapper says "LongMemEval
            // batched extraction stage failed." and nothing else, so a 26-minute run reported its
            // own death with no cause attached. Types and messages only - no stack traces.
            var chain = new List<string>();
            for (var current = exception; current is not null; current = current.InnerException)
                chain.Add($"{current.GetType().Name}: {current.Message}");
            Console.Error.WriteLine(
                $"longmemeval: prepared pair failed: {string.Join(" <- ", chain)}");
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
        // Scoped to this arm so structured and hybrid yields never mix. Until now the eight
        // instrumented vector searches emitted these spans on every run and NOTHING listened, so the
        // owner post-filter starvation figure rested on one hand measurement of one path.
        using var vectorYield = new LongMemEvalVectorYieldListener();
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
                volumeName,
                // K6. Pointed at the memory layer's own fact index: this corpus has no separate
                // knowledge graph, which is the setting GraphRAG was designed for. Retrieving the
                // same Fact nodes the Structured arm already retrieves is the whole question - does
                // a second budget over the same data add anything?
                graphRagIndexName: options.GraphRagItems > 0 ? "fact_embedding_idx" : null)
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
                // Every G3B.1-.4 correction previously reached the Raw arm only, so Structured and
                // Hybrid were being measured through the uncorrected message pipeline - an unfair
                // comparison against our own product. Hybrid was the visible casualty: 66% of its
                // message slots were formatter boilerplate and its two failing multi-session
                // questions received 0 and 2 real turns out of 15.
                ExcludeSyntheticFormatterMessages = true,
                ExpandFactsByPredicate = options.ExpandFactsByPredicate,
                ResolveQueryRelations = options.ResolveQueryRelations,
                MaxItemsPerSourceSession = options.MaxItemsPerSourceSession,
                GraphRagItems = options.GraphRagItems,
                ChronologicalAnswerContext = true,
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
            expectedInitialExtractionCalls: 0,
            diagnosticJudgeCalls: diagnostics.JudgeRetries.Count,
            // Diagnostics ran a call earlier; a verdict it already recovered must not be
            // reported as missing on the strength of AgentEval's original explanation.
            judgeRetries: diagnostics.JudgeRetries,
            agentEvalJudgeRetryAllowance: options.JudgeRetryAttempts);
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
                total.Elapsed.TotalMilliseconds),
            LongMemEvalVectorYieldSummary.From(vectorYield.Samples));
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
            // J5.1. The cost half of the comparison. Item counts alone cannot say whether an arm is
            // cheaper, and the band's recorded token figures predate predicate expansion entirely.
            meanEstimatedContextTokens = arm.Telemetry.Count == 0
                ? 0
                : arm.Telemetry.Average(item => item.EstimatedContextTokens),
            maxEstimatedContextTokens = arm.Telemetry.Count == 0
                ? 0
                : arm.Telemetry.Max(item => item.EstimatedContextTokens),
            rawMessagesRetrieved = arm.Telemetry.Sum(item => item.RawMessagesRetrieved),
            entitiesRetrieved = arm.Telemetry.Sum(item => item.EntitiesRetrieved),
            factsRetrieved = arm.Telemetry.Sum(item => item.FactsRetrieved),
            // The retrieval half of episodic memory. Capture was measured; this is the first run
            // shape that can say whether any of it comes BACK, and — read against factsRetrieved —
            // whether it arrives by crowding the semantic facts out of the same top-K.
            episodicFactsRetrieved = arm.Telemetry.Sum(item => item.EpisodicFactsRetrieved),
            questionsWithEpisodicFact = arm.Telemetry.Count(item => item.EpisodicFactsRetrieved > 0),
            preferencesRetrieved = arm.Telemetry.Sum(item => item.PreferencesRetrieved),
            // P5. The eight instrumented vector searches were emitting into the void on every run
            // because nothing in this harness subscribed. starvedSearches is the load-bearing field:
            // a search that SUCCEEDED and returned nothing is the owner post-filter shape, and a mean
            // hides it completely.
            vectorYield = arm.VectorYield,
            // K6. Zero on every run before this flag existed, because the budget was zero. Reported
            // as a pair: the count says whether the mechanism works at all, and the overlap says
            // whether what came back was already in the structured context.
            graphRagItemsRetrieved = arm.Telemetry.Sum(item => item.GraphRagItemsRetrieved),
            graphRagFactsAlreadyRetrieved =
                arm.Telemetry.Sum(item => item.GraphRagFactsAlreadyRetrieved),
            questions = arm.Telemetry,
            // The gate read per question type. "Absent" means two different things: not stored, or
            // stored but the gold answer is DERIVED from what is stored (750 = 800 - 50, and memory
            // holds the two prices, never their difference). Reported as a group so a derived-answer
            // type's absences are not read as extraction failures.
            answerPresenceByType = LongMemEvalAnswerPresence.SummariseByType(arm.Telemetry),
            // PLAN 4.2. Emitted here as well as on the plain verb, because THIS is where the
            // 50-question runs happen: the plain verb's cold build costs ~684 extraction calls and
            // 7-9 hours, so every real measurement goes through the prepared pair. An instrument that
            // reports only on the path nobody runs is the same dead-option defect as an unwired flag.
            sufficiencyAuc = LongMemEvalSufficiencyReport.From(arm.Telemetry),
            // The judge's own record, kept alongside our telemetry. AgentEval has always handed us
            // the agent's answer, the judge's explanation and a TYPED status; the report dropped all
            // three, so "do you agree with the judge?" was unanswerable and a disagreement between
            // the answer-presence gate and the judge could not be adjudicated at all. Emitted under
            // the evidence-detail setting, so a run that must not retain answer text still can't.
            judgments = evidenceDetail == LongMemEvalEvidenceDetail.None
                ? null
                : arm.Result.QuestionResults.Select(q => new
                {
                    q.QuestionId,
                    status = q.JudgeStatus?.ToString(),
                    q.Correct,
                    q.RawScore,
                    q.JudgeLlmCallCount,
                    q.JudgeTokensUsed,
                    agentResponse = q.AgentResponse,
                    judgeExplanation = q.JudgeExplanation,
                }).ToArray(),
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
                : null,
            // A rejected arm previously discarded all thirty questions' results, so one unjudgeable
            // question destroyed the evidence for the other twenty-nine. The hybrid arm has now been
            // rejected three times on the same question's judge verdict, each time taking a complete
            // run's data with it. The acceptance guard is unchanged - `accepted` is still false and
            // `result` is still null - but the measurements are kept under a name no reader can
            // mistake for an accepted result.
            unacceptedResult = arm.Validation.Accepted
                ? null
                : LongMemEvalReportProjection.CreateAcceptedResult(arm.Result, evidenceDetail)
        };

    private static async Task<T> RunPreparedWithDiagnosticsAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        LongMemEvalChatCallMeter meter,
        long expectedProviderCalls,
        TimeSpan overallTimeout,
        IServiceProvider services,
        TimeSpan noProviderProgressTimeout,
        string phase,
        TextWriter output)
    {
        try
        {
            return await LongMemEvalPreparationWatchdog.RunAsync(
                    operation,
                    meter,
                    expectedProviderCalls,
                    overallTimeout,
                    noProviderProgressTimeout,
                    phase,
                    output)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var snapshot = services.GetRequiredService<LlmExtractionBatchDiagnostics>().Snapshot();
            if (snapshot.Splits == 0)
                throw;
            var reasons = string.Join(',', snapshot.Details.GroupBy(item => item.Reason).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => $"{group.Key}={group.Count()}"));
            var sizes = string.Join(',', snapshot.Details.GroupBy(item => item.SourceSessions).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));
            var types = string.Join(',', snapshot.Details.Select(item => item.ExceptionType).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"{exception.Message} Content-free batch-split diagnostics: " +
                $"splits={snapshot.Splits}; reasons={reasons}; " +
                $"source_session_counts={sizes}; " +
                $"exception_types={types}; dropped_details={snapshot.DroppedDetails}.",
                exception);
        }
    }
    private static object Project(LongMemEvalChatCallSnapshot snapshot) => new
    {
        snapshot.Calls,
        snapshot.CompletedCalls,
        snapshot.Failures,
        snapshot.RetryCalls,
        snapshot.MaximumConcurrency,
        durationMs = snapshot.Duration.TotalMilliseconds,
        snapshot.DroppedCallDetails,
        batches = snapshot.CallDetails
            .Where(detail => string.Equals(detail.Purpose, "unified_batch", StringComparison.Ordinal))
            .Select(detail => new
            {
                detail.CallOrdinal,
                detail.DurationMilliseconds,
                detail.EstimatedInputTokens,
                detail.Retry,
                detail.ExceptionType,
                detail.ProviderStatus
            })
            .ToArray()
    };

    private static void ValidateCheckpointTelemetry(
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry,
        IReadOnlyList<LongMemEvalEvidenceQuestion> questions,
        IReadOnlyList<MultiSessionExtractionPlan> plans,
        IReadOnlyList<int> questionIndexes)
    {
        if (telemetry.Count != questionIndexes.Count)
            throw new InvalidOperationException(
                "LongMemEval checkpoint telemetry count did not match its frozen selection.");

        for (var position = 0; position < questionIndexes.Count; position++)
        {
            var questionIndex = questionIndexes[position];
            var item = telemetry[position];
            var plan = plans[questionIndex];
            if (item.QuestionNumber != questionIndex + 1 ||
                !string.Equals(item.Status, "prepared", StringComparison.Ordinal) ||
                item.MessagesStored <= 0 ||
                item.ExtractionUnits != plan.SourceSessionCount ||
                item.ExtractionCallsPlanned != plan.BatchCount ||
                item.ItemsRetrieved != 0 ||
                item.GraphReadBack is null ||
                item.GraphReadBack.TotalLearned == 0 ||
                !item.GraphReadBack.CompleteProvenance ||
                item.StageTimings is null ||
                item.StageTimings.StorageMs <= 0 ||
                item.StageTimings.ExtractionPersistenceMs <= 0 ||
                item.StageTimings.GraphReadBackMs <= 0)
            {
                throw new InvalidOperationException(
                    $"LongMemEval checkpoint question {questionIndex + 1} failed " +
                    "storage, extraction, graph, provenance, or timing guards.");
            }

            var sourceSessions = questions[questionIndex].Messages
                .Where(message =>
                    !message.IsSyntheticBoundary &&
                    !message.IsSyntheticFormatterPadding)
                .Select(message => message.SourceSessionOrdinal)
                .Distinct()
                .Count();
            if (sourceSessions != plan.SourceSessionCount)
                throw new InvalidOperationException(
                    $"LongMemEval checkpoint question {questionIndex + 1} source-session guard failed.");
        }
    }

    private static void ValidatePreparationTelemetry(
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry,
        int expectedQuestions)
    {
        if (telemetry.Count != expectedQuestions ||
            telemetry.Any(item =>
                !string.Equals(item.Status, "prepared", StringComparison.Ordinal) ||
                item.MessagesStored <= 0 ||
                item.ExtractionUnits <= 0 ||
                item.ExtractionCallsPlanned <= 0 ||
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

    /// <summary>
    /// Every option this verb accepts. Kept beside the parser so the two move together.
    /// </summary>
    /// <remarks>
    /// A name missing here is rejected at startup rather than ignored, which is the entire point: a
    /// `--mode` typo for `--memory-mode` once ran a whole measurement in the wrong configuration and
    /// produced a report that looked successful.
    /// </remarks>
    private static readonly string[] KnownOptions =
    [
        "--prepared-pair", "--assistant-content", "--checkpoint-questions",
        "--checkpoint-timeout-seconds", "--dataset", "--diagnostic-question",
        "--diagnostic-source-session", "--evidence-detail", "--expand-facts-by-predicate",
        "--graphrag-items", "--judge-retries", "--max-concurrent-batches-per-extraction",
        "--max-concurrent-extraction-batches", "--max-input-tokens", "--max-items-per-session",
        "--max-relevant", "--max-sessions-per-batch", "--no-orphan-sweep", "--oracle", "--output",
        "--preflight-only", "--preparation-workers", "--provider-no-progress-timeout-seconds",
        "--questions", "--resolve-query-relations", "--retain-prepared-volumes",
        "--reuse-prepared-volumes", "--seed", "--single-session-unified",
        "--description", "--memory-types", "--allow-stale-prepared",
        "--abstention", "--abstention-proportion",
        "--use-predicate-vocabulary",
    ];

    private static PreparedPairOptions Parse(string[] args)
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
        bool Has(string name) =>
            Array.IndexOf(args, name) >= 0;

        return new PreparedPairOptions(
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
            ParseNonNegative(Value("--judge-retries"), 2, "--judge-retries"),
            Value("--output"),
            ParseOptionalPositive(Value("--diagnostic-question"), "--diagnostic-question"),
            ParseOptionalNonNegative(
                Value("--diagnostic-source-session"), "--diagnostic-source-session"),
            ParsePositive(Value("--preparation-workers"), DefaultPreparationWorkers, "--preparation-workers"),
            ParsePositive(Value("--max-sessions-per-batch"), DefaultMaxSessionsPerBatch, "--max-sessions-per-batch"),
            ParsePositive(Value("--max-input-tokens"), DefaultMaxInputTokens, "--max-input-tokens"),
            ParsePositive(
                Value("--max-concurrent-batches-per-extraction"),
                DefaultMaxConcurrentBatchesPerExtraction,
                "--max-concurrent-batches-per-extraction"),
            ParsePositive(Value("--max-concurrent-extraction-batches"),
                DefaultMaxConcurrentExtractionBatches, "--max-concurrent-extraction-batches"),
            Has("--preflight-only"),
            Has("--retain-prepared-volumes"),
            Has("--use-predicate-vocabulary"),
            ParseAssistantContent(Value("--assistant-content")),
            // Default true reproduces every run recorded so far. Passing --single-session-unified
            // measures LlmUnifiedMemoryExtractor, the extractor an ordinary consumer gets from
            // UseUnifiedExtraction and which no measurement had ever exercised.
            !Has("--single-session-unified"),
            Has("--expand-facts-by-predicate"),
            Has("--resolve-query-relations"),
            Value("--reuse-prepared-volumes"),
            ParseNonNegative(Value("--max-items-per-session"), 0, "--max-items-per-session"),
            ParseOptionalPositive(Value("--checkpoint-questions"), "--checkpoint-questions"),
            ParsePositive(
                Value("--checkpoint-timeout-seconds"),
                DefaultCheckpointTimeoutSeconds,
                "--checkpoint-timeout-seconds"),
            ParsePositive(Value("--provider-no-progress-timeout-seconds"),
                DefaultProviderNoProgressTimeoutSeconds,
                "--provider-no-progress-timeout-seconds"),
            Has("--no-orphan-sweep"),
            ParseNonNegative(Value("--graphrag-items"), 0, "--graphrag-items"),
            Value("--description"),
            ParseMemoryTypes(Value("--memory-types")),
            Has("--allow-stale-prepared"),
            ParseAbstention(Value("--abstention")),
            ParseAbstentionProportion(Value("--abstention-proportion")));
    }

    /// <summary>
    /// Parses <c>--memory-types episodic</c> for a preparation, validating it before anything is built.
    /// </summary>
    /// <remarks>
    /// Validated here rather than at sampling time because the cost of being wrong is a 7-9 hour cold
    /// build of the wrong corpus. An unreachable type -- procedural, which this dataset does not
    /// contain at any sample size -- must stop the run at the command line.
    /// </remarks>
    private static IReadOnlyList<string> ParseMemoryTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var types = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        LongMemEvalMemoryTypeSelection.TaskTypesFor(types);
        return types;
    }

    /// <summary>
    /// Parses <c>--abstention exclude|as-sampled|only|target</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Abstention questions are the only <b>unanswerable</b> questions in this dataset, and they are
    /// what the retrieval-sufficiency AUC has to order against. Measured on a stratified 50: 32 of 33
    /// checkable questions had their answer present in memory, so the AUC rested on a single
    /// observation and the two arms landed at 0.969 and 0.094 from the same data. Without an absent
    /// class the metric is not noisy -- it is undefined.
    /// </para>
    /// <para>
    /// Default <c>as-sampled</c>, which is what every recorded run used: LongMemEval-S contains few
    /// _abs questions and stratified sampling almost never draws one, so across 52 recorded runs not
    /// one ever ran. That default is preserved exactly rather than improved, because changing what a
    /// sample contains changes every number computed from it.
    /// </para>
    /// </remarks>
    private static AbstentionSamplingPolicy ParseAbstention(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "" or "as-sampled" => AbstentionSamplingPolicy.AsSampled,
            "exclude" => AbstentionSamplingPolicy.Exclude,
            "only" => AbstentionSamplingPolicy.Only,
            "target" => AbstentionSamplingPolicy.TargetProportion,
            _ => throw new ArgumentException(
                "--abstention must be one of: as-sampled, exclude, only, target."),
        };

    /// <summary>Parses <c>--abstention-proportion</c>, the share of the sample that must abstain.</summary>
    /// <remarks>
    /// Only meaningful with <c>--abstention target</c>. Rejected outside (0,1): a proportion of 0
    /// silently means "exclude" and 1 means "only", and expressing either by accident would produce a
    /// sample nobody chose.
    /// </remarks>
    private static double? ParseAbstentionProportion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed is <= 0 or >= 1)
        {
            throw new ArgumentException(
                "--abstention-proportion must be strictly between 0 and 1; use --abstention exclude "
                + "or --abstention only for the endpoints.");
        }
        return parsed;
    }

    private static void Validate(PreparedPairOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ReusePreparedVolume) &&
            (options.IsDiagnostic || options.PreflightOnly || options.CheckpointQuestions is not null))
        {
            // Every one of these exists to exercise the preparation path, which reuse skips
            // entirely; combining them would report on work that never ran.
            throw new ArgumentException(
                "--reuse-prepared-volumes cannot be combined with diagnostic, preflight-only or " +
                "checkpoint execution: those measure preparation, and reuse performs none.");
        }

        if (options.ProviderNoProgressTimeoutSeconds > options.CheckpointTimeoutSeconds)
        {
            throw new ArgumentException(
                "--provider-no-progress-timeout-seconds cannot exceed --checkpoint-timeout-seconds.");
        }

        if (string.IsNullOrWhiteSpace(options.DatasetPath))
            throw new ArgumentException(
                "--dataset <longmemeval_s_cleaned.json> is required, or set "
                + $"{LongMemEvalDatasetLocator.PathVariable}. The file is gitignored here AND in "
                + "AgentEval and is tracked by neither, so it exists on exactly one disk; the sha256 "
                + $"of the one every recorded result used is {LongMemEvalDatasetLocator.KnownGoodSha256}.");
        if (!File.Exists(options.DatasetPath))
            throw new FileNotFoundException("LongMemEval dataset not found.", options.DatasetPath);
        if ((options.DiagnosticQuestionPosition is null) !=
            (options.DiagnosticSourceSessionOrdinal is null))
        {
            throw new ArgumentException(
                "--diagnostic-question and --diagnostic-source-session must be supplied together.");
        }
        if (options.DiagnosticQuestionPosition > options.Questions)
            throw new ArgumentException(
                "--diagnostic-question must be within the frozen selected-question count.");
        if (options.IsDiagnostic && options.OutputPath is not null)
            throw new ArgumentException(
                "--output is forbidden for diagnostic-only extraction because it cannot emit an accepted report.");
        if (options.IsDiagnostic &&
            options.EvidenceDetail == LongMemEvalEvidenceDetail.Content)
        {
            throw new ArgumentException(
                "Content evidence is forbidden for diagnostic-only extraction.");
        }
        if (options.PreflightOnly && options.IsDiagnostic)
        {
            throw new ArgumentException(
                "--preflight-only cannot be combined with diagnostic-only extraction.");
        }
        if (options.PreflightOnly && options.OutputPath is not null)
        {
            throw new ArgumentException(
                "--output is forbidden for preflight-only execution.");
        }
        if (options.CheckpointQuestions > options.Questions)
        {
            throw new ArgumentException(
                "--checkpoint-questions cannot exceed --questions.");
        }
        if (options.CheckpointQuestions is not null &&
            (options.IsDiagnostic || options.PreflightOnly))
        {
            throw new ArgumentException(
                "--checkpoint-questions cannot be combined with diagnostic or preflight-only execution.");
        }
        if (options.CheckpointQuestions is not null && options.OutputPath is not null)
        {
            throw new ArgumentException(
                "--output is forbidden for checkpoint execution.");
        }
        if (options.CheckpointQuestions is not null &&
            options.EvidenceDetail == LongMemEvalEvidenceDetail.Content)
        {
            throw new ArgumentException(
                "Content evidence is forbidden for checkpoint execution.");
        }
    }

    private static int ParsePositive(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
        return parsed;
    }

    /// <summary>
    /// Parses <c>--assistant-content ignore|utterance|fact</c>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="AssistantContentMode.Ignore"/>, which reproduces the prompts every
    /// existing sealed base was built under. An unrecognised value throws rather than silently
    /// falling back: a typo that quietly builds an 83-minute base in the wrong mode is exactly the
    /// kind of failure that is discovered only after the run.
    /// </remarks>
    private static AssistantContentMode ParseAssistantContent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AssistantContentMode.Ignore;

        if (!Enum.TryParse<AssistantContentMode>(value, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new ArgumentException(
                $"--assistant-content must be one of " +
                $"{string.Join(", ", Enum.GetNames<AssistantContentMode>()).ToLowerInvariant()}; got '{value}'.");
        }

        return parsed;
    }

    private static int ParseNonNegative(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ArgumentException($"{option} must be a non-negative integer.");
        return parsed;
    }

    private static int? ParseOptionalPositive(string? value, string option)
    {
        if (value is null) return null;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
        return parsed;
    }

    private static int? ParseOptionalNonNegative(string? value, string option)
    {
        if (value is null) return null;
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ArgumentException($"{option} must be a non-negative integer.");
        return parsed;
    }

    private static LongMemEvalEvidenceIndex? PreflightDiagnosticSelection(
        PreparedPairOptions options)
    {
        if (!options.IsDiagnostic)
            return null;

        var benchmarkOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
            options.DatasetPath,
            options.Questions,
            options.Seed,
            options.JudgeRetryAttempts,
            options.EvidenceDetail,
            options.MaxRelevantMessages);
        var evidenceIndex = LongMemEvalEvidenceIndex.Load(
            options.DatasetPath,
            benchmarkOptions);
        var questions = evidenceIndex.Questions.ToArray();
        var questionIndex = options.DiagnosticQuestionPosition!.Value - 1;
        if (questionIndex >= questions.Length)
            throw new ArgumentException(
                "The diagnostic question position does not exist in the frozen sample.");
        var sourceSessionExists = questions[questionIndex].Messages
            .Where(message =>
                !message.IsSyntheticBoundary &&
                !message.IsSyntheticFormatterPadding)
            .Select(message => message.SourceSessionOrdinal)
            .Contains(options.DiagnosticSourceSessionOrdinal!.Value);
        if (!sourceSessionExists)
            throw new ArgumentException(
                "The diagnostic source-session ordinal does not exist in the selected question.");
        return evidenceIndex;
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

    /// <summary>
    /// The identity of the run itself, which is not the identity of the preparation it measured.
    /// </summary>
    /// <remarks>
    /// A reused run must keep the sealed <c>preparationId</c> as its scope run id - the per-question
    /// scope hashes derive from it - but it must not inherit it as its own run id, because the report
    /// path is keyed on that and the reused run would overwrite the accepted report of the cold build
    /// it attached to.
    /// </remarks>
    internal static string ResolveRunId(string preparationId, bool reusing, DateTimeOffset now) =>
        reusing
            ? $"{preparationId}-reuse-{now:yyyyMMddTHHmmssZ}"
            : preparationId;

    private static string ResolveOutput(string? requested, string runId) =>
        Path.GetFullPath(requested ??
            Path.Combine(
                "artifacts",
                "evaluation",
                runId,
                "prepared-pair-report.json"));

    internal sealed record PreparedPairOptions(
        string DatasetPath,
        int Questions,
        int Seed,
        int MaxRelevantMessages,
        LongMemEvalEvidenceDetail EvidenceDetail,
        LongMemEvalOracleMode OracleMode,
        int JudgeRetryAttempts,
        string? OutputPath,
        int? DiagnosticQuestionPosition,
        int? DiagnosticSourceSessionOrdinal,
        int PreparationWorkers,
        int MaxSessionsPerBatch,
        int MaxInputTokens,
        int MaxConcurrentBatchesPerExtraction,
        int MaxConcurrentExtractionBatches,
        bool PreflightOnly,
        bool RetainPreparedVolumes,
        bool UsePredicateVocabulary,
        AssistantContentMode AssistantContent,
        bool MultiSessionBatch,
        bool ExpandFactsByPredicate,
        bool ResolveQueryRelations,
        string? ReusePreparedVolume,
        int MaxItemsPerSourceSession,
        int? CheckpointQuestions,
        int CheckpointTimeoutSeconds,
        int ProviderNoProgressTimeoutSeconds,
        bool NoOrphanSweep,
        int GraphRagItems,
        // A frozen corpus is reused for months; these three are what make it findable and safe to
        // reuse later, when nobody remembers what it was built for.
        string? Description = null,
        IReadOnlyList<string>? MemoryTypesRequested = null,
        bool AllowStalePrepared = false,
        AbstentionSamplingPolicy AbstentionPolicy = AbstentionSamplingPolicy.AsSampled,
        double? AbstentionProportion = null)
    {
        /// <summary>The memory types this corpus was sampled for; empty means every type.</summary>
        internal IReadOnlyList<string> MemoryTypes => MemoryTypesRequested ?? [];

        internal bool IsDiagnostic =>
            DiagnosticQuestionPosition is not null &&
            DiagnosticSourceSessionOrdinal is not null;
    }

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
        PreparedArmTimings Timings,
        LongMemEvalVectorYieldSummary VectorYield);
}
