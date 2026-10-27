using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ingestion;

namespace AgentMemory.Extensibility.Packs;

/// <summary>Lines a proposal read: a file, its first and last line (1-based), and a verbatim quote from them.</summary>
[Experimental("AMEXT001")]
public sealed record ReviewEvidence(string File, int Line, int EndLine, string Quote);

/// <summary>Where a proposal stands: waiting for a person, accepted as proposed, accepted with edits, or rejected.</summary>
[Experimental("AMEXT001")]
public enum ReviewState
{
    /// <summary>Waiting for a person.</summary>
    Pending,
    /// <summary>Accepted as proposed.</summary>
    Accepted,
    /// <summary>Accepted with a person's edits.</summary>
    Edited,
    /// <summary>Rejected; never proposed again.</summary>
    Rejected,
}

/// <summary>One proposed record with the lines it read, how sure its proposer was, and what a person decided.</summary>
[Experimental("AMEXT001")]
public sealed record ReviewItem
{
    /// <summary>The record's type, a type the module declares.</summary>
    public required string Type { get; init; }

    /// <summary>The record's key.</summary>
    public required string Key { get; init; }

    /// <summary>The record's properties as proposed, or as a person edited them.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Properties { get; init; }

    /// <summary>The lines the proposal rests on; at least one.</summary>
    public required IReadOnlyList<ReviewEvidence> Evidence { get; init; }

    /// <summary>How sure the proposer was, in [0, 1]; a person reads it, nothing decides by it.</summary>
    public double Confidence { get; init; }

    /// <summary>Who proposed it: <c>model:&lt;name&gt;</c> or <c>person:&lt;name&gt;</c>.</summary>
    public required string ProposedBy { get; init; }

    /// <summary>When it was proposed.</summary>
    public required DateTimeOffset ProposedAt { get; init; }

    /// <summary>Where it stands.</summary>
    public ReviewState State { get; init; }

    /// <summary>Who decided, when it is decided.</summary>
    public string? ReviewedBy { get; init; }

    /// <summary>When it was decided.</summary>
    public DateTimeOffset? ReviewedAt { get; init; }

    /// <summary>The reviewer's note, such as why it was rejected.</summary>
    public string? Note { get; init; }

    /// <summary>Whether the record goes into the store: accepted, with or without edits.</summary>
    public bool Admitted => State is ReviewState.Accepted or ReviewState.Edited;
}

/// <summary>A module's review set: the proposals for it, in the repository beside its sources, so git keeps their history.</summary>
[Experimental("AMEXT001")]
public sealed record ReviewSet
{
    /// <summary>The module the proposals are for.</summary>
    public required string Module { get; init; }

    /// <summary>The proposals, in (type, key) order.</summary>
    public IReadOnlyList<ReviewItem> Items { get; init; } = [];
}

/// <summary>
/// Onboarding an existing project (CMP-9): a deterministic import reads what is certain; what a model infers from the
/// project's documents enters a review set as a proposal with the lines it read and a confidence, and waits for a person.
/// Accepted proposals join the module's snapshot at the next import, so the store holds one snapshot per module and a
/// rejected proposal is remembered and never proposed again. Nothing is guessed into the store.
/// </summary>
/// <remarks>
/// The review set is a file (<c>agentmemory.review/1</c>) written in a stable order, so a review is a reviewable diff.
/// A deterministic record always wins over a proposal with the same key, and that conflict is reported.
/// </remarks>
[Experimental("AMEXT001")]
public static class ModuleReview
{
    /// <summary>The review set's format.</summary>
    public const string Format = "agentmemory.review/1";

