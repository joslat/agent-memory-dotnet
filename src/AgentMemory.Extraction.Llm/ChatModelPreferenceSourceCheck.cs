using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.Llm.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// The preference source check (AMDREAM001, <see cref="ConsolidationOptions.CloseUnsaidPreferences"/>) on the host's chat
/// model: Dreaming round 1's P3q, its question word for word as the router arena measured it (<c>dream_passes.SAID3_SYSTEM</c>,
/// the person's name given as "the person"). A preference the message says, or says in part, is kept; only one nothing in
/// the message says is returned. An answer without JSON, or a preference it leaves out, closes nothing.
/// </summary>
internal sealed class ChatModelPreferenceSourceCheck : IPreferenceSourceCheck
{
    internal const string Person = "the person";

    internal static readonly string SystemPrompt =
        $"A personal assistant stored memories about {Person} from one message {Person} sent on {{0}} (a fact as subject |\n"
        + $"predicate | object, where \"user\" is {Person}; a preference with its category; a connection as source -[TYPE]-> target;\n"
        + "an entity as name (type)). For each stored memory decide whether the message gives it.\n"
        + "\"said\": the message says it or plainly means it, in any words and in any kind. All of these count as said: a relative\n"
        + "date or time resolved against the message's date (\"Friday\" stored as \"Friday 20 November 2026\"); a person the message\n"
        + "names by role or pronoun stored by name; a person, place or thing the message names; a connection, plan or event the\n"
        + "message states; a fact stored as a preference.\n"
        + "\"partly\": part of it is said and part is added (a \"favourite\", a reason, a feeling around something the message does\n"
        + "say): the said part matters, so it is kept;\n"
        + "\"guess\": nothing in it is said by the message: a trait, value, feeling, habit or liking read into what " + Person + " said, a\n"
        + "generalisation from one remark, someone else's preference or situation stored as " + Person + "'s, or a claim the message\n"
        + "does not make.\n"
        + "Answer with JSON only: {{\"M1\": \"said\", \"M2\": \"partly\", \"M3\": \"guess\", ...}} with one entry for every stored memory.";

    private readonly LlmExtractionRunner _runner;

    public ChatModelPreferenceSourceCheck(IChatClient chatClient, IOptions<LlmExtractionOptions> options, ILogger<ChatModelPreferenceSourceCheck> logger)
    {
        _runner = new LlmExtractionRunner(chatClient, options.Value, logger);
    }

    public async Task<IReadOnlyCollection<string>> FindUnsaidAsync(PreferenceSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Preferences.Count == 0) return [];
        var response = await _runner.CompleteAsync(Messages(request), 3000, json: false, cancellationToken).ConfigureAwait(false);
        return Parse(response.Text, request.Preferences);
    }

    internal static IReadOnlyList<ChatMessage> Messages(PreferenceSourceRequest request)
    {
        var system = string.Format(CultureInfo.InvariantCulture, SystemPrompt, request.SaidAt.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture));
        var earlier = string.Concat(request.Earlier.Select(e => $"EARLIER: {Person}: {e}\n"));
        var lines = string.Join("\n", request.Preferences.Select((p, i) =>
            $"M{i + 1}: preference: {(string.IsNullOrWhiteSpace(p.Category) ? p.Text : $"{p.Text} ({p.Category})")}"));
        var user = $"{earlier}MESSAGE: {Person}: {request.Message}\n\nSTORED:\n{lines}";
        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)];
    }

    /// <summary>The keys of the preferences answered "guess"; anything else, or no readable answer, keeps a preference.</summary>
    internal static IReadOnlyCollection<string> Parse(string? text, IReadOnlyList<PreferenceSourceItem> preferences)
    {
        var unsaid = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return unsaid;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return unsaid;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            for (var i = 0; i < preferences.Count; i++)
            {
                if (doc.RootElement.TryGetProperty($"M{i + 1}", out var v) && v.ValueKind == JsonValueKind.String
                    && string.Equals(v.GetString()?.Trim(), "guess", StringComparison.OrdinalIgnoreCase))
                {
                    unsaid.Add(preferences[i].Key);
                }
            }
        }
        catch (JsonException)
        {
        }
        return unsaid;
    }
}
