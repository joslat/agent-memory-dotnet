// The R1 write path natively, through the native library's own explicit-write composition (WriteComposition, linked
// from tools/AgentMemory.Native) and its own OpenAI-compatible chat and embedding clients, against a stub model served
// by this process on 127.0.0.1 (the model is not under test; the HTTP clients, the writer, the judge and the store are).
// Two remembered turns: the first adds a fact and a preference, the second corrects the fact. Usage:
//   WriteProbe <bolt-uri> <user> <password-env-var>     (the password is read from that environment variable)
// Prints one JSON report; exit code 0 when every check passes.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Native.Chat;
using AgentMemory.Native.Composition;
using AgentMemory.Native.Embeddings;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 3 || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(args[2])))
{
    Console.Error.WriteLine("usage: WriteProbe <bolt-uri> <user> <password-env-var>");
    return 2;
}

const string Owner = "write-probe-owner";
const string Session = "probe-session";
const string Conversation = "probe-conversation";
var saidAt = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);

var report = new JsonObject();
var checks = new JsonObject();
await using var model = new StubModel();
try
{
    var settings = new MemorySettings
    {
        Uri = args[0],
        Username = args[1],
        Password = Environment.GetEnvironmentVariable(args[2])!,
        EmbeddingDimensions = StubModel.Dimensions,
    };
    var chat = new ChatEndpoint(new Uri(model.BaseUri, "v1/chat/completions"), "stub-writer", StubModel.ApiKey, TimeSpan.FromSeconds(10));
    var embedding = new EmbeddingEndpoint(new Uri(model.BaseUri, "v1/embeddings"), "stub-embedder", StubModel.ApiKey, TimeSpan.FromSeconds(10));
    await using var provider = WriteComposition.Build(settings, chat, embedding);
    await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

    // Turn 1: a fact with a date in its words, and a preference. The model's first answer is a 503 (B5: retried by status).
    model.FailNextChatWith = 503;
    var first = await WriteComposition.RememberAsync(provider, new RememberInput
    {
        Owner = Owner, SessionId = Session, ConversationId = Conversation, MessageId = "probe-m1", OccurredAt = saidAt,
        Text = "I work in Zurich since March 2, 2024. Please keep your answers concise.",
    }, CancellationToken.None);
    var turnOneChatCalls = model.ChatCalls;
    var afterFirst = await ReadAsync(provider);
    var zurich = afterFirst.Facts.FirstOrDefault(f => f.Object.StartsWith("Zurich", StringComparison.Ordinal));
    var concise = afterFirst.Preferences.FirstOrDefault(p => p.PreferenceText.Contains("concise", StringComparison.Ordinal));
    var storedMessage = afterFirst.Messages.FirstOrDefault(m => m.MessageId == "probe-m1");

    // Turn 2: a correction. The writer must be shown the stored fact, name it as replaced, and the judge must confirm.
    var second = await WriteComposition.RememberAsync(provider, new RememberInput
    {
        Owner = Owner, SessionId = Session, ConversationId = Conversation, MessageId = "probe-m2", OccurredAt = saidAt.AddMinutes(5),
        Text = "Actually I moved: I work in Basel now.",
    }, CancellationToken.None);
    var afterSecond = await ReadAsync(provider, zurich?.FactId);
    var zurichAfter = afterSecond.ById;
    var basel = afterSecond.Facts.FirstOrDefault(f => f.Object == "Basel");

    report["turn1"] = new JsonObject
    {
        ["facts"] = first.Extraction.Facts.Count, ["preferences"] = first.Extraction.Preferences.Count,
        ["chatCalls"] = turnOneChatCalls,
    };
    report["turn2"] = new JsonObject
    {
        ["facts"] = second.Extraction.Facts.Count, ["chatCalls"] = model.ChatCalls - turnOneChatCalls,
        ["writerSawStoredFact"] = model.WriterSawStoredFact, ["judgeCalls"] = model.JudgeCalls,
    };
    report["model"] = model.Describe();
    report["fact"] = zurich is null ? null : new JsonObject
    {
        ["text"] = $"{zurich.Subject} | {zurich.Predicate} | {zurich.Object}", ["owner"] = zurich.OwnerId,
        ["validFrom"] = zurich.ValidFrom?.ToString("O"), ["embedding"] = zurich.Embedding?.Length ?? 0,
        ["closedAfterCorrection"] = zurichAfter?.InvalidatedAtUtc?.ToString("O"),
    };
    report["correction"] = basel is null ? null : new JsonObject
    {
        ["text"] = $"{basel.Subject} | {basel.Predicate} | {basel.Object}", ["owner"] = basel.OwnerId,
        ["live"] = basel.InvalidatedAtUtc is null,
    };

    checks["message stored with its own id and time"] = storedMessage is not null && storedMessage.TimestampUtc == saidAt;
    checks["model's 503 retried by status (B5)"] = turnOneChatCalls == 2 && model.Failed503 == 1;
    checks["chat request: model, temperature 0, output cap, API key"] = model.RequestsLookRight;
    checks["fact written by the writer, owned by the owner"] = zurich?.OwnerId == Owner;
    checks["fact dated from its own words (2 March 2024)"] =
        zurich?.ValidFrom is { } from && from.Year == 2024 && from.Month == 3 && from.Day == 2;
    checks["fact embedded through the native embedding client"] = zurich?.Embedding?.Length == StubModel.Dimensions;
    checks["preference written, owned and embedded"] =
        concise?.OwnerId == Owner && concise.Embedding?.Length == StubModel.Dimensions;
    checks["writer shown the stored fact on the next turn"] = model.WriterSawStoredFact;
    checks["correction confirmed by the judge closes the old fact"] = model.JudgeCalls == 1 && zurichAfter?.InvalidatedAtUtc is not null;
    checks["corrected fact live and owned"] = basel is { InvalidatedAtUtc: null } && basel.OwnerId == Owner;
    checks["message, fact and preference vectors came from the endpoint"] = model.EmbeddingCalls > 0 && model.EmbeddingInputs >= 3;
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
    report["model"] = model.Describe();
}
report["checks"] = checks;
var passed = checks.Count > 0 && checks.All(c => c.Value?.GetValue<bool>() == true) && report["error"] is null;
report["passed"] = passed;
using (var stdout = Console.OpenStandardOutput())
using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
    report.WriteTo(writer);
