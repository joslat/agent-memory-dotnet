using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Services;

/// <summary>
/// Turns one compound query into per-memory-type legs (Proposal M, 30.10).
/// </summary>
/// <remarks>
/// A seam with two implementations rather than a single method, because the two differ in the one
/// property that matters for measurement: the deterministic deriver returns the same legs for the
/// same query forever, and the model-backed one does not. A number measured through the second cannot
/// be reproduced, so which one ran has to be visible in the witness (<c>DeriverId</c>).
/// </remarks>
internal interface ISubQueryDeriver
{
    /// <summary>A stable identifier recorded in the fan-out report: "det-v1", "llm-v1".</summary>
    string DeriverId { get; }

    /// <summary>
    /// Derives at most <paramref name="maxSubQueries"/> legs. Returns empty when the query does not
    /// decompose — an empty result is a legitimate answer, never an error.
    /// </summary>
    Task<IReadOnlyList<RecallSubQuery>> DeriveAsync(
        string query, int maxSubQueries, CancellationToken cancellationToken = default);
}

/// <summary>
/// Splits a compound query at its coordinating joiner and types each fragment by its own signals.
/// </summary>
/// <remarks>
/// <para>
/// <b>The default, because it is reproducible.</b> Free, provider-free, and testable without a model:
/// the same query yields the same legs on every run and every machine, which is what makes a
/// before/after measurement of this mechanism mean anything.
/// </para>
/// <para>
/// <b>Semantic is the residual, not a signal.</b> A fragment becomes Semantic when nothing else
/// claims it. Giving Semantic its own positive keyword set would have it fire on nearly everything —
/// the measured fire rates say the other four types are the discriminating ones, and the leftover is
/// where the general case belongs.
/// </para>
/// </remarks>
internal sealed class DeterministicSubQueryDeriver : ISubQueryDeriver
{
    /// <inheritdoc/>
    public string DeriverId => "det-v1";

    private static readonly string[] Joiners = [" and ", " as well as ", " & ", "; "];

    private static readonly string[] TemporalSignals =
        [
            "january", "february", "march", "april", "may", "june", "july", "august",
            "september", "october", "november", "december",
            "last week", "last month", "last quarter", "last year", "when",
        ];

    private static readonly string[] EpisodicSignals =
        ["i said", "you mentioned", "we talked", "we discussed", "you told me", "i told you"];

    private static readonly string[] PreferenceSignals =
        ["prefer", "like", "likes", "favorite", "favourite", "dislike", "hate"];

    private static readonly string[] ProceduralSignals =
        ["how do", "how to", "steps", "procedure", "process for", "workflow"];

    private static readonly char[] WordSeparators =
        [' ', '\t', '\r', '\n', '.', ',', '!', '?', ':', ';'];

    private static readonly HashSet<string> WhLemmas =
        new(StringComparer.OrdinalIgnoreCase)
        { "what", "when", "where", "who", "which", "how", "why" };

    /// <inheritdoc/>
    public Task<IReadOnlyList<RecallSubQuery>> DeriveAsync(
        string query, int maxSubQueries, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxSubQueries <= 0)
            return Task.FromResult<IReadOnlyList<RecallSubQuery>>([]);

        var fragments = SplitAtJoiners(query);

        // One fragment means the query did not decompose. Returning it as a single "sub-query" would
        // re-issue the monolithic query under another name and bill an extra embedding for it.
        if (fragments.Count < 2)
            return Task.FromResult<IReadOnlyList<RecallSubQuery>>([]);

        var legs = new List<RecallSubQuery>(Math.Min(fragments.Count, maxSubQueries));
        foreach (var fragment in fragments)
        {
            if (legs.Count >= maxSubQueries) break;

            var trimmed = fragment.Trim();
            if (trimmed.Length == 0) continue;

            var affinity = ClassifyAffinity(trimmed.ToLowerInvariant());
            legs.Add(new RecallSubQuery
            {
                Affinity = affinity,
                QueryText = PrepareText(trimmed, affinity),
            });
        }