    /// <summary>Where a module's review set lives in a repository by default.</summary>
    public static string DefaultPath(string moduleId) => $"docs/memory/review/{moduleId}.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Reads a review set; a malformed one is refused, never read as empty.</summary>
    /// <exception cref="FormatException">The text is not a review set.</exception>
    public static ReviewSet Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != Format || Text(root, "module") is not { } module
            || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new FormatException($"Not a review set ({Format}).");
        return new ReviewSet { Module = module, Items = Ordered(items.EnumerateArray().Select(Item)) };
    }

    /// <summary>Writes a review set: indented, items in (type, key) order, line feeds only.</summary>
    public static string Write(ReviewSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var items = new JsonArray();
        foreach (var item in Ordered(set.Items))
        {
            var node = new JsonObject
            {
                ["type"] = item.Type, ["key"] = item.Key,
                ["properties"] = new JsonObject(item.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Key, JsonNode.Parse(p.Value.GetRawText())))),
                ["evidence"] = new JsonArray([.. item.Evidence.Select(e => (JsonNode)new JsonObject
                {
                    ["file"] = e.File, ["line"] = e.Line, ["endLine"] = e.EndLine, ["quote"] = e.Quote,
                })]),
                ["confidence"] = Math.Round(item.Confidence, 2),
                ["proposedBy"] = item.ProposedBy, ["proposedAt"] = Time(item.ProposedAt),
                ["state"] = item.State.ToString().ToLowerInvariant(),
            };
            if (item.ReviewedBy is { } by) node["reviewedBy"] = by;
            if (item.ReviewedAt is { } at) node["reviewedAt"] = Time(at);
            if (item.Note is { } note) node["note"] = note;
            items.Add(node);
        }
        var root = new JsonObject { ["format"] = Format, ["module"] = set.Module, ["items"] = items };
        return root.ToJsonString(Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>
    /// Adds new proposals as pending. A proposal is left out when the deterministic snapshot already holds its key, or the set
    /// already has it in any state: a rejected key stays rejected, a pending or decided one is not proposed twice.
    /// </summary>
    /// <returns>The set with the new proposals, and how many were added and left out.</returns>
    public static (ReviewSet Set, int Added, int LeftOut) Propose(ReviewSet set, IEnumerable<ReviewItem> proposals, ModuleSnapshot? deterministic = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(proposals);
        var held = new HashSet<(string, string)>(set.Items.Select(i => (i.Type, i.Key)));
        foreach (var record in deterministic?.Records ?? []) held.Add((record.Type, record.Key));
        var added = new List<ReviewItem>();
        var leftOut = 0;
        foreach (var proposal in proposals)
            if (held.Add((proposal.Type, proposal.Key))) added.Add(proposal with { State = ReviewState.Pending, ReviewedBy = null, ReviewedAt = null, Note = null });
            else leftOut++;
        return (set with { Items = Ordered(set.Items.Concat(added)) }, added.Count, leftOut);
    }

    /// <summary>
    /// Decides proposals: accepts, rejects, or (with <paramref name="edits"/>) accepts with changed properties. Each key is a
    /// record key, or <c>Type:key</c> where two types share one; a key the set does not hold is an error, so a typo decides nothing.
    /// </summary>
    /// <exception cref="ArgumentException">A key names no proposal, or names more than one.</exception>
    public static ReviewSet Decide(ReviewSet set, IEnumerable<string> keys, bool accept, string reviewer, DateTimeOffset at, string? note = null,
        IReadOnlyDictionary<string, JsonElement>? edits = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);
        var items = set.Items.ToList();
        foreach (var key in keys)
        {
            var matches = Enumerable.Range(0, items.Count).Where(i => Names(items[i], key)).ToList();
            if (matches.Count != 1)
                throw new ArgumentException(matches.Count == 0 ? $"No proposal '{key}' in the review set." : $"'{key}' names {matches.Count} proposals; use Type:key.", nameof(keys));
            var item = items[matches[0]];
            var properties = item.Properties;
            if (accept && edits is { Count: > 0 })
            {
                var changed = new Dictionary<string, JsonElement>(item.Properties, StringComparer.Ordinal);
                foreach (var (name, value) in edits)
                    if (value.ValueKind == JsonValueKind.Null) changed.Remove(name);
                    else changed[name] = value.Clone();
                properties = changed.AsReadOnly();
            }
            items[matches[0]] = item with
            {
                Properties = properties,
                State = !accept ? ReviewState.Rejected : edits is { Count: > 0 } || item.State == ReviewState.Edited ? ReviewState.Edited : ReviewState.Accepted,
                ReviewedBy = reviewer, ReviewedAt = at, Note = note,
            };
        }
        return set with { Items = Ordered(items) };
    }

    /// <summary>
    /// The deterministic snapshot with the accepted proposals added, each carrying who accepted it and the lines it rests on.
    /// A proposal whose key the snapshot already holds is left out and named in <paramref name="conflicts"/>: what is certain wins.
    /// </summary>
    /// <exception cref="ArgumentException">The review set is for another module.</exception>
    public static ModuleSnapshot Merge(ModuleSnapshot deterministic, ReviewSet? set, out IReadOnlyList<string> conflicts)
    {
        ArgumentNullException.ThrowIfNull(deterministic);
        conflicts = [];
        if (set is null || set.Items.All(i => !i.Admitted)) return deterministic;
        if (set.Module != deterministic.Module.Id)
            throw new ArgumentException($"The review set is for {set.Module}, not {deterministic.Module.Id}.", nameof(set));
        var held = deterministic.Records.Select(r => (r.Type, r.Key)).ToHashSet();
        var clashes = new List<string>();
        var records = deterministic.Records.ToList();
        foreach (var item in Ordered(set.Items).Where(i => i.Admitted))
        {
            if (!held.Add((item.Type, item.Key))) { clashes.Add($"{item.Type}:{item.Key}"); continue; }
            var first = item.Evidence[0];
            records.Add(new ModuleProposal
            {
                Type = item.Type, Key = item.Key, Properties = item.Properties,
                Source = new ProposalSource
                {
                    Excerpt = Clip($"{(item.State == ReviewState.Edited ? "Edited and accepted" : "Accepted")} by {item.ReviewedBy} on {item.ReviewedAt:yyyy-MM-dd}; proposed by {item.ProposedBy} " +
                        $"(confidence {item.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}) from {string.Join(", ", item.Evidence.Select(e => $"{e.File}:{e.Line}-{e.EndLine}"))}", 500),
                    Pack = new PackSource { Id = deterministic.Module.Id, Version = "", Digest = "", File = first.File, Line = first.Line },
                },
            });
        }
        conflicts = clashes.AsReadOnly();
        return deterministic with { Records = records.AsReadOnly() };
    }

    /// <summary>
    /// Why a proposal's evidence does not hold, or nothing: each quote must appear, ignoring runs of white space, within its
    /// lines of a file the repository has. A model that cites lines it did not read is caught here, before a person sees it.
    /// </summary>
    /// <param name="item">The proposal.</param>
    /// <param name="readFile">A file's text by its repository path, or null when there is no such file.</param>
    public static IReadOnlyList<string> CheckEvidence(ReviewItem item, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(readFile);
        var problems = new List<string>();
        if (item.Evidence.Count == 0) problems.Add($"{item.Type}:{item.Key} cites no lines.");
        foreach (var evidence in item.Evidence)
        {
            if (readFile(evidence.File) is not { } text) { problems.Add($"{item.Type}:{item.Key} cites {evidence.File}, which the repository does not have."); continue; }
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (evidence.Line < 1 || evidence.EndLine < evidence.Line || evidence.EndLine > lines.Length || evidence.EndLine - evidence.Line > 200)
            { problems.Add($"{item.Type}:{item.Key} cites lines {evidence.Line}-{evidence.EndLine} of {evidence.File}, which has {lines.Length}."); continue; }
            var quote = Squeeze(evidence.Quote);
            if (quote.Length < 8) { problems.Add($"{item.Type}:{item.Key} quotes too little of {evidence.File} to check."); continue; }
            if (!Squeeze(string.Join(' ', lines[(evidence.Line - 1)..evidence.EndLine])).Contains(quote, StringComparison.Ordinal))
                problems.Add($"{item.Type}:{item.Key} quotes text that is not in {evidence.File}:{evidence.Line}-{evidence.EndLine}.");
        }
        return problems;
    }

    private static string Squeeze(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Trim())
            if (char.IsWhiteSpace(c)) space = true;
            else { if (space && builder.Length > 0) builder.Append(' '); builder.Append(c); space = false; }
        return builder.ToString();
    }

    private static bool Names(ReviewItem item, string key) =>
        item.Key == key || key.IndexOf(':') is var colon and > 0 && item.Type == key[..colon] && item.Key == key[(colon + 1)..];

    private static List<ReviewItem> Ordered(IEnumerable<ReviewItem> items) =>
        [.. items.OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Key, StringComparer.Ordinal)];

    private static ReviewItem Item(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || Text(element, "type") is not { } type || Text(element, "key") is not { } key
            || !element.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array
            || Text(element, "proposedBy") is not { } by || !DateTimeOffset.TryParse(Text(element, "proposedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var proposedAt)
            || !Enum.TryParse<ReviewState>(Text(element, "state"), ignoreCase: true, out var state) || !Enum.IsDefined(state))
            throw new FormatException("A review item needs type, key, properties, evidence, proposedBy, proposedAt and state.");
        return new ReviewItem
        {
            Type = type, Key = key,
            Properties = properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal).AsReadOnly(),
            Evidence = [.. evidence.EnumerateArray().Select(e => Text(e, "file") is { } file && e.TryGetProperty("line", out var line) && line.TryGetInt32(out var first)
                && e.TryGetProperty("endLine", out var end) && end.TryGetInt32(out var last) && Text(e, "quote") is { } quote
                ? new ReviewEvidence(file, first, last, quote) : throw new FormatException($"{type}:{key} has malformed evidence."))],
            Confidence = element.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var c) ? Math.Clamp(c, 0, 1) : 0,
            ProposedBy = by, ProposedAt = proposedAt, State = state,
            ReviewedBy = Text(element, "reviewedBy"),
            ReviewedAt = DateTimeOffset.TryParse(Text(element, "reviewedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reviewedAt) ? reviewedAt : null,
            Note = Text(element, "note"),
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}
