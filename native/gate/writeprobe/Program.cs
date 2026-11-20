// The R1 write path, natively: store a message, extract it through IChatClient (a fixed reply: the model is not under
// test), persist, and read every written property back through the library. Usage:
//   WriteProbe <bolt-uri> <user> <password-env-var>     (the password is read from that environment variable)
// Prints one JSON report; exit code 0 when every check passes.
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Extraction.Llm;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 3 || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(args[2])))
{
    Console.Error.WriteLine("usage: WriteProbe <bolt-uri> <user> <password-env-var>");
    return 2;
}

const string Owner = "write-probe-owner";
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IChatClient>(new FixedReplyChat());
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new LetterEmbeddings());
services.AddAgentMemoryCore(o => o.WorkingMemory.Enabled = false);
services.AddNeo4jAgentMemory(o =>
{
    o.Uri = args[0];
    o.Username = args[1];
    o.Password = Environment.GetEnvironmentVariable(args[2])!;
    o.EmbeddingDimensions = LetterEmbeddings.Dimensions;
});
services.AddLlmExtraction();

var report = new JsonObject();
var checks = new JsonObject();
try
{
    await using var provider = services.BuildServiceProvider();
    await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
    await using var scope = provider.CreateAsyncScope();
    var ownerContext = scope.ServiceProvider.GetRequiredService<IWritableMemoryOwnerContext>();
    using (ownerContext.BeginOwnerScope(Owner))
    {
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
        var message = await memory.AddMessageAsync("probe-session", "probe-conversation", "user",
            "Since March 2024 I work in Zurich. Please keep your answers concise.",
            new Dictionary<string, object> { ["source"] = "write-probe", ["turn"] = 1 });
        var extracted = await memory.ExtractAndPersistAsync(new ExtractionRequest
        {
            // The owner of extracted memories is explicit on the request; the ambient owner scope does not set it.
            Messages = [message], SessionId = "probe-session", UserId = Owner,
        });
        report["extracted"] = new JsonObject
        {
            ["facts"] = extracted.Facts.Count, ["preferences"] = extracted.Preferences.Count,
            ["entities"] = extracted.Entities.Count,
        };

        var facts = await longTerm.GetFactsBySubjectAsync("user");
        var preferences = await longTerm.GetPreferencesByCategoryAsync("style");
        var work = facts.FirstOrDefault(f => f.Object == "Zurich");
        var concise = preferences.FirstOrDefault(p => p.PreferenceText.Contains("concise", StringComparison.Ordinal));
        report["fact"] = work is null ? null : new JsonObject
        {
            ["id"] = work.FactId, ["subject"] = work.Subject, ["predicate"] = work.Predicate, ["object"] = work.Object,
            ["owner"] = work.OwnerId, ["confidence"] = work.Confidence, ["validFrom"] = work.ValidFrom?.ToString("O"),
            ["embedding"] = work.Embedding?.Length ?? 0, ["sourceMessages"] = work.SourceMessageIds.Count,
        };
        report["preference"] = concise is null ? null : new JsonObject
        {
            ["id"] = concise.PreferenceId, ["text"] = concise.PreferenceText, ["owner"] = concise.OwnerId,
            ["embedding"] = concise.Embedding?.Length ?? 0,
        };

        checks["message stored with its id"] = !string.IsNullOrEmpty(message.MessageId);
        checks["fact extracted and read back"] = work is not null;
        checks["fact owned by the scope's owner"] = work?.OwnerId == Owner;
        checks["fact predicate and subject written"] = work is { Subject.Length: > 0, Predicate.Length: > 0 };
        checks["fact valid from March 2024 (month precision)"] =
            work?.ValidFrom is { } from && from.Year == 2024 && from.Month == 3 && from.Day == 1;
        checks["fact embedded"] = work?.Embedding?.Length == LetterEmbeddings.Dimensions;
        checks["fact confidence written"] = work is { Confidence: > 0 };
        checks["preference extracted and read back"] = concise is not null;
        checks["preference owned by the scope's owner"] = concise?.OwnerId == Owner;
        checks["preference embedded"] = concise?.Embedding?.Length == LetterEmbeddings.Dimensions;
    }
}
catch (Exception ex)
{
    report["error"] = $"{ex.GetType().FullName}: {ex.Message}";
    var chain = new JsonArray();
    for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        chain.Add((JsonNode?)JsonValue.Create(
            $"{inner.GetType().FullName}: {inner.Message} | {inner.StackTrace?.Split(Environment.NewLine).FirstOrDefault()?.Trim()}"));
    report["inner"] = chain;
    report["stack"] = ex.StackTrace;
}
report["checks"] = checks;
var passed = checks.Count > 0 && checks.All(c => c.Value?.GetValue<bool>() == true) && report["error"] is null;
report["passed"] = passed;
using (var stdout = Console.OpenStandardOutput())
using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
    report.WriteTo(writer);
Console.WriteLine();
return passed ? 0 : 1;

/// <summary>The model is not under test: every extraction call gets the same reply, in the extraction reply's shape.</summary>
sealed class FixedReplyChat : IChatClient
{
    private const string Reply = """
        {"entities":[{"name":"Zurich","type":"LOCATION","confidence":0.9}],
         "facts":[{"subject":"user","predicate":"works_in","object":"Zurich","confidence":0.92,"valid_from":"2024-03",
                   "source_role":"user","source_turn":1}],
         "preferences":[{"category":"style","preference":"Prefers concise answers","confidence":0.9,"source_role":"user"}],
         "relations":[]}
        """;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply)));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Deterministic vectors from letter counts (never string.GetHashCode, which changes per process).</summary>
sealed class LetterEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 8;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var vectors = values.Select(text =>
        {
            var v = new float[Dimensions];
            foreach (var c in text.ToLowerInvariant())
                if (c is >= 'a' and <= 'z') v[(c - 'a') % Dimensions] += 1f;
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
            else v[0] = 1f;
            return new Embedding<float>(v);
        });
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(vectors));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