        return Task.FromResult<IReadOnlyList<RecallSubQuery>>(legs);
    }

    private static List<string> SplitAtJoiners(string query)
    {
        var fragments = new List<string> { query };

        foreach (var joiner in Joiners)
        {
            var next = new List<string>();
            foreach (var fragment in fragments)
            {
                next.AddRange(fragment.Split(joiner, StringSplitOptions.RemoveEmptyEntries));
            }

            fragments = next;
        }

        return fragments;
    }

    /// <summary>First matching signal wins; Semantic is the residual.</summary>
    /// <remarks>
    /// Order is deliberate and load-bearing. Episodic precedes Preference because "you mentioned you
    /// like X" is a question about the conversation, not about the preference; putting Preference
    /// first would route it to the wrong store and the leg would return nothing.
    /// </remarks>
    private static MemoryTypeAffinity ClassifyAffinity(string lowered)
    {
        if (ContainsAny(lowered, EpisodicSignals)) return MemoryTypeAffinity.Episodic;
        if (ContainsAny(lowered, ProceduralSignals)) return MemoryTypeAffinity.Procedural;
        if (ContainsAny(lowered, TemporalSignals)) return MemoryTypeAffinity.Temporal;
        if (ContainsAny(lowered, PreferenceSignals)) return MemoryTypeAffinity.Preference;
        return MemoryTypeAffinity.Semantic;
    }

    /// <summary>
    /// Temporal fragments with no interrogative are prefixed so they read as a date question.
    /// </summary>
    /// <remarks>
    /// A bare fragment like "the Acme contract in March" embeds as a statement. The retrieval it needs
    /// is "on what date …", and supplying that prefix costs nothing while making the leg's embedding
    /// point at the question actually being asked.
    /// </remarks>
    private static string PrepareText(string fragment, MemoryTypeAffinity affinity)
    {
        if (affinity != MemoryTypeAffinity.Temporal) return fragment;

        foreach (var token in fragment.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (WhLemmas.Contains(token)) return fragment;
        }

        return "on what date " + fragment;
    }

    private static bool ContainsAny(string lowered, string[] signals)
    {
        foreach (var signal in signals)
        {
            if (lowered.Contains(signal, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}

/// <summary>
/// Asks a model to split the query. Opt-in via <see cref="RecallFanOutOptions.UseLlmDerivation"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One call, JSON only, and it never throws.</b> Every failure mode — unparseable output, an
/// unknown affinity, a provider exception — resolves to "no fan-out this turn" plus a
/// <c>VoidReason</c> the witness can carry. A silent fallback to the deterministic deriver would be
/// worse than no fan-out: the report would name a deriver that did not run.
/// </para>
/// <para>
/// Not the default. Its legs depend on a sampled output, so two runs of the same corpus can differ
/// for reasons that have nothing to do with the change being measured.
/// </para>
/// </remarks>
internal sealed class LlmSubQueryDeriver : ISubQueryDeriver
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<LlmSubQueryDeriver> _logger;

    public LlmSubQueryDeriver(IChatClient chatClient, ILogger<LlmSubQueryDeriver> logger)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public string DeriverId => "llm-v1";

    /// <summary>Raised only to the caller that knows how to record it; never past the assembler.</summary>
    internal const string FailureReason = "derivation-failed";

    private const string Instruction =
        "Split the user question into independent retrieval sub-queries, one per memory type. " +
        "Reply with JSON only: [{\"affinity\":\"Semantic|Temporal|Episodic|Preference|Procedural\"," +
        "\"text\":\"...\"}]. No prose, no code fences. Return [] if it does not decompose.";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RecallSubQuery>> DeriveAsync(
        string query, int maxSubQueries, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxSubQueries <= 0) return [];

        string? text;
        try
        {
            var response = await _chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Instruction), new ChatMessage(ChatRole.User, query)],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            text = response.Text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never a throw: a derivation failure must degrade to "no fan-out", not fail the recall
            // the caller actually asked for.
            _logger.LogWarning(exception, "LLM sub-query derivation failed; no fan-out this turn.");
            return [];
        }

        return Parse(text, maxSubQueries);
    }

    /// <summary>Parses the model's JSON, dropping anything it cannot type.</summary>
    /// <remarks>
    /// Unknown affinities are dropped rather than coerced to Semantic. Coercing would route a leg to
    /// a store the model did not choose and then report it as though the model had.
    /// </remarks>
    internal static IReadOnlyList<RecallSubQuery> Parse(string? text, int maxSubQueries)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var legs = new List<RecallSubQuery>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (legs.Count >= maxSubQueries) break;
                if (element.ValueKind != JsonValueKind.Object) continue;

                if (!element.TryGetProperty("affinity", out var affinityElement)) continue;
                if (!element.TryGetProperty("text", out var textElement)) continue;

                var affinityText = affinityElement.GetString();
                var queryText = textElement.GetString();
                if (string.IsNullOrWhiteSpace(affinityText) || string.IsNullOrWhiteSpace(queryText))
                    continue;

                if (!Enum.TryParse<MemoryTypeAffinity>(affinityText, ignoreCase: true, out var affinity))
                    continue;
                if (!Enum.IsDefined(affinity)) continue;

                legs.Add(new RecallSubQuery { Affinity = affinity, QueryText = queryText });
            }

            return legs;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
