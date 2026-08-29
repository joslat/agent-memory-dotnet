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
        // 30.9c: the Wave-C ablation switches. Absent, every run takes the shipped-default path,
        // which is what every sealed measurement so far was taken under.
        "--working-memory", "--arithmetic-memory", "--rescue-short-owner-results",
        "--fact-weighted-budget",
        // Escalation instrument. The verb hard-coded Identifiers, so AnswerContext[].Content came
        // back null on every item and "was the needed VALUE rendered" could not be asked of any
        // artifact -- only "was its SESSION covered", which is a different and weaker question. The
        // Bitemporal misses turned on exactly that distinction.
        "--evidence-detail",
        // 30.9c ON arm for Bitemporal — see PREREG-BITEMPORAL-ON-2026-08-23.md.
        "--supersede-replaced-facts",
        // 30.9d. Renders the chains --supersede-replaced-facts writes. Only informative together.
        "--resolve-supersessions",
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

            // Listens for the repositories' own vector-yield telemetry so the run can say, from its own
            // artifact, whether owner starvation happened during it. Null on the oracle arm because
            // that arm issues no vector search at all -- and null there means NOT MEASURED, which is a
            // different statement from a zeroed block claiming no starvation was seen.
            using LongMemEvalVectorYieldListener? vectorYield =
                options.Oracle ? null : new LongMemEvalVectorYieldListener();
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
                            CancellationToken.None,
                            phase30: options.Phase30,
                            rescueShortOwnerResults: options.RescueShortOwnerResults,
                            supersedeReplacedFacts: options.SupersedeReplacedFacts,
                            resolveSupersessions: options.ResolveSupersessions)
                        .ConfigureAwait(false);
                }

                var assembledResults = new List<ExternalBenchmarkResult>();
                foreach (var vertical in options.Verticals)
                {
                    assembledResults.AddRange(await RunVerticalAsync(
                            vertical, options, answerChatClient, judgeChatClient, deployment, profile,
                            vectorYield)
                        .ConfigureAwait(false));
                }

                // The double-count guard runs over everything this invocation assembled: the
                // twelve tg probe questions carried into Prospective make any cross-corpus total
                // a double count, and the detector is upstream's, not a local id comparison.
                WarnOnSeedOverlap(assembledResults, Console.Out);
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

    private static async Task<IReadOnlyList<ExternalBenchmarkResult>> RunVerticalAsync(
        TypedMemEvalVertical vertical,
        TypedMemEvalRunOptions options,
        IChatClient answerChatClient,
        IChatClient judgeChatClient,
        string deployment,
        LongMemEvalMemoryProfile? profile,
        LongMemEvalVectorYieldListener? vectorYield)
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

            // Per-run, unlike the vector-yield listener above: this summary is written into ONE
            // artifact's sidecar and must describe that run alone. Null on the oracle arm, which
            // assembles no memory context to project -- "not measured", not "measured zero".
            LongMemEvalSupersessionRenderProbe? renderProbe = null;
            if (!options.Oracle) renderProbe = new LongMemEvalSupersessionRenderProbe();

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
                        FactWeightedBudget = options.FactWeightedBudget,
                        MemoryMode = LongMemEvalMemoryMode.Structured,
                        MinSimilarityScore = 0,
                        ModelId = deployment,
                        EvidenceIndex = LongMemEvalEvidenceIndex.CreateTypedMemEval(vertical, facade),
                        EvidenceDetail = options.EvidenceDetail,
                        SupersessionRenderProbe = renderProbe,
                        RequireGraphReadBack = true,
                        GraphProbe = new Neo4jLongMemEvalGraphProbe(
                            profile.Services.GetRequiredService<global::Neo4j.Driver.IDriver>()),
                        ExtractionProgress = (completed, total) => Console.WriteLine(
                            $"typedmemeval: extraction units {completed}/{total}.")
                    });
                result = await runner.RunAsync(adapter, vertical, facade).ConfigureAwait(false);
            }

            // Read BEFORE Persist and before teardown: the container is reaped when the run ends, so
            // a store fact not captured here can never be recovered. This is the store-state gate as
            // a COUNT rather than the facts-per-question inference it replaces -- that inference
            // certified an off-state arm as verified-ON, which is the failure the gate existed to
            // prevent occurring inside the gate itself.
            LongMemEvalSupersessionStore? supersessionStore = null;
            if (profile is not null && !options.Oracle)
            {
                try
                {
                    supersessionStore = await new Neo4jLongMemEvalGraphProbe(
                            profile.Services.GetRequiredService<global::Neo4j.Driver.IDriver>())
                        .ReadSupersessionStoreAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Diagnostic only: it must never cost a completed run. Null reads as NOT
                    // MEASURED, which is the honest value and is distinct from a measured zero.
                    Console.WriteLine($"typedmemeval: supersession store probe failed — {ex.Message}");
                }
            }

            var renderSummary = renderProbe is null
                ? null
                : LongMemEvalSupersessionRenderSummary.From(renderProbe.Samples);
            var destination = Persist(
                result, descriptor, options, runIndex, startedUtc,
                vectorYield is null ? null : LongMemEvalVectorYieldSummary.From(vectorYield.Samples),
                renderSummary, supersessionStore);
            PrintSupersessionStore(supersessionStore);
            PrintRenderState(options, renderSummary);
            PrintRun(result, destination);
            results.Add(result);
        }

        if (results.Count > 1)
            PrintBand(TypedMemEvalRunSet.Summarize(results));

        return results;
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
        DateTimeOffset startedUtc,
        LongMemEvalVectorYieldSummary? vectorYield,
        LongMemEvalSupersessionRenderSummary? renderSummary,
        LongMemEvalSupersessionStore? supersessionStore)
    {
        // The arm is stamped into the FILENAME, not into the report body. The serialized type is
        // AgentEval's ExternalBenchmarkResult and its Options is their fixed record with no extension
        // point, so adding a field is not available; rewrapping the JSON would break every existing
        // reader of these artifacts. The filename and a sidecar are the two places that can carry it
        // without touching a byte of the shape anyone already parses.
        var arm = options.Arm;
        var name =
            $"typedmemeval-{descriptor.Slug}" +
            (options.Control ? "-control" : string.Empty) +
            (options.Oracle ? "-oracle" : string.Empty) +
            $"-seed{options.RandomSeed?.ToString(CultureInfo.InvariantCulture) ?? "unseeded"}" +
            $"-arm{arm.FileToken()}" +
            $"-run{runIndex}" +
            $"-{startedUtc:yyyyMMddTHHmmssZ}.json";
        var destination = Path.GetFullPath(Path.Combine("artifacts", "evaluation", name));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(
            destination,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);

        WriteProvenance(
            destination, arm, descriptor, options, runIndex, startedUtc, vectorYield, renderSummary,
            supersessionStore);
        return destination;
    }

    /// <summary>
    /// Writes the arm's provenance beside the report, as <c>&lt;report&gt;.provenance.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filename names the arm; this says what the arm MEANT, plus the commit it ran on. A token
    /// like <c>armfactwt</c> is only useful while someone remembers what that lever did, and the
    /// commit is what makes a six-month-old number re-derivable at all.
    /// </para>
    /// <para>
    /// <b>No environment is captured, deliberately.</b> This harness reads an endpoint, an API key and
    /// deployment names out of the environment, and a provenance file that swept those up would put
    /// credentials in a directory whose whole purpose is being shared and committed. Only the parsed
    /// options are recorded, which cannot contain a secret because no secret is ever a CLI argument.
    /// </para>
    /// </remarks>
    private static void WriteProvenance(
        string reportPath,
        TypedMemEvalArm arm,
        TypedMemEvalVerticalDescriptor descriptor,
        TypedMemEvalRunOptions options,
        int runIndex,
        DateTimeOffset startedUtc,
        LongMemEvalVectorYieldSummary? vectorYield,
        LongMemEvalSupersessionRenderSummary? renderSummary,
        LongMemEvalSupersessionStore? supersessionStore)
    {
        // Built before the object rather than inline: an anonymous type inside a conditional has no
        // natural type to infer, so `condition ? null : new { ... }` does not compile. Hoisting it
        // also keeps the null case explicit, which is the meaning that matters here.
        object? yieldBlock = null;
        if (vectorYield is not null)
        {
            yieldBlock = new
            {
                searches = vectorYield.Searches,
                ownerScopedSearches = vectorYield.OwnerScopedSearches,
                starvedSearches = vectorYield.StarvedSearches,
                escalatedSearches = vectorYield.EscalatedSearches,
                meanReturned = vectorYield.MeanReturned,
                meanFillRatio = vectorYield.MeanFillRatio,
                meanYieldRatio = vectorYield.MeanYieldRatio,
                totalReturned = vectorYield.TotalReturned,
            };
        }

        object? renderBlock = null;
        if (renderSummary is not null)
        {
            renderBlock = new
            {
                promptsInspected = renderSummary.PromptsInspected,
                promptsWithAnnotation = renderSummary.PromptsWithAnnotation,
                promptsWithNoteInPrompt = renderSummary.PromptsWithNoteInPrompt,
                notesAnnotated = renderSummary.NotesAnnotated,
                notesInPrompt = renderSummary.NotesInPrompt,
                sample = renderSummary.Sample,
                renderConfirmed = renderSummary.RenderConfirmed,
            };
        }

        object? storeBlock = null;
        if (supersessionStore is not null)
        {
            storeBlock = new
            {
                supersededByEdges = supersessionStore.SupersededByEdges,
                facts = supersessionStore.Facts,
                invalidatedFacts = supersessionStore.InvalidatedFacts,
                topPredicates = supersessionStore.TopPredicates
                    .Select(p => new { predicate = p.Predicate, count = p.Count }).ToArray(),
            };
        }

        var provenance = new
        {
            schema = "typedmemeval-provenance/1",
            report = Path.GetFileName(reportPath),
            vertical = descriptor.Slug,
            startedUtc = startedUtc.ToString("O", CultureInfo.InvariantCulture),
            run = runIndex,
            arm = new
            {
                token = arm.FileToken(),
                describe = arm.Describe(),
                isDefault = arm.IsDefault,
                workingMemory = arm.Phase30.WorkingMemory,
                arithmeticMemory = arm.Phase30.ArithmeticMemory,
                rescueShortOwnerResults = arm.RescueShortOwnerResults,
                supersedeReplacedFacts = arm.SupersedeReplacedFacts,
                resolveSupersessions = arm.ResolveSupersessions,
                factWeightedBudget = arm.FactWeightedBudget,
                schemaExtensions = arm.Phase30.Extensions,
            },
            sampling = new
            {
                maxQuestions = options.MaxQuestions,
                randomSeed = options.RandomSeed,
                answerSeed = options.AnswerSeed,
                runs = options.Runs,
                oracle = options.Oracle,
                control = options.Control,
            },
            commit = ReadGitSha(),
            // Recorded gap, closed here rather than in the report: a run could not confirm from its
            // own artifact whether owner starvation occurred during it, which is what forced the
            // LongMemEval runs to stand in as evidence for a TypedMemEval claim. It lives in the
            // sidecar for the same reason the arm token does -- AgentEval's result type is fixed and
            // rewrapping the JSON would break every existing reader.
            //
            // NULL means NOT MEASURED, and that distinction is the point: a zeroed block would read
            // as "no starvation observed", which is a claim this listener never made.
            vectorYield = yieldBlock,
            // 30.9d render-state gate. `renderConfirmed` is the ONLY affirmative form, and it needs a
            // retained sample rather than a nonzero counter so the claim can be eyeballed. A null
            // block means the probe never ran, which the prereg reads as a FAILURE, not a pass:
            // scores from an unconfirmed arm may not be read, because a dark renderer is exactly the
            // defect this arm exists to rule out.
            supersessionRender = renderBlock,
            // The store-state gate, as a count. `supersededByEdges: 0` means write-time supersession
            // never ran and the arm measured an off-state whatever its flags say; `topPredicates`
            // explains why, since supersession is refused for any predicate outside the six the
            // relation vocabulary declares single-valued.
            supersessionStore = storeBlock,
        };

        File.WriteAllText(
            Path.ChangeExtension(reportPath, null) + ".provenance.json",
            JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);
    }

    /// <summary>The commit this ran on, or null when it cannot be determined.</summary>
    /// <remarks>
    /// Null rather than a guess. "unknown" written into a provenance field reads as a value and would
    /// let a run claim a commit it never had; a missing field reads as missing.
    /// </remarks>
    private static string? ReadGitSha()
    {
        try
        {
            var head = Path.Combine(".git", "HEAD");
            if (!File.Exists(head)) return null;

            var contents = File.ReadAllText(head).Trim();
            if (!contents.StartsWith("ref:", StringComparison.Ordinal)) return contents;

            var refPath = Path.Combine(".git", contents[4..].Trim().Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Announces the render-state gate at the console, loudly when it fails.
    /// </summary>
    /// <remarks>
    /// Printed only for the arm that asked for rendering. Elsewhere a zero is the correct and
    /// expected state, and a warning there would train the reader to ignore this line -- which is how
    /// a gate stops being read at all.
    /// </remarks>
    /// <summary>Announces what supersession actually wrote, loudly when it wrote nothing.</summary>
    private static void PrintSupersessionStore(LongMemEvalSupersessionStore? store)
    {
        if (store is null) return;

        Console.WriteLine(
            $"typedmemeval: supersession store — {store.SupersededByEdges} :SUPERSEDED_BY edge(s), " +
            $"{store.InvalidatedFacts}/{store.Facts} facts invalidated.");

        if (store.SupersededByEdges > 0) return;

        Console.WriteLine(
            "typedmemeval: WARNING supersession wrote NOTHING — this arm measured an OFF-STATE " +
            "regardless of its flags. Write-time supersession is refused for any predicate outside " +
            "the six the relation vocabulary declares single-valued. Top predicates extracted: " +
            string.Join(", ", store.TopPredicates.Take(8).Select(p => $"{p.Predicate}×{p.Count}")));
    }

    private static void PrintRenderState(
        TypedMemEvalRunOptions options, LongMemEvalSupersessionRenderSummary? renderSummary)
    {
        if (!options.ResolveSupersessions) return;

        if (renderSummary is null)
        {
            Console.WriteLine(
                "typedmemeval: WARNING render-state NOT MEASURED (--resolve-supersessions was set). " +
                "Treat this run's scores as unreadable.");
            return;
        }

        Console.WriteLine(
            $"typedmemeval: supersession render — prompts {renderSummary.PromptsWithNoteInPrompt}" +
            $"/{renderSummary.PromptsInspected} carried a chain, notes {renderSummary.NotesInPrompt}" +
            $"/{renderSummary.NotesAnnotated} reached the prompt.");

        if (renderSummary.RenderConfirmed)
        {
            Console.WriteLine($"typedmemeval: render CONFIRMED — sample {renderSummary.Sample}");
            return;
        }

        // The two failures say different things and must not be collapsed: nothing annotated means
        // the feature or its edges are dark, while annotated-but-absent means the harness's own
        // prompt builder dropped the note. Only the second is a render-surface bug.
        Console.WriteLine(renderSummary.NotesAnnotated == 0
            ? "typedmemeval: WARNING render NOT confirmed — projection annotated NOTHING. Either the " +
              "renderer is off or no :SUPERSEDED_BY edges exist. Do not read this run's scores."
            : "typedmemeval: WARNING render NOT confirmed — projection annotated " +
              $"{renderSummary.NotesAnnotated} note(s) and NONE reached the prompt. Render-surface bug.");
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
            Array.IndexOf(args, "--control") >= 0,
            new PhaseThirtyFeatures(
                WorkingMemory: Array.IndexOf(args, "--working-memory") >= 0,
                ArithmeticMemory: Array.IndexOf(args, "--arithmetic-memory") >= 0),
            Array.IndexOf(args, "--rescue-short-owner-results") >= 0,
            Array.IndexOf(args, "--supersede-replaced-facts") >= 0,
            ParseEvidenceDetail(Value("--evidence-detail")),
            Array.IndexOf(args, "--fact-weighted-budget") >= 0,
            Array.IndexOf(args, "--resolve-supersessions") >= 0);

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

    /// <summary>Parses <c>--evidence-detail</c>, defaulting to the value this verb used to hard-code.</summary>
    /// <remarks>
    /// <c>identifiers</c> stays the default so every sealed measurement remains comparable: it is what
    /// the verb did before the flag existed. <c>content</c> is the escalation instrument -- it puts
    /// bounded evidence text in <c>AnswerContext[].Content</c>, which turns "was the needed VALUE
    /// rendered" from an inference into a measurement. It costs artifact size, so it is opt-in.
    /// </remarks>
    private static LongMemEvalEvidenceDetail ParseEvidenceDetail(string? value) => value switch
    {
        null or "" => LongMemEvalEvidenceDetail.Identifiers,
        "none" => LongMemEvalEvidenceDetail.None,
        "identifiers" => LongMemEvalEvidenceDetail.Identifiers,
        "content" => LongMemEvalEvidenceDetail.Content,
        _ => throw new ArgumentException(
            "--evidence-detail must be one of: none, identifiers, content."),
    };

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

    /// <summary>
    /// AgentEval's double-count guard, applied at the one place this verb assembles results across
    /// verticals: the twelve time-grounded probe questions were carried into
    /// TypedMemEval-Prospective, so a result set containing both corpora double-counts them in any
    /// total. <see cref="TypedMemEvalRunSet.DetectSeedOverlap"/> is the upstream authority on the
    /// corpus identifiers; this method only decides where its warning surfaces.
    /// </summary>
    /// <returns>True when the warning was printed — the tests' observable.</returns>
    internal static bool WarnOnSeedOverlap(
        IEnumerable<ExternalBenchmarkResult> results, TextWriter output)
    {
        if (TypedMemEvalRunSet.DetectSeedOverlap(results) is { } warning)
        {
            output.WriteLine($"typedmemeval: WARNING — {warning}");
            return true;
        }

        return false;
    }

    internal sealed record TypedMemEvalRunOptions(
        IReadOnlyList<TypedMemEvalVertical> Verticals,
        int? MaxQuestions,
        int? RandomSeed,
        int? AnswerSeed,
        int Runs,
        bool Oracle,
        bool Control,
        PhaseThirtyFeatures Phase30,
        // 30.9c re-measure instrument. Owner starvation reaches this eval because the harness assigns
        // one owner per question into a shared index (ScopeId("owner", _questionNumber)); stored runs
        // show owner-scoped recall returning ~48% of what it asks for. This flag is the mitigation
        // arm. It stays OFF by default here for the same reason it ships off: measurements taken
        // without it must remain comparable.
        bool RescueShortOwnerResults,
        bool SupersedeReplacedFacts,
        LongMemEvalEvidenceDetail EvidenceDetail,
        // Arm A finding (2026-08-21): the structured budget splits three ways, so an arithmetic
        // question spends two thirds of its context on entities and preferences -- kinds that cannot
        // carry a value. This reallocates the SAME total toward facts.
        bool FactWeightedBudget,
        // 30.9d. The renderer (`SupersessionProjectionFeature`) was built, gated on
        // MemoryProjectionOptions.ResolveSupersessions = false, and never set by any harness -- so
        // every scored run rendered supersession chains DARK. Appended last for the positional-safety
        // reason documented on TypedMemEvalArm.ResolveSupersessions.
        bool ResolveSupersessions)
    {
        /// <summary>
        /// Every lever this run had on, composed into one identity for the filename and the sidecar.
        /// </summary>
        /// <remarks>
        /// Derived rather than stored, so it cannot drift from the flags it describes: an arm token
        /// that disagreed with the options that produced it would be worse than no token at all.
        /// </remarks>
        internal TypedMemEvalArm Arm =>
            new(Phase30, RescueShortOwnerResults, SupersedeReplacedFacts, FactWeightedBudget,
                ResolveSupersessions);
    }
}
