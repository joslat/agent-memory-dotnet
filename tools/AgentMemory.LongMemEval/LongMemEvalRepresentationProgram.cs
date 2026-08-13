using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.Llm;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.LongMemEval;

/// <summary>
/// P2. Does the <b>structured representation</b> lose the answer that the raw text carries, with
/// retrieval held at 100%?
/// </summary>
/// <remarks>
/// <para>
/// <b>The last untested candidate.</b> The clean-context oracle answers 96.6% correctly from raw
/// messages; real structured runs score ~88%. Two candidate explanations have now been eliminated —
/// decomposed answering won 0 of 29, and 9.2× context noise moved accuracy by nothing. What remains
/// is that the loss happens at <i>extraction</i>: the oracle reads speakers, timestamps and ordering
/// that a subject–predicate–object triple has no slot for.
/// </para>
/// <para>
/// <b>Recall stays pinned at 100%.</b> Both arms see exactly the gold sessions — one as raw messages,
/// one as everything the extractor produced from those same messages. No database, no retrieval, no
/// ranking, no top-K. If the structured arm falls toward 88%, the loss is in the representation and
/// no retrieval improvement can recover it.
/// </para>
/// <para>
/// <b>The extractor is the multi-session batch path deliberately</b> — every recorded quality number
/// in this project came from that extractor. Measuring a different one would answer a question nobody
/// asked. Rendering reuses <c>BuildAnswerPrompt(MemoryContext, …)</c>, the same code the real
/// structured arm uses, so this measures the representation rather than a formatter written for it.
/// </para>
/// </remarks>
internal static class LongMemEvalRepresentationProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var extractionDeployment =
                Environment.GetEnvironmentVariable("AZURE_OPENAI_EXTRACTION_DEPLOYMENT") ?? deployment;
            var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

            using var answerClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            // The same wrapper the prepared-pair path uses: this deployment rejects an explicit
            // temperature of 0, and reaching for a second workaround would measure a different client
            // than every other recorded run.
            using var extractionClient = new LongMemEvalChatCallMeter(
                new ProviderCompatibleExtractionChatClient(
                    azureClient.GetChatClient(extractionDeployment).AsIChatClient()));

            var benchmarkOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
                options.DatasetPath, options.Questions, options.Seed,
                judgeRetryAttempts: 0, LongMemEvalEvidenceDetail.Identifiers, maxRelevantMessages: 30);
            var evidenceIndex = LongMemEvalEvidenceIndex.Load(options.DatasetPath, benchmarkOptions);
            var questions = evidenceIndex.Questions.ToList();
            var judge = new LongMemEvalJudge(answerClient, NullLogger<LongMemEvalJudge>.Instance);

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IChatClient>(extractionClient);
            services.AddLlmExtraction(llm =>
            {
                llm.ModelId = extractionDeployment;
                llm.Temperature = 0;
                llm.MaxRetries = 2;
                llm.UseJsonResponseFormat = true;
                llm.UseUnifiedExtraction = true;
                llm.UseMultiSessionBatchExtraction = true;
            });
            var provider = services.BuildServiceProvider();

            Console.WriteLine(
                $"longmemeval: representation sweep over {questions.Count} questions "
                + "(gold sessions only, recall pinned at 100%).");

            var correct = 0;
            var comparable = 0;
            var emptyExtractions = 0;
            var details = new List<object>();

            foreach (var (question, index) in questions.Select((q, i) => (q, i)))
            {
                var goldOrigins = question.Messages
                    .Where(message => question.AnswerSessionIds.Contains(message.SourceSessionId))
                    .ToList();

                var (context, origins, learned) = await ExtractAsync(
                    provider, question, goldOrigins).ConfigureAwait(false);

                if (learned == 0) emptyExtractions++;

                var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
                    context, question.InvocationPrompt, question.QuestionDate, origins);

                string answer;
                try
                {
                    var response = await answerClient.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, AgentMemoryLongMemEvalAdapter.SystemPrompt),
                        new ChatMessage(ChatRole.User, prompt),
                    ]).ConfigureAwait(false);
                    answer = response.Text ?? string.Empty;
                }
                catch (Exception ex)
                {
                    details.Add(new { question.QuestionId, status = $"threw:{ex.GetType().Name}", learned });
                    Console.WriteLine($"  [{index + 1}/{questions.Count}] {question.QuestionId} threw");
                    continue;
                }

                var judgment = await judge.JudgeAsync(
                    answer, ToBenchmarkQuestion(question)).ConfigureAwait(false);
                var valid = LongMemEvalRunValidator.TryParseJudgeVerdict(
                    judgment.Explanation, out var parsed) && parsed == judgment.Correct;
                if (!valid)
                {
                    details.Add(new { question.QuestionId, status = "judge-invalid", learned });
                    Console.WriteLine($"  [{index + 1}/{questions.Count}] {question.QuestionId} judge?");
                    continue;
                }

                comparable++;
                if (judgment.Correct == true) correct++;
                details.Add(new
                {
                    question.QuestionId,
                    question.QuestionType,
                    status = "completed",
                    correct = judgment.Correct,
                    learnedItems = learned,
                    goldMessages = goldOrigins.Count,
                    promptChars = prompt.Length,
                });

                Console.WriteLine(
                    $"  [{index + 1}/{questions.Count}] {question.QuestionId} "
                    + $"{(judgment.Correct == true ? "Y" : "n")} learned={learned} "
                    + $"promptChars={prompt.Length}");
            }

            // The witness. An extractor that returned nothing produces an empty context, and an empty
            // context scores like a no-memory arm -- which would look exactly like "the representation
            // loses everything" while actually measuring a broken extraction call.
            var isVoid = comparable == 0 || emptyExtractions == questions.Count;
            var accuracy = comparable == 0 ? (double?)null : (double)correct / comparable;

            var runId = $"representation-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            var report = new
            {
                schemaVersion = 1,
                runId,
                dataset = Path.GetFileName(options.DatasetPath),
                options.Questions,
                options.Seed,
                answerDeployment = deployment,
                extractionDeployment,
                extractor = "multi-session-batch-unified",
                comparable,
                correct,
                accuracy,
                emptyExtractions,
                isVoid,
                calls = new
                {
                    answerAndJudge = answerClient.Snapshot().Calls,
                    extraction = extractionClient.Snapshot().Calls,
                },
                questions = details,
            };

            var output = options.OutputPath ?? Path.Combine("artifacts", "evaluation", $"{runId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(
                output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine(
                $"longmemeval: STRUCTURED representation, recall 100% — correct {correct}/{comparable}"
                + (accuracy is { } a ? $" ({a:P1})" : " (n/a)")
                + $"; empty extractions {emptyExtractions}/{questions.Count}");
            Console.WriteLine(
                $"longmemeval: calls answer+judge={answerClient.Snapshot().Calls} "
                + $"extraction={extractionClient.Snapshot().Calls}  report {output}");

            if (isVoid)
            {
                Console.Error.WriteLine(
                    "longmemeval: VOID — nothing comparable, or every extraction returned empty.");
                return 3;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"longmemeval: {ex.Message}");
            return 1;
        }
    }

    private static async Task<(MemoryContext Context, Dictionary<string, LongMemEvalMessageOrigin> Origins, int Learned)>
        ExtractAsync(
            IServiceProvider provider,
            LongMemEvalEvidenceQuestion question,
            IReadOnlyList<LongMemEvalMessageOrigin> goldOrigins)
    {
        var origins = new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal);
        var messageIdsBySession = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var bySession = goldOrigins
            .GroupBy(origin => origin.SourceSessionId, StringComparer.Ordinal)
            .ToList();

        var requests = new List<ExtractionRequest>();
        foreach (var session in bySession)
        {
            var messages = new List<Message>();
            foreach (var origin in session)
            {
                var messageId = $"{question.QuestionId}-{origin.MessageOrdinal}";
                origins[messageId] = origin;
                messages.Add(new Message
                {
                    MessageId = messageId,
                    SessionId = session.Key,
                    ConversationId = session.Key,
                    Role = origin.Role,
                    Content = origin.FormattedContent,
                    // Monotonic, matching the adapter: the real dates travel as provenance and are
                    // rendered from `origins`, exactly as the shipped structured arm does it.
                    TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(origin.MessageOrdinal),
                });
            }

            messageIdsBySession[session.Key] = messages.Select(message => message.MessageId).ToList();
            requests.Add(new ExtractionRequest { Messages = messages, SessionId = session.Key });
        }

        using var scope = provider.CreateScope();
        var extractor = scope.ServiceProvider.GetServices<IMultiSessionUnifiedMemoryExtractor>()
            .First(candidate => candidate.IsEnabled);
        var extracted = await extractor
            .ExtractAsync(requests, maxSessionsPerBatch: 4, maxInputTokens: 100_000)
            .ConfigureAwait(false);

        var entities = new List<Entity>();
        var facts = new List<Fact>();
        var preferences = new List<Preference>();
        var now = DateTimeOffset.UtcNow;
        var counter = 0;

        foreach (var (sessionId, result) in extracted)
        {
            // Batch provenance, matching the shipped default (ExtractionProvenanceMode.Batch): an
            // extracted item links to every source message of its session. ExtractedFact carries no
            // SourceMessageIds of its own -- the edges are written by the persistence stage -- so
            // reconstructing them any other way would render dates the real arm does not have.
            var sessionMessageIds = messageIdsBySession.TryGetValue(sessionId, out var ids)
                ? ids
                : (IReadOnlyList<string>)[];

            foreach (var entity in result.Entities)
            {
                entities.Add(new Entity
                {
                    EntityId = $"e{counter++}",
                    Name = entity.Name,
                    Type = entity.Type,
                    Description = entity.Description,
                    Confidence = entity.Confidence,
                    SourceMessageIds = sessionMessageIds,
                    CreatedAtUtc = now,
                });
            }

            foreach (var fact in result.Facts)
            {
                facts.Add(new Fact
                {
                    FactId = $"f{counter++}",
                    Subject = fact.Subject,
                    Predicate = fact.Predicate,
                    Object = fact.Object,
                    Confidence = fact.Confidence,
                    ValidFrom = fact.ValidFrom,
                    ValidUntil = fact.ValidUntil,
                    SourceMessageIds = sessionMessageIds,
                    CreatedAtUtc = now,
                });
            }

            foreach (var preference in result.Preferences)
            {
                preferences.Add(new Preference
                {
                    PreferenceId = $"p{counter++}",
                    Category = preference.Category,
                    PreferenceText = preference.PreferenceText,
                    Confidence = preference.Confidence,
                    SourceMessageIds = sessionMessageIds,
                    CreatedAtUtc = now,
                });
            }
        }

        var context = new MemoryContext
        {
            SessionId = question.QuestionId,
            AssembledAtUtc = now,
            // RelevantMessages deliberately EMPTY. Including the raw messages would make this the
            // hybrid arm, and the whole question is what survives extraction on its own.
            RelevantEntities = new MemoryContextSection<Entity> { Items = entities },
            RelevantFacts = new MemoryContextSection<Fact> { Items = facts },
            RelevantPreferences = new MemoryContextSection<Preference> { Items = preferences },
        };

        return (context, origins, entities.Count + facts.Count + preferences.Count);
    }

    private static ExternalBenchmarkQuestion ToBenchmarkQuestion(LongMemEvalEvidenceQuestion indexed) => new()
    {
        QuestionId = indexed.QuestionId,
        QuestionType = indexed.QuestionType,
        Question = indexed.Question,
        GoldAnswer = indexed.GoldAnswer,
        QuestionDate = indexed.QuestionDate,
        IsAbstention = indexed.IsAbstention,
    };

    private static RepresentationOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }

        var datasetPath = Value("--dataset")
            ?? LongMemEvalDatasetLocator.Resolve(null, Environment.GetEnvironmentVariable)
            ?? throw new ArgumentException("--dataset <longmemeval_s_cleaned.json> is required.");
        if (!File.Exists(datasetPath))
            throw new FileNotFoundException("LongMemEval dataset not found.", datasetPath);

        return new RepresentationOptions(
            datasetPath,
            ParsePositive(Value("--questions"), 10, "--questions"),
            ParsePositive(Value("--seed"), 42, "--seed"),
            Value("--output"));
    }

    private static int ParsePositive(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
        return parsed;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is required; refusing to create a synthetic LongMemEval score.");

    private sealed record RepresentationOptions(
        string DatasetPath, int Questions, int Seed, string? OutputPath);
}
