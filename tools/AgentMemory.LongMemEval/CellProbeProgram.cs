using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Extracts an EXTERNAL corpus cell and reports whether amount subjects collide in the store.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this exists to settle.</b> Our probe found several distinct amounts under the bare
/// subject <c>Payment</c> and I scoped that as an extraction defect of ours. AgentEval showed it is
/// not: every amount-bearing sentence in their arithmetic corpus is
/// <c>"Payment logged against {job}: ${amount} for {item}."</c> — 248 of 248 — so the grammatical
/// subject really is the bare common noun, and our extractor reported what the sentence said.
/// </para>
/// <para>
/// Two explanations survive that, and they imply opposite work. Either their bare subject is the
/// cause (fixable by wording), or an amount is a <b>four-place fact</b> — payer, job, amount, line
/// item — that a triple store must reify or lose the join however it is worded, in which case no
/// rewording helps and the defect is ours in a different place. Cells B and C differ in exactly one
/// thing, the payment sentence's subject, so the pair separates them.
/// </para>
/// <para>
/// <b>No judge, no questions, no scoring.</b> This ingests and measures the store. That makes it
/// materially cheaper than an arm and keeps it incapable of producing an accuracy number that could
/// later be read as a result it was never designed to support.
/// </para>
/// </remarks>
internal static class CellProbeProgram
{
    internal static readonly string[] KnownOptions = ["--cell-probe", "--max-entries"];

    public static async Task<int> RunAsync(string[] args)
    {
        var path = Value(args, "--cell-probe")
            ?? throw new ArgumentException("--cell-probe requires a path to a cell .json.");
        var maxEntries = int.TryParse(Value(args, "--max-entries"), out var n) ? n : int.MaxValue;
        path = Path.GetFullPath(path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entries = document.RootElement.EnumerateArray().Take(maxEntries).ToArray();
        Console.WriteLine(
            $"cell-probe: {Path.GetFileName(path)} — {entries.Length} entr(ies), extraction only.");

        var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
        var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
        var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
        var embeddingDeployment = RequiredEnvironment("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
        var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        using var extractionChat = new LongMemEvalChatCallMeter(
            new ProviderCompatibleExtractionChatClient(azure.GetChatClient(deployment).AsIChatClient()));
        var embeddings = azure.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator();
        var dimensions = await LongMemEvalRuntime
            .ProbeEmbeddingDimensionsAsync(embeddings).ConfigureAwait(false);

        await using var profile = await LongMemEvalMemoryProfile.StartAsync(
            embeddings, extractionChat, LongMemEvalMemoryMode.Structured, deployment, dimensions,
            Console.Out, CancellationToken.None).ConfigureAwait(false);

        var memory = profile.Services.GetRequiredService<IMemoryService>();
        var unit = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            // One owner per entry, mirroring the benchmark harness's isolation exactly: pooling them
            // would let two entries' payments collide for a reason that has nothing to do with the
            // cell under test.
            var owner = $"cell-owner-{i:D4}";
            var sessions = entries[i].GetProperty("haystack_sessions").EnumerateArray().ToArray();
            for (var s = 0; s < sessions.Length; s++)
            {
                var sessionId = $"cell-session-{i:D4}-{s:D3}";
                var messages = sessions[s].EnumerateArray()
                    .Select(turn => new Message
                    {
                        MessageId = Guid.NewGuid().ToString("n"),
                        ConversationId = sessionId,
                        SessionId = sessionId,
                        Role = turn.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user",
                        Content = turn.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                        TimestampUtc = DateTimeOffset.UtcNow,
                    })
                    .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                    .ToArray();
                if (messages.Length == 0) continue;

                await memory.AddMessagesAsync(messages, CancellationToken.None).ConfigureAwait(false);
                await memory.ExtractAndPersistAsync(
                    new ExtractionRequest { Messages = messages, SessionId = sessionId, UserId = owner },
                    CancellationToken.None).ConfigureAwait(false);
                if (++unit % 10 == 0) Console.WriteLine($"cell-probe: extraction units {unit}.");
            }
        }

        var probe = new Neo4jLongMemEvalGraphProbe(
            profile.Services.GetRequiredService<global::Neo4j.Driver.IDriver>());
        var objects = await probe.ReadFactObjectShapeAsync(CancellationToken.None).ConfigureAwait(false);
        var ambiguity = await probe.ReadSubjectAmbiguityAsync(CancellationToken.None).ConfigureAwait(false);
        var amountGroups = await probe.ReadAmountGroupCollisionAsync(CancellationToken.None)
            .ConfigureAwait(false);

        Console.WriteLine(
            $"cell-probe: objects — {objects.AmountBearing} amount-bearing of {objects.Facts} facts.");
        Console.WriteLine(
            $"cell-probe: AMOUNT-SUBJECT COLLISION — {amountGroups.CollidingGroups} of " +
            $"{amountGroups.AmountGroups} amount-bearing subject/predicate groups hold >1 distinct " +
            $"amount = {(amountGroups.AmountGroups == 0 ? 0 : 100.0 * amountGroups.CollidingGroups / amountGroups.AmountGroups):F1}%");
        foreach (var sample in amountGroups.Samples) Console.WriteLine($"cell-probe:   {sample}");
        Console.WriteLine($"cell-probe: (subject-ambiguity groups overall: {ambiguity.Pairs.Count})");
        Console.WriteLine($"cell-probe: extraction LLM calls {extractionChat.Snapshot()}");
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index < 0 || index + 1 >= args.Length ? null : args[index + 1];
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"{name} must be set.");
}
