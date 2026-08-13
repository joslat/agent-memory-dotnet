using Azure;
using Azure.AI.OpenAI;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Runs the procedural-benefit measurement: the same multi-step task, repeated, with procedural
/// memory on and off (7.6).
/// </summary>
/// <remarks>
/// <para>
/// Everything this composes has its own provider-free tests — the benefit scoring, the step and
/// tool-call counting, the enforced-chain task, and the promotion decision. This is the assembly, and
/// assembly is where a measurement quietly stops measuring: an arm switch that does not switch, or a
/// procedure store both arms share, produces a confident "no benefit" that reads as a finding.
/// </para>
/// <para>
/// <b>The arms differ in exactly two things and nothing else.</b> The procedural arm is given a
/// <see cref="Neo4jMemoryContextProvider"/> configured to recall reasoning traces and nothing else, and
/// it promotes successful attempts; the control has neither. Same model, same tools, <b>same
/// instructions verbatim</b>, same task, same attempt count. Anything else that differed would be
/// attributed to memory by a harness that cannot see it.
/// </para>
/// <para>
/// That claim was false for five runs and this comment is the reason it went unnoticed: it asserted
/// trace recall while <c>BuildAgent</c> attached no <c>AIContextProvider</c> at all, so the procedural
/// arm stored procedures and had no way to read one back. It is restated here only because the wiring
/// below now implements it — <c>ProceduralRecallOnly</c> for the read side, the trace repository for the
/// write side, one arm switch feeding both.
/// </para>
/// </remarks>
internal static class ProceduralBenefitProgram
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var attempts = ParseAttempts(args);
        var log = Console.Out;

        var endpoint = Required("AZURE_OPENAI_ENDPOINT");
        var apiKey = Required("AZURE_OPENAI_API_KEY");
        var deployment = Required("AZURE_OPENAI_DEPLOYMENT");
        var embeddingDeployment = Required("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");

        var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        var chatClient = azure.GetChatClient(deployment).AsIChatClient();
        var embeddings = azure.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator();
        var dimensions = await LongMemEvalRuntime
            .ProbeEmbeddingDimensionsAsync(embeddings).ConfigureAwait(false);

        await using var profile = await LongMemEvalMemoryProfile.StartAsync(
            embeddings,
            // The profile demands an extraction client for Structured mode. This benchmark never
            // extracts -- it stores and recalls procedures -- so the same chat client is handed over
            // rather than a stub, which would fail loudly the moment anything did try to extract.
            extractionChatClient: chatClient,
            LongMemEvalMemoryMode.Structured,
            extractionModelId: deployment,
            dimensions,
            log,
            cancellationToken).ConfigureAwait(false);

        // Prompt and completion only -- both are pure, and this instance is never given to an agent.
        var template = new ProceduralBenchmarkTask();
        var traces = profile.Services.GetRequiredService<IReasoningTraceRepository>();

        // One witness per procedural attempt, in attempt order. The arm is not trusted to be wired:
        // three separate wiring faults have produced an identical "no benefit" verdict, so what reached
        // the model is observed rather than assumed. See ProceduralRecallWitness.
        var witnesses = new List<ProceduralRecallWitness>();

        // The arm switch, and the only difference between the two agents. The control arm is handed
        // no trace repository, so it neither reads nor writes procedures.
        var runner = new MafAgentTaskRunner(
            // A FRESH task per attempt. Sharing one leaked the stale-session flag from the procedural
            // arm into the control, which completed a five-call chain in four calls -- a void run whose
            // only tell was that the arithmetic was impossible. The environment must start stale every
            // time or the control arm inherits the discovery instead of paying for it.
            proceduralMemoryEnabled =>
                BuildAgent(
                    chatClient, new ProceduralBenchmarkTask(), proceduralMemoryEnabled,
                    profile.Services, witnesses),
            template.Prompt,
            template.IsComplete,
            traces,
            profile.Services.GetRequiredService<IEmbeddingOrchestrator>(),
            // So the promoted procedure is the chain that WORKED, not the transcript of finding it. The
            // seventh run stored "PlaceHold then RefreshSession then PlaceHold" and the arm replaying it
            // paid for the refused call all over again.
            ProceduralBenchmarkTask.IsRefusal);

        log.WriteLine($"procedural-benefit: {attempts} attempts per arm, task='{template.Prompt}'");
        var result = await ProceduralBenefitResult
            .MeasureAsync(runner, "procedural-benchmark", attempts, cancellationToken)
            .ConfigureAwait(false);

        Report(log, result);
        return ReportRecallWitness(log, witnesses) ? 0 : 1;
    }

    /// <summary>
    /// Reports what the procedural arm actually read, and whether the run is interpretable at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A run where the arm never read a procedure is void, not negative.</b> Attempt one is expected
    /// to read nothing — the store is empty until it promotes — so the invariant is on the attempts
    /// after it: at least one of them must have had a procedure admitted into its context. If none did,
    /// the two arms were the same agent and the efficiency figures describe noise between two identical
    /// configurations, which is exactly the reading that has been published six times here.
    /// </para>
    /// <para>
    /// Returns false in that case so the process exits non-zero. A void run must be inconvenient to
    /// mistake for a result.
    /// </para>
    /// </remarks>
    private static bool ReportRecallWitness(TextWriter log, List<ProceduralRecallWitness> witnesses)
    {
        var counts = witnesses.Select(w => w.AdmittedProcedureCount).ToList();
        log.WriteLine(
            $"  proceduresInContextPerAttempt=[{string.Join(", ", counts)}] "
            + $"(attempt 1 reads nothing by construction)");

        var lastAdmitted = witnesses.LastOrDefault(w => w.AdmittedProcedureCount > 0)?
            .AdmittedProcedures[^1];
        if (lastAdmitted is not null)
            log.WriteLine($"  lastProcedureRead=\"{lastAdmitted}\"");

        if (counts.Skip(1).Any(count => count > 0)) return true;

        log.WriteLine(
            "  VOID: no procedure was ever admitted into the procedural arm's context, so both arms ran "
            + "as the same agent. The figures above describe noise between two identical configurations, "
            + "NOT the feature. Fix the read path before reporting anything.");
        return false;
    }

    /// <summary>
    /// Builds the agent for one arm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The benchmark tools are identical in both arms, and so are the instructions. Only whether the
    /// agent can recall a stored procedure differs — which is what makes any measured gap attributable
    /// to memory rather than to a differently-equipped agent.
    /// </para>
    /// <para>
    /// <b>The instructions are byte-identical, including the sentence about procedures.</b> Earlier the
    /// procedural arm alone was told "if you recall a procedure for this task, follow it", which is a
    /// third difference between the arms and one that acts directly on the number being measured: a
    /// model told to expect a shortcut behaves differently from one that is not, recall or no recall.
    /// The sentence is inert for the control — nothing ever puts a procedure in its context — so
    /// giving it to both costs nothing and removes the confound.
    /// </para>
    /// </remarks>
    private static AIAgent BuildAgent(
        IChatClient chatClient,
        ProceduralBenchmarkTask task,
        bool proceduralMemoryEnabled,
        IServiceProvider services,
        List<ProceduralRecallWitness> witnesses)
    {
        const string instructions =
            "You complete booking tasks using the supplied tools. If you recall a procedure for this "
            + "task, follow it. Reply with the confirmation reference exactly as the tool returns it.";

        AIContextProvider? recall = null;
        if (proceduralMemoryEnabled)
        {
            // One witness per attempt, appended in attempt order, because "did the arm read a procedure"
            // is a per-attempt property: attempt one must read nothing and the later ones must read
            // something, and a single shared counter cannot tell those two failures apart.
            var witness = new ProceduralRecallWitness();
            witnesses.Add(witness);
            recall = BuildProceduralRecall(services, witness);
        }

        return chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = proceduralMemoryEnabled ? "WithProcedures" : "Control",
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = [.. task.CreateTools()],
            },
            AIContextProviders = recall is null ? null : [recall],
        });
    }

    /// <summary>
    /// The procedural arm's read side: the shipped MAF context provider, configured to recall promoted
    /// procedures and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constructed by hand rather than resolved, because the LongMemEval profile registers the memory
    /// core and Neo4j but not the MAF adapter — and because every option below has to be chosen for this
    /// measurement rather than inherited from whatever the profile happens to configure.
    /// </para>
    /// <para>
    /// <b>Traces only.</b> Every other recall category is zeroed. An arm that also recalled messages,
    /// entities and facts would differ from the control in <i>memory</i>, not in <i>procedural</i>
    /// memory, and any gap it produced would be unattributable — the agent could be arriving faster
    /// because it remembered the traveller's tier as a fact, which is a different feature.
    /// </para>
    /// <para>
    /// <b>Extraction off.</b> On by default, and it would spend a model call per turn deriving
    /// entities/facts that nothing here recalls — cost and nondeterminism for no signal.
    /// </para>
    /// <para>
    /// <b>Memory tools off</b> (the shipped default, restated because it matters here): those tools are
    /// tool calls, and tool calls are the measurement. An arm that could call <c>search_memory</c> would
    /// score worse on the metric while using memory more.
    /// </para>
    /// </remarks>
    private static Neo4jMemoryContextProvider BuildProceduralRecall(
        IServiceProvider services, ProceduralRecallWitness witness) =>
        new(
            services.GetRequiredService<IMemoryService>(),
            services.GetRequiredService<IEmbeddingOrchestrator>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IIdGenerator>(),
            Options.Create(new MemoryOptions { Recall = ProceduralRecallOnly }),
            Options.Create(new ContextFormatOptions
            {
                IncludeReasoningTraces = true,
                // Without this the block renders the trace's TASK and drops its OUTCOME -- i.e. it
                // tells the agent it has done this before and not what it did. The chain is the
                // procedure; this is what makes the arm's memory legible to the model at all.
                IncludeTraceOutcomes = true,
                IncludeEntities = false,
                IncludeFacts = false,
                IncludePreferences = false,
                MaxChatHistoryMessages = 0,
                // The shipped prefix frames recalled memory as untrusted data and tells the model never
                // to follow instructions found inside it. For a promoted procedure that is a direct
                // contradiction -- the block IS a suggested ordering, and the arm's own instructions ask
                // the agent to follow it. The untrusted framing is kept verbatim and one sentence added,
                // scoped to procedures, rather than dropping a #92 defence to make a number move.
                ContextPrefix = new ContextFormatOptions().ContextPrefix
                    + " One exception, and only this one: a \"Similar past tasks\" entry records the tool "
                    + "ordering that previously completed this same task, and you may reuse that ordering.",
            }),
            Options.Create(new AgentFrameworkOptions
            {
                AutoExtractOnPersist = false,
                ExposeMemoryToolsFromContextProvider = false,
            }),
            services.GetRequiredService<ILogger<Neo4jMemoryContextProvider>>(),
            // The witness rides the admission-policy seam: it decides nothing, delegating every verdict
            // to the default policy, and records which trace blocks were admitted into this attempt's
            // context. Passing it here (rather than counting store writes or trusting the options) is
            // what makes "the arm read a procedure" an observation instead of an assumption.
            admissionPolicy: witness);

    /// <summary>
    /// Recall confined to promoted procedures: <c>MaxTraces</c> &gt; 0, every other budget zero.
    /// </summary>
    /// <remarks>
    /// <c>SuccessfulTracesOnly</c> is redundant with promotion (only completed attempts are ever
    /// promoted) and set anyway, because "the arm recalls a failed procedure" is a failure mode worth
    /// closing in the configuration rather than relying on the writer to keep the store clean.
    /// </remarks>
    private static readonly RecallOptions ProceduralRecallOnly = new()
    {
        MaxRecentMessages = 0,
        MaxRelevantMessages = 0,
        MaxEntities = 0,
        MaxFacts = 0,
        MaxPreferences = 0,
        MaxGraphRagItems = 0,
        MaxTraces = 3,
        SuccessfulTracesOnly = true,
    };

    private static void Report(TextWriter log, ProceduralBenefitResult result)
    {
        void Arm(string name, ProceduralBenefitArm arm) =>
            log.WriteLine(
                $"  {name,-10} completion={arm.CompletionRate:P0} "
                + $"meanSteps={arm.MeanStepsWhenCompleted:F1} "
                + $"meanToolCalls={arm.MeanToolCallsWhenCompleted:F1}");

        log.WriteLine("procedural-benefit results:");
        Arm("procedures", result.WithProcedures);
        Arm("control", result.WithoutProcedures);
        log.WriteLine(
            $"  stepReduction={result.StepReduction:P1} toolCallReduction={result.ToolCallReduction:P1} "
            + $"completionDelta={result.CompletionRateDelta:P0}");
        // Decomposed on purpose: the composite gate accepts learning shown in EITHER measure, so a reader
        // has to be able to see which one moved rather than trusting the summary flag.
        log.WriteLine(
            $"  improvedWithRepetition={result.ImprovedWithRepetition} "
            + $"(steps={result.ImprovedStepsWithRepetition}, toolCalls={result.ImprovedToolCallsWithRepetition})");
        log.WriteLine(
            $"  noiseBand(control spread): steps={result.StepNoiseBand:F2} toolCalls={result.ToolCallNoiseBand:F2} "
            + $"=> exceeded: steps={result.StepGainExceedsNoise}, toolCalls={result.ToolCallGainExceedsNoise}");
        log.WriteLine(
            "  perAttempt steps/toolCalls: procedures="
            + Trace(result.WithProcedures) + " control=" + Trace(result.WithoutProcedures));

        static string Trace(ProceduralBenefitArm arm) =>
            "[" + string.Join(", ", arm.Runs.Select(r =>
                $"{r.Steps}/{r.ToolCalls}{(r.Completed ? string.Empty : "!")}")) + "]";
        // The verdict is completion-gated: an arm that finishes less often shows no benefit however
        // few steps it took, because the steps it saved were not spent finishing.
        log.WriteLine($"  SHOWS BENEFIT: {result.ShowsBenefit}");
    }

    private static int ParseAttempts(string[] args)
    {
        var index = Array.IndexOf(args, "--attempts");
        return index >= 0 && index + 1 < args.Length
               && int.TryParse(args[index + 1], out var value) && value >= 2
            ? value
            : 3;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");
}