Console.WriteLine();
return passed ? 0 : 1;

// Everything the owner has, read back through the library in the owner's scope.
static async Task<Snapshot> ReadAsync(ServiceProvider provider, string? factId = null)
{
    var scope = provider.CreateAsyncScope();
    await using (scope.ConfigureAwait(false))
    {
        using (scope.ServiceProvider.GetRequiredService<IWritableMemoryOwnerContext>()
                   .BeginOwnerScope(Owner))
        {
            var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
            var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
            return new Snapshot(
                // Strict isolation: a long-term read names its scope; the ambient owner only stamps writes.
                await longTerm.GetFactsBySubjectAsync("user", AgentMemory.Abstractions.Options.MemoryScope.For(Owner, includeShared: false)),
                await longTerm.GetPreferencesByCategoryAsync("general", AgentMemory.Abstractions.Options.MemoryScope.For(Owner, includeShared: false)),
                await shortTerm.GetConversationMessagesAsync(Conversation),
                factId is null ? null : await scope.ServiceProvider
                    .GetRequiredService<IFactRepository>().GetByIdAsync(factId));
        }
    }
}

sealed record Snapshot(
    IReadOnlyList<AgentMemory.Abstractions.Domain.Fact> Facts,
    IReadOnlyList<AgentMemory.Abstractions.Domain.Preference> Preferences,
    IReadOnlyList<AgentMemory.Abstractions.Domain.Message> Messages,
    AgentMemory.Abstractions.Domain.Fact? ById);

/// <summary>
/// An OpenAI-compatible model on 127.0.0.1: the writer's operations, the update judge's verdicts and deterministic
/// embeddings (letter counts, never string.GetHashCode). It records what the clients sent, never what they should send.
/// </summary>
sealed partial class StubModel : IAsyncDisposable
{
    public const int Dimensions = 8;
    public const string ApiKey = "probe-key";

    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private readonly List<string> _problems = [];

    public Uri BaseUri { get; }
    public int FailNextChatWith { get; set; }
    public int ChatCalls { get; private set; }
    public int Failed503 { get; private set; }
    public int JudgeCalls { get; private set; }
    public int EmbeddingCalls { get; private set; }
    public int EmbeddingInputs { get; private set; }
    public bool WriterSawStoredFact { get; private set; }
    public bool RequestsLookRight { get; private set; } = true;

