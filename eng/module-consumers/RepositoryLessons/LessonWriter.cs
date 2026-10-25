using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Ingestion;
using Microsoft.Extensions.AI;

namespace AgentMemory.Examples.RepositoryLessons;

/// <summary>
/// The model-backed writer: one call a turn, after the core write. It sees the lessons already kept
/// (<see cref="IngestionRequest.Existing"/>) and what the core writer stored (<see cref="IngestionRequest.CoreOutcome"/>),
/// and proposes new or changed lessons, naming the ones a new lesson replaces. Its retention policy is this module's own.
/// </summary>
public sealed partial class LessonWriter(IChatClient chat, RepositoryLessonsOptions? options = null) : IIngestionProvider
{
    private readonly RepositoryLessonsOptions _options = options ?? new RepositoryLessonsOptions();

    /// <inheritdoc/>
    public IngestionProviderDescriptor Descriptor { get; } = new()
    {
        Id = RepositoryLessonsModule.Id + ":writer", ExistingView = RepositoryLessonsModule.CurrentView,
        MaxExisting = 30, MaxExistingUtf8Bytes = 12_000,
    };

    /// <summary>The retention policy the model follows.</summary>
    public const string Instructions =
        "You keep a software repository's lessons: what a future task in this repository needs to know and cannot easily "
        + "rediscover. Keep a lesson when the conversation shows one of these:\n"
        + "- a failure and what resolved it: the failure signature (error text or symptom) and the fix;\n"
        + "- a convention or constraint of this repository: how it builds, tests, is configured or named;\n"
        + "- a pitfall: an approach that was tried and did not work here, and why.\n"
        + "Mark verified true only when the conversation shows the fix or convention was checked (a test or build passed, "
        + "a command succeeded); a plan or a guess is not verified.\n"
        + "Keep nothing for general programming knowledge, facts about the people, the request itself, or what the existing "
        + "lessons already say. Write each lesson as one self-contained sentence a newcomer can act on, naming the failure "
        + "and the fix when there are both.\n"
        + "When the conversation changes or corrects an existing lesson, write it again under the same key. When a new "
        + "lesson makes an existing lesson under another key wrong or obsolete, give that key in replaces.\n"
        + "Answer with JSON only: {\"lessons\": [{\"key\": \"short-kebab-key\", \"lesson\": \"...\", \"failure\": \"...\", "
        + "\"fix\": \"...\", \"repository\": \"...\", \"version\": \"...\", \"verified\": true, \"replaces\": [\"key\"]}]}. "
        + "Leave out fields you do not know. Answer {\"lessons\": []} when nothing is worth keeping.";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Messages.Any(m => m.Role == "user")) return [];
        var response = await chat.GetResponseAsync(Prompt(request, _options.Repository),
            new ChatOptions { Temperature = 0, MaxOutputTokens = 6_000 }, cancellationToken).ConfigureAwait(false);
        if (response.FinishReason == ChatFinishReason.Length)
            throw new InvalidOperationException("The lesson writer's reply was cut off at its output limit.");
        return Parse(response.Text, request, _options);
    }

    /// <summary>The call: the instructions, then the repository, the kept lessons, the core write and the conversation.</summary>
    public static IReadOnlyList<ChatMessage> Prompt(IngestionRequest request, string? repository)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = new StringBuilder();
        text.Append("Repository: ").Append(repository ?? "not given; use the name the conversation uses, or \"unknown\"").Append("\n\n");
        text.Append("Lessons already kept (key: lesson):\n");
        if (request.Existing.Count == 0) text.Append("(none)\n");
        foreach (var item in request.Existing) text.Append(item.Record.Key).Append(": ").Append(item.Text).Append('\n');
        if (request.CoreOutcome is { Stored.Count: > 0 } core)
        {
            text.Append("\nThe personal memory already stored this turn (do not restate):\n");
            foreach (var stored in core.Stored) text.Append("- ").Append(stored.Text).Append('\n');
        }
        text.Append("\nThe conversation:\n");
        foreach (var message in request.Messages) text.Append(message.Role).Append(": ").Append(message.Text).Append('\n');
        return [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, text.ToString())];
    }

    /// <summary>
    /// The proposals in a reply. A lesson with an invalid key or no text is left out; a replaced key the writer was not
    /// shown is dropped; the configured repository wins over the model's. A reply that is not the expected JSON is an error.
    /// </summary>
    /// <exception cref="FormatException">The reply holds no lessons object.</exception>
    public static IReadOnlyList<ModuleProposal> Parse(string? reply, IngestionRequest request, RepositoryLessonsOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var start = reply?.IndexOf('{') ?? -1;
        var end = reply?.LastIndexOf('}') ?? -1;
        if (reply is null || start < 0 || end <= start) throw new FormatException("The lesson writer's reply holds no JSON object.");
        JsonDocument document;
        try { document = JsonDocument.Parse(reply[start..(end + 1)], new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException exception) { throw new FormatException("The lesson writer's reply is not valid JSON.", exception); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("lessons", out var lessons)
                || lessons.ValueKind != JsonValueKind.Array)
                throw new FormatException("The lesson writer's reply has no lessons array.");
            var shown = request.Existing.Where(e => e.Record.Type == RepositoryLessonsModule.Type).Select(e => e.Record.Key)
                .ToHashSet(StringComparer.Ordinal);
            var source = Source(request);
            var proposals = new List<ModuleProposal>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lesson in lessons.EnumerateArray())
            {
                if (proposals.Count == options.MaxLessonsPerTurn) break;
                if (lesson.ValueKind != JsonValueKind.Object) continue;
                var key = Text(lesson, "key");
                var said = Text(lesson, "lesson");
                if (key is null || !KeyPattern().IsMatch(key) || said is null || !keys.Add(key)) continue;
                var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["lesson"] = Value(Bounded(said, 400)),
                    ["repository"] = Value(Bounded(options.Repository ?? Text(lesson, "repository") ?? "unknown", 200)),
                    ["verified"] = JsonSerializer.SerializeToElement(
                        lesson.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True),
                };
                foreach (var (name, limit) in new[] { ("version", 100), ("failure", 400), ("fix", 400) })
                    if (Text(lesson, name) is { } value) properties[name] = Value(Bounded(value, limit));
                var replaces = lesson.TryGetProperty("replaces", out var named) && named.ValueKind == JsonValueKind.Array
                    ? named.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!)
                        .Where(k => k != key && shown.Contains(k)).Distinct(StringComparer.Ordinal).Take(8).ToArray()
                    : [];
                proposals.Add(new()
                {
                    Type = RepositoryLessonsModule.Type, Key = key, Properties = properties.AsReadOnly(), Replaces = replaces,
                    Source = source,
                });
            }
            return proposals.AsReadOnly();
        }
    }

    private static ProposalSource Source(IngestionRequest request)
    {
        var excerpt = request.Messages.Last(m => m.Role == "user").Text.Trim();
        if (excerpt.Length == 0) excerpt = "(empty message)";
        return new() { MessageIds = request.Messages.Select(m => m.Id).Distinct(StringComparer.Ordinal).ToArray(), Excerpt = Bounded(excerpt, 500) };
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()!.Trim() : null;

    private static JsonElement Value(string text) => JsonSerializer.SerializeToElement(text);

    // Cut at a character boundary: a surrogate pair is never split.
    private static string Bounded(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..length];
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
