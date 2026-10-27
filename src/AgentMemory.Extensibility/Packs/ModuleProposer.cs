using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using Microsoft.Extensions.AI;

namespace AgentMemory.Extensibility.Packs;

/// <summary>A document the proposer reads: its repository path and its text.</summary>
[Experimental("AMEXT001")]
public sealed record ProposerDocument(string Path, string Text);

/// <summary>How the proposer reads (CMP-9).</summary>
[Experimental("AMEXT001")]
public sealed record ProposerOptions
{
    /// <summary>The types it may propose; empty means every type the module declares.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>Who proposes, as the review set names it: <c>model:&lt;name&gt;</c>.</summary>
    public string ProposedBy { get; init; } = "model";

    /// <summary>The most document text in one call, in characters.</summary>
    public int MaximumChunkCharacters { get; init; } = 20_000;

    /// <summary>The most proposals one reply may carry; the rest are dropped.</summary>
    public int MaximumProposalsPerCall { get; init; } = 12;

    /// <summary>The most calls one run makes; chunks past it are not read, and the run says so.</summary>
    public int MaximumCalls { get; init; } = 40;

    /// <summary>The most output tokens a call may use, reasoning included.</summary>
    public int MaximumOutputTokens { get; init; } = 8000;

    /// <summary>
    /// The reasoning effort asked of the model; low by default, since a reasoning model otherwise spends most of its output
    /// thinking (GLM-5.3-Flash, measured on extraction: 55–60%) and may run out before the JSON.
    /// </summary>
    public ReasoningEffort? Reasoning { get; init; } = ReasoningEffort.Low;

    /// <summary>
    /// What counts as a record of these types and what never does, in the pack's own words; appended to the instructions. A
    /// model without it reads every description of current behaviour as a requirement (CMP-9, first run on this repository).
    /// </summary>
    public string? Guidance { get; init; }

    /// <summary>Called with each reply as it came, for a run's record; never with the prompt.</summary>
    public Action<int, string>? OnReply { get; init; }
}

/// <summary>What one proposer run produced: the proposals that held, and why the others were dropped.</summary>
[Experimental("AMEXT001")]
public sealed record ProposerRun
{
    /// <summary>Proposals whose types, properties and evidence hold, as pending review items.</summary>
    public required IReadOnlyList<ReviewItem> Proposed { get; init; }

    /// <summary>Each dropped proposal with its reason.</summary>
    public required IReadOnlyList<string> Dropped { get; init; }

    /// <summary>Calls made.</summary>
    public int Calls { get; init; }

    /// <summary>Chunks not read because the call budget ran out.</summary>
    public int ChunksSkipped { get; init; }

    /// <summary>Input tokens the provider reported, when it reported them.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output tokens the provider reported, when it reported them.</summary>
    public long OutputTokens { get; init; }
}