    public StubModel()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public JsonObject Describe()
    {
        lock (_gate)
        {
            var problems = new JsonArray();
            foreach (var problem in _problems) problems.Add((JsonNode?)JsonValue.Create(problem));
            return new JsonObject
            {
                ["chatCalls"] = ChatCalls, ["failed503"] = Failed503, ["judgeCalls"] = JudgeCalls,
                ["embeddingCalls"] = EmbeddingCalls, ["embeddingInputs"] = EmbeddingInputs, ["problems"] = problems,
            };
        }
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            try { await HandleAsync(context); }
            catch (Exception ex)
            {
                lock (_gate) _problems.Add($"stub failed: {ex.GetType().Name}: {ex.Message}");
                context.Response.StatusCode = 500;
            }
            finally { context.Response.Close(); }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        using var request = JsonDocument.Parse(await reader.ReadToEndAsync());
        var root = request.RootElement;
        if (context.Request.Headers["Authorization"] != $"Bearer {ApiKey}") Problem("a request without the API key");

        if (context.Request.Url!.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal))
        {
            var inputs = root.GetProperty("input").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            lock (_gate) { EmbeddingCalls++; EmbeddingInputs += inputs.Count; }
            await WriteAsync(context, json =>
            {
                json.WriteStartArray("data");
                for (var i = 0; i < inputs.Count; i++)
                {
                    json.WriteStartObject();
                    json.WriteNumber("index", i);
                    json.WriteStartArray("embedding");
                    foreach (var value in Letters(inputs[i])) json.WriteNumberValue(value);
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            });
            return;
        }

        lock (_gate) ChatCalls++;
        if (root.GetProperty("model").GetString() != "stub-writer") Problem("chat model not sent");
        if (!root.TryGetProperty("temperature", out var temperature) || temperature.GetDouble() != 0) Problem("temperature not 0");
        if (!root.TryGetProperty("max_tokens", out var cap) || cap.GetInt32() <= 0) Problem("no output-token cap");
        if (FailNextChatWith != 0)
        {
            context.Response.StatusCode = FailNextChatWith;
            lock (_gate) { if (FailNextChatWith == 503) Failed503++; }
            FailNextChatWith = 0;
            return;
        }

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        var system = messages.First().GetProperty("content").GetString() ?? "";
        var user = messages.Last().GetProperty("content").GetString() ?? "";
        string reply;
        if (system.StartsWith("You keep a person's long-term memory", StringComparison.Ordinal))
        {
            lock (_gate) JudgeCalls++;
            reply = """{"p1": "yes"}""";
        }
        else if (user.Contains("Basel", StringComparison.Ordinal))
        {
            var stored = StoredZurich().Match(user);
            lock (_gate) WriterSawStoredFact = stored.Success;
            reply = $$"""{"ops":[{"op":"replace","kind":"fact","text":"user | works in | Basel","quote":"I work in Basel now","id":"{{(stored.Success ? stored.Groups["label"].Value : "F9")}}","until":null,"ref":null}]}""";
        }
        else
        {
            reply = """
                {"ops":[
                  {"op":"add","kind":"fact","text":"user | works in | Zurich since March 2, 2024","quote":"I work in Zurich since March 2, 2024","id":null,"until":null,"ref":null},
                  {"op":"add","kind":"preference","text":"Prefers concise answers","quote":"keep your answers concise","id":null,"until":null,"ref":null}]}
                """;
        }
        await WriteAsync(context, json =>
        {
            json.WriteString("model", "stub-writer");
            json.WriteStartArray("choices");
            json.WriteStartObject();
            json.WriteStartObject("message");
            json.WriteString("role", "assistant");
            json.WriteString("content", reply);
            json.WriteEndObject();
            json.WriteString("finish_reason", "stop");
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteStartObject("usage");
            json.WriteNumber("prompt_tokens", 100);
            json.WriteNumber("completion_tokens", 40);
            json.WriteNumber("total_tokens", 140);
            json.WriteEndObject();
        });
    }

    private void Problem(string text)
    {
        lock (_gate) { RequestsLookRight = false; if (!_problems.Contains(text)) _problems.Add(text); }
    }

    private static async Task WriteAsync(HttpListenerContext context, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    private static float[] Letters(string text)
    {
        var v = new float[Dimensions];
        foreach (var c in text.ToLowerInvariant())
            if (c is >= 'a' and <= 'z') v[(c - 'a') % Dimensions] += 1f;
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        else v[0] = 1f;
        return v;
    }

    [GeneratedRegex(@"(?<label>F\d+): user \| works in \| Zurich")]
    private static partial Regex StoredZurich();

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        try { await _loop; } catch (Exception) { }
    }
}
