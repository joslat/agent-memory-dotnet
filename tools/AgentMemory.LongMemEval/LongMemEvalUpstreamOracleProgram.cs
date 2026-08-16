using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 28.2. Runs <b>AgentEval's</b> oracle instead of ours, so the hand-rolled one can be retired.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this replaces local code rather than adding to it.</b> The oracle is a property of the
/// <i>dataset</i> — project the labelled evidence sessions, strip gold labels, answer through a
/// retrieval-bypassing reader. It contains nothing about this memory system, which is exactly why it
/// was rebuilt three times here before being asked for upstream. AgentEval 0.21.0-beta ships it public,
/// with the two controls that mattered (<c>DistractorSessions</c>, <c>GoldSessionFraction</c>) and the
/// realised-versus-requested reporting that makes a level interpretable.
/// </para>
/// <para>
/// <b>It brings its own void witness, which is the part worth having.</b>
/// <c>DistractorRequestFullyMet</c> answers "did the level actually degrade anything?" — the question
/// our hand-rolled sweep had to answer by comparing context sizes across levels, and which fired on
/// <c>gold=0.85</c> when the ceiling made that level identical to the control.
/// </para>
/// <para>
/// <b>Retirement is earned, not assumed.</b> This verb exists first to reproduce a level we already
/// measured locally. Two oracles that disagree on the same level are not interchangeable, and swapping
/// them silently would break comparability with every number in the archive.
/// </para>
/// </remarks>
internal static class LongMemEvalUpstreamOracleProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

            using var judge = new LongMemEvalChatCallMeter(azure.GetChatClient(deployment).AsIChatClient());
            using var answer = new LongMemEvalChatCallMeter(azure.GetChatClient(deployment).AsIChatClient());

            var dataset = LongMemEvalDatasetLocator.Resolve(
                    Value(args, "--dataset"), Environment.GetEnvironmentVariable)
                ?? throw new InvalidOperationException(
                    "No LongMemEval dataset found. Pass --dataset or set LONGMEMEVAL_DATASET.");

            var questions = int.TryParse(Value(args, "--questions"), out var q) ? q : 30;
            var seed = int.TryParse(Value(args, "--seed"), out var s) ? s : 42;
            var distractors = int.TryParse(Value(args, "--distractor-sessions"), out var k) ? k : 0;
            var goldFraction = double.TryParse(
                Value(args, "--gold-fraction"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 1.0;

            var options = LongMemEvalBenchmarkProtocol.CreateOptions(
                dataset, questions, seed,
                judgeRetryAttempts: 0,
                LongMemEvalEvidenceDetail.Identifiers,
                maxRelevantMessages: 30);
            var oracleOptions = new LongMemEvalOracleOptions
            {
                DistractorSessions = distractors,
                GoldSessionFraction = goldFraction,
            };

            Console.WriteLine(
                $"longmemeval: UPSTREAM oracle (AgentEval 0.21.0-beta) over {questions} questions, "
                + $"K={distractors} gold={goldFraction:0.##}. No Neo4j, no extraction, no corpus.");

            var runner = LongMemEvalBenchmarkRunner.Create(judge, dataset);
            var result = await runner.RunOracleAsync(answer, options, oracleOptions).ConfigureAwait(false);

            var scored = result.QuestionResults.Where(question => question.Correct is not null).ToList();
            var correct = scored.Count(question => question.Correct == true);

            Console.WriteLine(
                $"  correct {correct}/{scored.Count} = {(scored.Count == 0 ? 0 : (double)correct / scored.Count):P1}");

            var artifacts = Value(args, "--artifacts") ?? Path.Combine("artifacts", "evaluation");
            Directory.CreateDirectory(artifacts);
            var path = Path.Combine(
                artifacts, $"upstream-oracle-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(
                new
                {
                    probe = "upstream-oracle",
                    task = "28.2",
                    agentEval = "0.21.0-beta",
                    requested = new { distractorSessions = distractors, goldSessionFraction = goldFraction },
                    correct,
                    scored = scored.Count,
                    accuracy = scored.Count == 0 ? (double?)null : (double)correct / scored.Count,
                    // The comparison this verb exists to support. Two oracles that disagree on the same
                    // level are not interchangeable, and swapping them silently would break
                    // comparability with every oracle number already in the archive.
                    note = "Run against the locally-measured level before retiring the hand-rolled "
                        + "oracle. Local --oracle-precision at K=0 gold=1.0 measured 96.6%.",
                    questionResults = scored.Select(question => new
                    {
                        question.QuestionId,
                        question.Correct,
                    }),
                },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"longmemeval: wrote {path}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"longmemeval: upstream oracle failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");
}
