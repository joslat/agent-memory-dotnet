using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 26.2. Runs <see cref="ProcedureRetrievalSet"/> through real procedure recall and scores it with
/// <see cref="ProcedureRetrievalPrecision"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No chat model and no judge.</b> Storing and recalling procedures needs embeddings and a graph,
/// nothing else — so this measures procedural retrieval for the cost of ~32 embedding calls, against a
/// benefit harness that costs hundreds of agent turns. The two answer different questions and the
/// cheap one was missing.
/// </para>
/// <para>
/// <b>The abstention threshold is a parameter and is reported.</b> Whether a retriever "answers" is
/// entirely a function of the minimum score it will accept, so a precision figure without its
/// threshold is not reproducible. Sweeping it is the point: <c>WrongProcedureRate</c> and
/// <c>AbstentionRate</c> move in opposite directions, and the interesting number is where.
/// </para>
/// <para>
/// <b>Void witness.</b> If nothing was stored, or no query retrieved anything at any threshold, the run
/// prints VOID and exits non-zero — a retriever that returns nothing scores a perfect
/// <c>WrongProcedureRate</c> of zero, which would read as flawless precision.
/// </para>
/// </remarks>
internal static class ProcedureRetrievalProgram
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            var thresholds = ParseThresholds(args);
            var artifacts = Value(args, "--artifacts") ?? Path.Combine("artifacts", "evaluation");

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var embeddingDeployment = RequiredEnvironment("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");

            var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            var embeddings = azure.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator();
            using var chat = azure.GetChatClient(deployment).AsIChatClient();
            var dimensions = await LongMemEvalRuntime
                .ProbeEmbeddingDimensionsAsync(embeddings).ConfigureAwait(false);

            await using var profile = await LongMemEvalMemoryProfile.StartAsync(
                embeddings,
                // Never used: this verb stores and recalls procedures and extracts nothing. Handed the
                // real client rather than a stub so that anything which DID try to extract would fail
                // loudly instead of silently producing empty memory.
                extractionChatClient: chat,
                LongMemEvalMemoryMode.Structured,
                extractionModelId: deployment,
                dimensions,
                Console.Out,
                cancellationToken).ConfigureAwait(false);

            await using var scope = profile.Services.CreateAsyncScope();
            var reasoning = scope.ServiceProvider.GetRequiredService<IReasoningMemoryService>();
            var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingOrchestrator>();

            // A fresh owner per run, so a re-run cannot recall the previous run's procedures and score
            // itself against a store it did not build.
            var owner = MemoryScope.For($"procedure-retrieval-{Guid.NewGuid():N}");

            Console.WriteLine(
                $"longmemeval: procedure retrieval over {ProcedureRetrievalSet.Procedures.Count} "
                + $"procedures x {ProcedureRetrievalSet.Queries.Count} queries, "
                + $"thresholds = {string.Join(", ", thresholds)}. Embedding calls only.");

            var storedIds = await StoreAsync(reasoning, embedder, owner, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"longmemeval: stored {storedIds.Count} procedures.");

            var levels = new List<object>();
            var anyRetrieval = false;

            foreach (var threshold in thresholds)
            {
                var cases = new List<ProcedureRetrievalCase>(ProcedureRetrievalSet.Queries.Count);
                foreach (var query in ProcedureRetrievalSet.Queries)
                {
                    var hits = await reasoning.SearchSimilarTracesAsync(
                        await embedder.EmbedAsync(query.Query, cancellationToken).ConfigureAwait(false),
                        proceduresOnly: true,
                        successFilter: true,
                        limit: 3,
                        minScore: threshold,
                        scope: owner,
                        cancellationToken).ConfigureAwait(false);

                    // Map the stored trace back to its fixture id through the task text: the trace id
                    // is generated, and scoring against generated ids would make the labels unreadable.
                    var retrieved = hits
                        .Select(hit => storedIds.TryGetValue(hit.TraceId, out var id) ? id : hit.TraceId)
                        .ToList();
                    if (retrieved.Count > 0) anyRetrieval = true;

                    cases.Add(new ProcedureRetrievalCase(query.TaskId, retrieved, query.Correct));
                }

                var score = ProcedureRetrievalPrecision.Score(cases);
                Console.WriteLine(
                    $"  minScore={threshold:0.00}  correct={score.CorrectAtOne,2}  wrong={score.WrongAtOne,2}  "
                    + $"abstained={score.Abstained,2}  missed={score.Missed,2}  "
                    + $"wrongRate={score.WrongProcedureRate:P1}  "
                    + $"precisionWhenAnswering={score.PrecisionWhenAnswering:P1}");

                levels.Add(new
                {
                    minScore = threshold,
                    score.Total,
                    score.CorrectAtOne,
                    score.WrongAtOne,
                    score.Abstained,
                    score.Missed,
                    score.PrecisionAtOne,
                    score.WrongProcedureRate,
                    score.AbstentionRate,
                    score.MissRate,
                    score.PrecisionWhenAnswering,
                    cases = cases.Select(c => new { c.TaskId, c.RetrievedProcedureIds, c.CorrectProcedureIds }),
                });
            }

            if (storedIds.Count == 0 || !anyRetrieval)
            {
                // A void run must say WHICH gate shut, or the next person re-derives it. Three
                // independent things make a procedure unretrievable and they look identical from
                // outside: no trace at all, a trace that was never promoted (proceduresOnly filters
                // every episode out), and a trace whose success flag excludes it.
                var probeEmbedding = await embedder
                    .EmbedAsync(ProcedureRetrievalSet.Procedures[0].Task, cancellationToken)
                    .ConfigureAwait(false);

                var anyTrace = await reasoning.SearchSimilarTracesAsync(
                    probeEmbedding, proceduresOnly: null, successFilter: null,
                    limit: 5, minScore: 0, scope: owner, cancellationToken).ConfigureAwait(false);
                var anyProcedure = await reasoning.SearchSimilarTracesAsync(
                    probeEmbedding, proceduresOnly: true, successFilter: null,
                    limit: 5, minScore: 0, scope: owner, cancellationToken).ConfigureAwait(false);
                var anySuccessful = await reasoning.SearchSimilarTracesAsync(
                    probeEmbedding, proceduresOnly: null, successFilter: true,
                    limit: 5, minScore: 0, scope: owner, cancellationToken).ConfigureAwait(false);

                Console.Error.WriteLine(
                    "longmemeval: VOID — no query retrieved anything at any threshold. A retriever "
                    + "that returns nothing scores a wrong-procedure rate of zero, which reads as "
                    + "perfect precision. Refusing to report it.");
                Console.Error.WriteLine(
                    $"  stored={storedIds.Count}  unfiltered={anyTrace.Count}  "
                    + $"proceduresOnly={anyProcedure.Count}  successfulOnly={anySuccessful.Count}");
                Console.Error.WriteLine(
                    anyTrace.Count == 0
                        ? "  → nothing is retrievable at all: the traces have no task embedding, or "
                          + "the owner scope does not match what was written."
                        : anyProcedure.Count == 0
                            ? "  → traces exist but none is a PROCEDURE: promotion did not persist "
                              + "trace_kind, so proceduresOnly filters every one of them out."
                            : "  → traces and procedures exist; the success filter is excluding them.");
                return 3;
            }

            Directory.CreateDirectory(artifacts);
            var path = Path.Combine(artifacts, $"procedure-retrieval-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(
                new
                {
                    probe = "procedure-retrieval",
                    task = "26.2",
                    procedures = ProcedureRetrievalSet.Procedures.Count,
                    queries = ProcedureRetrievalSet.Queries.Count,
                    abstainExpected = ProcedureRetrievalSet.Queries.Count(q => q.Correct.Count == 0),
                    // Named in the artifact so nobody quotes a precision figure as an accuracy.
                    note = "correct / wrong / abstained / missed are reported separately. Abstention "
                        + "(nothing applied, nothing returned) is NOT a failure. A MISS (a "
                        + "procedure applied and nothing was returned) is a failure, and a safe "
                        + "one -- it is neither of its neighbours and folding it into either "
                        + "loses the distinction this instrument exists to preserve.",
                    levels,
                },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"longmemeval: wrote {path}");

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: procedure retrieval failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>Stores every fixture as a promoted, successful procedure. Returns traceId → fixture id.</summary>
    private static async Task<Dictionary<string, string>> StoreAsync(
        IReasoningMemoryService reasoning,
        IEmbeddingOrchestrator embedder,
        MemoryScope owner,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fixture in ProcedureRetrievalSet.Procedures)
        {
            var trace = await reasoning.StartTraceAsync(
                sessionId: $"procedure-set-{fixture.Id}",
                task: fixture.Task,
                // Recall is a vector search over the task; a fixture stored without this is invisible
                // and the whole set would score as an abstaining retriever.
                taskEmbedding: await embedder.EmbedAsync(fixture.Task, cancellationToken).ConfigureAwait(false),
                ownerId: owner.OwnerId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await reasoning.CompleteTraceAsync(
                trace.TraceId, fixture.Outcome, success: true, cancellationToken).ConfigureAwait(false);

            // Without promotion these stay Episode-kinded and proceduresOnly filters every one of them
            // out — the set would measure an empty store.
            await reasoning.PromoteTraceAsync(trace.TraceId, TraceKind.Procedure, cancellationToken)
                .ConfigureAwait(false);

            map[trace.TraceId] = fixture.Id;
        }

        return map;
    }

    private static IReadOnlyList<double> ParseThresholds(string[] args) =>
        (Value(args, "--min-scores") ?? "0.0,0.3,0.5,0.7")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 1
                ? parsed
                : throw new ArgumentException("--min-scores must be values in [0, 1]."))
            .Distinct().OrderBy(value => value).ToList();

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");
}
