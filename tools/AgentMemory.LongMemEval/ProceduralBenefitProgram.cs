using Azure;
using Azure.AI.OpenAI;
using AgentMemory.Abstractions.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

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
/// <b>The arms differ in exactly two things and nothing else.</b> The procedural arm recalls traces
/// (<c>MaxTraces</c> &gt; 0) and promotes successful attempts; the control does neither. Same model,
/// same tools, same prompt, same task, same attempt count. Anything else that differed would be
/// attributed to memory by a harness that cannot see it.
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

        var task = new ProceduralBenchmarkTask();
        var traces = profile.Services.GetRequiredService<IReasoningTraceRepository>();

        // The arm switch, and the only difference between the two agents. The control arm is handed
        // no trace repository, so it neither reads nor writes procedures.
        var runner = new MafAgentTaskRunner(
            proceduralMemoryEnabled => BuildAgent(chatClient, task, proceduralMemoryEnabled),
            task.Prompt,
            task.IsComplete,
            traces);

        log.WriteLine($"procedural-benefit: {attempts} attempts per arm, task='{task.Prompt}'");
        var result = await ProceduralBenefitResult
            .MeasureAsync(runner, "procedural-benchmark", attempts, cancellationToken)
            .ConfigureAwait(false);

        Report(log, result);
        return 0;
    }

    /// <summary>
    /// Builds the agent for one arm.
    /// </summary>
    /// <remarks>
    /// The benchmark tools are identical in both arms. Only whether the agent can recall a stored
    /// procedure differs — which is what makes any measured gap attributable to memory rather than to
    /// a differently-equipped agent.
    /// </remarks>
    private static AIAgent BuildAgent(
        IChatClient chatClient, ProceduralBenchmarkTask task, bool proceduralMemoryEnabled)
    {
        var instructions = proceduralMemoryEnabled
            ? "You complete booking tasks using the supplied tools. If you recall a procedure for this "
              + "task, follow it. Reply with the confirmation reference exactly as the tool returns it."
            : "You complete booking tasks using the supplied tools. Reply with the confirmation "
              + "reference exactly as the tool returns it.";

        return chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = proceduralMemoryEnabled ? "WithProcedures" : "Control",
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = [.. task.CreateTools()],
            },
        });
    }

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
        log.WriteLine($"  improvedWithRepetition={result.ImprovedWithRepetition}");
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
