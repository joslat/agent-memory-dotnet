using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// A model stand-in that returns a fixed, valid extraction payload after a configurable delay.
/// </summary>
/// <remarks>
/// <para>
/// The response body is <b>not</b> arbitrary text. All four LLM extractors parse the same JSON shape
/// (<c>entities</c> / <c>facts</c> / <c>preferences</c> / <c>relations</c>), and if the model returns
/// something unparseable, extraction yields nothing, persistence writes nothing, and a post-turn
/// scenario silently measures a no-op while looking perfectly healthy. Returning a valid payload is
/// what makes <c>PERF-W-02</c> measure a real default turn.
/// </para>
/// <para>
/// <paramref name="delay"/> exists so the hermetic profile can reproduce the <em>shape</em> of a remote
/// deployment deterministically. Measuring at zero latency isolates database and CPU cost; measuring at
/// a remote-like latency isolates orchestration and overlap. A change that improves only the latter is
/// an ordering win; one that improves both removed work.
/// </para>
/// </remarks>
public sealed class ScriptedChatClient : IChatClient
{
    /// <summary>
    /// Valid extraction output. Deliberately small and fixed: the point is to exercise the persistence
    /// path with a realistic item count, not to simulate a model's variability.
    /// </summary>
    public const string ExtractionPayload = """
        {
          "entities": [
            {"name": "Acme Corporation", "type": "ORGANIZATION", "confidence": 0.92},
            {"name": "Alice Martin", "type": "PERSON", "confidence": 0.95}
          ],
          "facts": [
            {"subject": "Alice Martin", "predicate": "works_at", "object": "Acme Corporation", "confidence": 0.9},
            {"subject": "Alice Martin", "predicate": "leads", "object": "platform team", "confidence": 0.85}
          ],
          "preferences": [
            {"category": "communication", "preference": "prefers concise written summaries", "confidence": 0.88}
          ],
          "relations": [
            {"source": "Alice Martin", "target": "Acme Corporation", "relationType": "WORKS_AT", "confidence": 0.9}
          ]
        }
        """;

    /// <summary>A scripted answer selected when <see cref="MatchOn"/> and, when supplied,
    /// <see cref="MatchAlsoOn"/> both appear in the prompt.</summary>
    public sealed record Rule(string MatchOn, string Payload, string? MatchAlsoOn = null);

    private readonly TimeSpan _delay;
    private readonly string _payload;
    private readonly IReadOnlyList<Rule> _rules;