/// <summary>
/// The onboarding proposer (CMP-9): a model reads a project's documents and proposes records of a module's types, each citing
/// the lines it read. A proposal is kept only when its type and properties are the module's own and every quote is found in
/// the lines it cites; anything else is dropped with its reason. What is kept is pending: a person decides (see
/// <see cref="ModuleReview"/>). The proposer never writes to a store.
/// </summary>
[Experimental("AMEXT001")]
public static class ModuleProposer
{
    /// <summary>The chunks a run would send, each as its system and user prompt: what a dry run prints and costs.</summary>
    public static IReadOnlyList<(string System, string User)> Prompts(ModuleManifest module, IReadOnlyList<ProposerDocument> documents,
        ModuleSnapshot? deterministic, ProposerOptions options)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(options);
        var system = System(module, deterministic, options);
        return [.. Chunks(documents, options.MaximumChunkCharacters).Select(chunk => (system, chunk))];
    }

    /// <summary>Runs the proposer over the documents, at most <see cref="ProposerOptions.MaximumCalls"/> calls.</summary>
    /// <param name="client">The model.</param>
    /// <param name="module">The module whose types are proposed.</param>
    /// <param name="documents">What it reads.</param>
    /// <param name="deterministic">The deterministic snapshot, so its records are not proposed again.</param>
    /// <param name="options">How it reads.</param>
    /// <param name="readFile">A file's text by repository path, to check the evidence.</param>
    /// <param name="now">When the proposals are made.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<ProposerRun> ProposeAsync(IChatClient client, ModuleManifest module, IReadOnlyList<ProposerDocument> documents,
        ModuleSnapshot? deterministic, ProposerOptions options, Func<string, string?> readFile, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(readFile);
        var prompts = Prompts(module, documents, deterministic, options);
        var proposed = new List<ReviewItem>();
        var dropped = new List<string>();
        var held = new HashSet<(string, string)>((deterministic?.Records ?? []).Select(r => (r.Type, r.Key)));
        long input = 0, output = 0;
        var calls = 0;
        foreach (var (system, user) in prompts.Take(options.MaximumCalls))
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)],
                new ChatOptions
                {
                    Temperature = 0, MaxOutputTokens = options.MaximumOutputTokens,
                    Reasoning = options.Reasoning is { } effort ? new ReasoningOptions { Effort = effort } : null,
                }, cancellationToken).ConfigureAwait(false);
            calls++;
            options.OnReply?.Invoke(calls, response.Text);
            input += response.Usage?.InputTokenCount ?? 0;
            output += response.Usage?.OutputTokenCount ?? 0;
            var (kept, refused) = ReadReply(response.Text, module, deterministic, options, readFile, now);
            dropped.AddRange(refused);
            foreach (var item in kept)
                if (held.Add((item.Type, item.Key))) proposed.Add(item);
                else dropped.Add($"{item.Type}:{item.Key}: already held or proposed.");
        }
        return new ProposerRun
        {
            Proposed = proposed.AsReadOnly(), Dropped = dropped.AsReadOnly(), Calls = calls,
            ChunksSkipped = Math.Max(0, prompts.Count - options.MaximumCalls), InputTokens = input, OutputTokens = output,
        };
    }

    /// <summary>
    /// Reads one reply: <c>{"records": [{"type", "key", "properties", "evidence": [{"file", "line", "endLine", "quote"}],
    /// "confidence"}]}</c>, also inside a code fence. Each record is checked against the module and its evidence.
    /// </summary>
    public static (IReadOnlyList<ReviewItem> Kept, IReadOnlyList<string> Dropped) ReadReply(string reply, ModuleManifest module, ModuleSnapshot? deterministic,
        ProposerOptions options, Func<string, string?> readFile, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);
        var kept = new List<ReviewItem>();
        var dropped = new List<string>();
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return (kept, ["The reply holds no JSON object."]);
        JsonDocument document;
        try { document = JsonDocument.Parse(reply[start..(end + 1)]); }
        catch (JsonException e) { return (kept, ["The reply is not JSON: " + e.Message]); }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                return (kept, ["The reply has no records array."]);
            var types = Allowed(module, options);
            foreach (var record in records.EnumerateArray().Take(options.MaximumProposalsPerCall))
            {
                var (item, problem) = Item(record, types, options, now);
                if (item is null) { dropped.Add(problem!); continue; }
                var evidence = ModuleReview.CheckEvidence(item, readFile);
                if (evidence.Count > 0) { dropped.Add(evidence[0]); continue; }
                // The module's own rules, as an import would apply them.
                var trial = new ModuleSnapshot
                {
                    Module = module, Dependencies = deterministic?.Dependencies ?? [], Source = "review",
                    Records = [new ModuleProposal { Type = item.Type, Key = item.Key, Properties = item.Properties, Source = new ProposalSource { Excerpt = "review" } }],
                };
                if (trial.Problems() is { Count: > 0 } problems) { dropped.Add($"{item.Type}:{item.Key}: {problems[0]}"); continue; }
                kept.Add(item);
            }
            if (records.GetArrayLength() > options.MaximumProposalsPerCall)
                dropped.Add($"{records.GetArrayLength() - options.MaximumProposalsPerCall} proposals past the {options.MaximumProposalsPerCall} a reply may carry.");
        }
        return (kept, dropped);
    }

    private static Dictionary<string, ModuleType> Allowed(ModuleManifest module, ProposerOptions options) =>
        (module.Ontology?.Types ?? []).Where(t => options.Types.Count == 0 || options.Types.Contains(t.Name, StringComparer.Ordinal))
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

    private static (ReviewItem? Item, string? Problem) Item(JsonElement record, Dictionary<string, ModuleType> types, ProposerOptions options, DateTimeOffset now)
    {
        if (record.ValueKind != JsonValueKind.Object) return (null, "A proposal that is not an object.");
        var type = Text(record, "type");
        var key = Text(record, "key")?.Trim();
        if (type is null || !types.TryGetValue(type, out var declared)) return (null, $"{type ?? "?"}:{key ?? "?"}: not a type this run may propose.");
        if (string.IsNullOrEmpty(key) || key.Length > 200 || key.Any(char.IsControl)) return (null, $"{type}:{key ?? "?"}: no usable key.");
        if (!record.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return (null, $"{type}:{key}: no properties.");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            // Locations come from the cited lines below, whatever the model says.
            if (Located.Contains(property.Name)) continue;
            if (!declared.Properties.TryGetValue(property.Name, out var rule)) return (null, $"{type}:{key}: '{property.Name}' is not a property of {type}.");
            if (property.Value.ValueKind == JsonValueKind.Null) continue;
            if (rule.Type == "string" && property.Value.ValueKind == JsonValueKind.String && rule.MaxLength is { } max && property.Value.GetString()!.Length > max)
                values[property.Name] = JsonSerializer.SerializeToElement(property.Value.GetString()![..max]);
            else values[property.Name] = property.Value.Clone();
        }
        if (!record.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() == 0)
            return (null, $"{type}:{key}: cites no lines.");
        var cited = new List<ReviewEvidence>();
        foreach (var line in evidence.EnumerateArray().Take(4))
        {
            if (Text(line, "file") is not { } file || !line.TryGetProperty("line", out var first) || !first.TryGetInt32(out var from)
                || Text(line, "quote") is not { } quote)
                return (null, $"{type}:{key}: malformed evidence.");
            var to = line.TryGetProperty("endLine", out var last) && last.TryGetInt32(out var e) ? e : from;
            cited.Add(new ReviewEvidence(file.Replace('\\', '/').TrimStart('/'), from, to, quote));
        }
        // Where a record comes from is the lines it cites, not the model's say.
        foreach (var (name, located) in Location(cited[0]))
            if (declared.Properties.TryGetValue(name, out var rule) && rule.Type == (located is int ? "int" : "string"))
                values[name] = JsonSerializer.SerializeToElement(located);
        var confidence = record.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var value) ? Math.Clamp(value, 0, 1) : 0;
        return (new ReviewItem
        {
            Type = type, Key = key, Properties = values.AsReadOnly(), Evidence = cited.AsReadOnly(), Confidence = confidence,
            ProposedBy = options.ProposedBy, ProposedAt = now, State = ReviewState.Pending,
        }, null);
    }

    private static string System(ModuleManifest module, ModuleSnapshot? deterministic, ProposerOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"You read a software project's documents and propose records for the memory module {module.Id}.");
        builder.AppendLine("Propose only what the text states. Do not infer, generalise or invent. Most passages hold nothing to propose; then return no records.");
        builder.AppendLine("A record is worth proposing only when a person maintaining this project would want it kept: prefer a few certain records to many likely ones.");
        builder.AppendLine("Every record cites the lines it rests on: the file path and line numbers exactly as shown before each line, and a verbatim quote of at least eight characters from those lines.");
        builder.AppendLine("Give each record a short, stable key in lowercase words joined by hyphens, and a confidence from 0 to 1 that the text states it as such a record: 0.9 only when the wording itself says so, 0.5 when it is your reading.");
        if (options.Guidance is { Length: > 0 } guidance)
        {
            builder.AppendLine();
            builder.AppendLine(guidance.Trim());
        }
        builder.AppendLine();
        builder.AppendLine("Types you may propose:");
        foreach (var type in Allowed(module, options).Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {type.Name}{(type.Description is { } d ? ": " + d : "")}");
            foreach (var (name, rule) in type.Properties.Where(p => !Located.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
                builder.AppendLine($"    {name} ({rule.Type}{(rule.Required ? ", required" : "")}{(rule.Values.Count > 0 ? ", one of " + string.Join("|", rule.Values) : "")}{(rule.MaxLength is { } m ? ", at most " + m.ToString(CultureInfo.InvariantCulture) + " characters" : "")}){(rule.Description is { } pd ? ": " + pd : "")}");
        }
        var known = (deterministic?.Records ?? []).Where(r => options.Types.Count == 0 || options.Types.Contains(r.Type, StringComparer.Ordinal)).Take(200).ToList();
        if (known.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Already recorded; do not propose these again:");
            foreach (var record in known)
                builder.AppendLine($"- {record.Type} {record.Key}{(record.Properties.TryGetValue("title", out var title) && title.ValueKind == JsonValueKind.String ? ": " + title.GetString() : "")}");
        }
        builder.AppendLine();
        builder.AppendLine("Answer with JSON only: {\"records\": [{\"type\": \"...\", \"key\": \"...\", \"properties\": {...}, \"evidence\": [{\"file\": \"...\", \"line\": 1, \"endLine\": 2, \"quote\": \"...\"}], \"confidence\": 0.8}]}");
        builder.AppendLine($"At most {options.MaximumProposalsPerCall} records.");
        return builder.ToString();
    }

    // Documents as numbered lines, cut at line boundaries into chunks of at most the given size; a longer line is its own chunk, clipped.
    private static IEnumerable<string> Chunks(IReadOnlyList<ProposerDocument> documents, int size)
    {
        var chunk = new StringBuilder();
        foreach (var document in documents.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            var lines = document.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                var line = $"{document.Path}:{i + 1}| {lines[i]}\n";
                if (line.Length > size) line = line[..size];
                if (chunk.Length + line.Length > size && chunk.Length > 0) { yield return chunk.ToString(); chunk.Clear(); }
                chunk.Append(line);
            }
        }
        if (chunk.Length > 0) yield return chunk.ToString();
    }

    // Properties filled from the cited lines, never asked of the model.
    private static readonly HashSet<string> Located = new(StringComparer.Ordinal) { "source", "file", "line" };

    private static IEnumerable<(string Name, object Value)> Location(ReviewEvidence evidence) =>
        [("source", $"{evidence.File}:{evidence.Line}"), ("file", evidence.File), ("line", evidence.Line)];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