    public ScriptedChatClient(TimeSpan delay, string? payload = null, IReadOnlyList<Rule>? rules = null)
    {
        _delay = delay;
        _payload = payload ?? ExtractionPayload;
        _rules = rules ?? Array.Empty<Rule>();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

        var materialized = messages as IList<ChatMessage> ?? messages.ToList();
        var payload = SelectPayload(materialized);

        // Token counts are approximated from character length rather than invented, so cost accounting
        // stays proportional to real prompt growth as scenarios change.
        var inputChars = materialized.Sum(m => m.Text?.Length ?? 0);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, payload))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputChars / 4,
                OutputTokenCount = payload.Length / 4,
            },
        };
    }

    /// <summary>
    /// Picks the scripted answer for this prompt.
    /// </summary>
    /// <remarks>
    /// Cost scenarios do not care what comes back, so they use the single default payload. Extraction
    /// <em>quality</em> scenarios do: a client that answers identically regardless of input would make
    /// every judged case extract the same facts, and the fixture would measure nothing at all. Rules
    /// key on a distinctive phrase from the case's own conversation — deterministic, and readable in the
    /// fixture, unlike a hash.
    /// </remarks>
    private string SelectPayload(IEnumerable<ChatMessage> messages)
    {
        if (_rules.Count == 0) return _payload;

        var prompt = string.Join("\n", messages.Select(m => m.Text ?? string.Empty));
        if (prompt.Contains("LAB-N1 source", StringComparison.Ordinal))
            return MultiSessionPayload(prompt, useLexicalIdentity: true, useCapacityLabels: true);
        if (prompt.Contains("LAB-X1 source", StringComparison.Ordinal))
            return MultiSessionPayload(prompt, useLexicalIdentity: true);
        if (prompt.Contains("LAB-B1 source", StringComparison.Ordinal))
            return MultiSessionPayload(prompt, useLexicalIdentity: false);

        foreach (var rule in _rules)
        {
            if (prompt.Contains(rule.MatchOn, StringComparison.OrdinalIgnoreCase) &&
                (rule.MatchAlsoOn is null ||
                 prompt.Contains(rule.MatchAlsoOn, StringComparison.OrdinalIgnoreCase)))
                return rule.Payload;
        }

        // No rule matched. Return an EMPTY extraction rather than the default payload: silently
        // substituting facts from an unrelated case would make a mis-keyed fixture case look like it
        // extracted correctly, which is the one failure mode this client must not hide.
        return EmptyPayload;
    }

    /// <summary>A well-formed response that extracts nothing.</summary>
    public const string EmptyPayload =
        """{"entities": [], "facts": [], "preferences": [], "relations": []}""";

    private static readonly string[] IntegratedLabels =
    [
        "amber", "birch", "cobalt", "dahlia", "ember", "fjord", "garnet", "harbor",
        "indigo", "juniper", "kelp", "lilac", "maple", "nectar", "onyx", "pebble",
        "quartz", "raven", "saffron", "thistle", "umber", "violet", "willow", "xenon",
        "yarrow", "zephyr", "acorn", "breeze", "cedar", "drift", "elm", "fern",
        "glacier", "hazel", "iris", "jade", "lotus", "moss", "opal", "pine",
    ];

    private const int CapacityLabelCount = 320;
    private const int CapacityEmbeddingDimensions = 384;
    private static readonly string[] CapacityLabels = CreateCapacityLabels();

    private static string[] CreateCapacityLabels()
    {
        var labels = new List<string>(CapacityLabelCount);
        var usedSlots = new HashSet<int>
        {
            LabelSlot("person"), LabelSlot("company"),
        };

        for (var candidate = 0; labels.Count < CapacityLabelCount; candidate++)
        {
            var label = PseudoWord(candidate);
            if (usedSlots.Add(LabelSlot(label)))
                labels.Add(label);
        }

        return labels.ToArray();
    }

    private static string PseudoWord(int value)
    {
        Span<char> characters = stackalloc char[12];
        var state = unchecked((uint)value * 747_796_405u + 2_891_336_453u);
        for (var index = 0; index < characters.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            characters[index] = (char)('a' + state % 26);
        }
        // Prevent the conservative harness stemmer from trimming a generated suffix.
        characters[^1] = 'q';
        return new string(characters);
    }

    private static int LabelSlot(string text)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var character in text)
        {
            hash ^= character;
            hash *= prime;
        }
        return (int)(hash % CapacityEmbeddingDimensions);
    }

    /// <summary>
    /// One batched source session as the provider sees it: an opaque request-local alias key plus the
    /// session's own transcript.
    /// </summary>
    /// <remarks>
    /// Under <c>batch-source-alias-schema-v1</c> the key is a deterministic short alias (<c>s1</c>…
    /// <c>sN</c>) that the product maps back to the immutable real session id after the response, so it
    /// carries no session identity at all. A stand-in must therefore recover identity the way a real
    /// model does — by reading the block's own content — and echo the alias back unchanged.
    /// </remarks>
    private static readonly Regex SourceSessionBlock = new(
        "<source_session key=\"([^\"]+)\">(.*?)</source_session>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SourceUnitMarker = new(
        @"LAB-[A-Z0-9]+ source (\d+)",
        RegexOptions.Compiled);

    private static string MultiSessionPayload(
        string prompt, bool useLexicalIdentity, bool useCapacityLabels = false)
    {
        var blocks = SourceSessionBlock.Matches(prompt)
            .Select(match => (Key: match.Groups[1].Value, Body: match.Groups[2].Value))
            .DistinctBy(block => block.Key, StringComparer.Ordinal)
            .ToArray();
        if (blocks.Length == 0)
            throw new InvalidOperationException(
                "Multi-session stand-in found no <source_session> block; returning an empty payload " +
                "would measure a no-op that looks healthy.");

        var keys = blocks.Select(block => block.Key).ToArray();
        var identities = blocks.ToDictionary(
            block => block.Key,
            block =>
            {
                var marker = SourceUnitMarker.Match(block.Body);
                if (!marker.Success)
                    throw new InvalidOperationException(
                        "Multi-session stand-in could not recover a source ordinal from a batched " +
                        "session body; the laboratory fixture and alias contract disagree.");
                var digits = marker.Groups[1].Value;
                if (!useLexicalIdentity)
                    return digits;
                var index = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
                var labels = useCapacityLabels ? CapacityLabels : IntegratedLabels;
                return index < labels.Length
                    ? labels[index]
                    : throw new InvalidOperationException("Integrated capacity label range exceeded.");
            },
            StringComparer.Ordinal);
        string Identity(string key) => identities[key];

        return JsonSerializer.Serialize(new
        {
            processed_source_sessions = keys,
            entities = keys.SelectMany(key =>
            {
                var identity = Identity(key);
                return new[]
                {
                    new { source_session = key, name = $"Person {identity}", type = "PERSON", confidence = 0.95 },
                    new { source_session = key, name = $"Company {identity}", type = "ORGANIZATION", confidence = 0.95 },
                };
            }),
            facts = keys.Select(key =>
            {
                var identity = Identity(key);
                return new
                {
                    source_session = key,
                    subject = $"Person {identity}",
                    predicate = "works_at",
                    @object = $"Company {identity}",
                    confidence = 0.9,
                };
            }),
            preferences = keys.Select(key => new
            {
                source_session = key,
                category = "drink",
                preference = useLexicalIdentity ? $"prefers {Identity(key)} tea" : "prefers tea",
                confidence = 0.9,
            }),
            relations = keys.Select(key =>
            {
                var identity = Identity(key);
                return new
                {
                    source_session = key,
                    source = $"Person {identity}",
                    target = $"Company {identity}",
                    relation_type = "WORKS_AT",
                    confidence = 0.9,
                };
            }),
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
