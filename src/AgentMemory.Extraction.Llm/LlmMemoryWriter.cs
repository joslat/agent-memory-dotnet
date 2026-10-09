using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using AgentMemory.Extraction.Llm.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// The store-aware writer (AMWRITE001): one model call per turn that sees the owner's most relevant stored memories and
/// proposes only what is new (add), what changes a stored memory (replace, naming it) or what was wrong (correct, naming
/// it), or nothing. Storage round 3's selected form "F", ported from the research harness (round3.py, storage_round2.py):
/// its system prompt is the harness's, word for word. Measured in the harness on a fresh 12-session world: storage accuracy
/// 90.8%, precision 96.8%, recall 87.1%, no wrong closure in nine runs (the library's extractors 42.3% on the same setup);
/// through the library, on that world's first three sessions, 93.2% against the harness's 91.6-97.2% on the same turns.
/// </summary>
/// <remarks>
/// <para>
/// Off by default (<see cref="LlmExtractionOptions.UseMemoryWriter"/>). The stored memories are read only when the writer
/// is enabled and asked, so registering it costs a host that never turns it on nothing.
/// </para>
/// <para>
/// Where the port differs from the harness, knowingly: the stored memories it shows are the library's (a fact as
/// "subject | predicate | object", validity as [valid from/until], ids numbered per prompt: F1, P1, E1, R1); the
/// connections it shows are those of the people the turn names (connections are not searched by meaning: they carry no
/// vector); and today is the clock's date in UTC.
/// </para>
/// </remarks>
internal sealed class LlmMemoryWriter : IMemoryWriter
{
    private readonly LlmExtractionOptions _options;
    private readonly LlmExtractionRunner _runner;
    private readonly IServiceProvider _services;
    private readonly ILogger<LlmMemoryWriter> _logger;

    public LlmMemoryWriter(
        IChatClient chatClient,
        IOptions<LlmExtractionOptions> options,
        IServiceProvider services,
        ILogger<LlmMemoryWriter> logger)
    {
        _options = options.Value;
        _runner = new LlmExtractionRunner(chatClient, _options, logger);
        _services = services;
        _logger = logger;
    }

    public bool IsEnabled => _options.UseMemoryWriter;

    public async Task<UnifiedExtractionResult> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var said = request.Window.Targets.Where(m => MemoryWriterPrompt.IsUser(m.Role)).ToList();
        if (said.Count == 0)
            return new UnifiedExtractionResult();

        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.write.store_aware");
        // The write owner's own memories only; with no owner (or a write shared with everyone), the ownerless ones. Never
        // another owner's: what is read here goes into the prompt.
        var scope = SharedScopes.OwnedOrShared(SharedScopes.WriteOwner(request.Scope));
        var now = _services.GetService<IClock>()?.UtcNow ?? DateTimeOffset.UtcNow;
        var owner = await OwnerNameAsync(scope, cancellationToken).ConfigureAwait(false);
        var text = string.Join("\n", said.Select(m => m.Content));
        var stored = await StoredAsync(text, scope, now, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("memory.write.candidates", stored.Count);

        var system = MemoryWriterPrompt.System(owner, _options.MemoryWriterMaxOperations);
        var context = MemoryWriterPrompt.Context(now, request.Window, owner, stored);
        var ops = await AskAsync(system, context, cancellationToken).ConfigureAwait(false);
        var result = MemoryWriterOps.ToResult(ops, stored, owner);
        activity?.SetTag("memory.write.operations", ops.Count);
        _logger.LogInformation(
            "Memory writer: {Candidates} stored memories shown; {Ops} operation(s): {Facts} fact(s), {Preferences} preference(s), "
            + "{Entities} entity(ies), {Relationships} relationship(s); {Named} name a stored memory as replaced.",
            stored.Count, ops.Count, result.Facts.Count, result.Preferences.Count, result.Entities.Count, result.Relationships.Count,
            result.Facts.Count(f => f.ReplacesId is not null) + result.Preferences.Count(p => p.ReplacesId is not null));
        return result;
    }

    /// <summary>
    /// One call; an answer without JSON is asked again with twice the room, and once more at that room, then fails loudly
    /// (the harness's rule): a turn whose model gives no JSON stores nothing and is recorded as failed, never read as
    /// "nothing to keep". The third try (2026-10-10): over 3,634 turns of the Dreaming jar's store runs the first answer held
    /// no JSON 104 times and the second 10, each a model that reasoned through its whole room and answered nothing (12,000
    /// output tokens over the two): a runaway the next call rarely repeats, so one more call saves most of the lost turns.
    /// </summary>
    private async Task<IReadOnlyList<MemoryWriterOps.Op>> AskAsync(string system, string context, CancellationToken cancellationToken)
    {
        const int Attempts = 3;
        var room = _options.MemoryWriterMaxOutputTokens;
        string? reply = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var response = await _runner.CompleteAsync(
                [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, context)], room, json: false, cancellationToken)
                .ConfigureAwait(false);
            reply = response.Text;
            if (MemoryWriterOps.TryParse(reply, _options.MemoryWriterMaxOperations, out var ops))
                return ops;
            _logger.LogWarning("Memory writer: the reply held no JSON (attempt {Attempt} of {Attempts}, {Room} tokens of room).", attempt, Attempts, room);
            if (attempt == 1) room *= 2;
        }
        throw new FormatException($"The memory writer's model gave no JSON: {(reply is null ? "" : reply[..Math.Min(160, reply.Length)])}");
    }

    /// <summary>
    /// The owner's name once they have said it (a stored naming fact), else null: the prompt then says "the user". Only a
    /// plain name is used (letters, spaces, apostrophes, hyphens and dots, at most 60 characters): it is said by the person
    /// and goes into the system prompt.
    /// </summary>
    private async Task<string?> OwnerNameAsync(MemoryScope scope, CancellationToken cancellationToken)
    {
        if (SharedScopes.WriteOwner(scope) is null) return null;
        try
        {
            var facts = _services.GetRequiredService<IFactRepository>();
            var name = await facts.FindLatestObjectAsync(PersistenceStage.UserNames.SelfWords, PersistenceStage.UserNames.NamingPredicates,
                scope, cancellationToken).ConfigureAwait(false);
            return MemoryWriterPrompt.PlainName(name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory writer: could not read the owner's name; the prompt says \"the user\".");
            return null;
        }
    }

    /// <summary>
    /// The stored memories the writer sees, as the harness chose them (storage_round2.World.candidates): the people and
    /// things the turn names; the live memories that mention them (connections first, then facts, then preferences, oldest
    /// first: the store's order); then the most similar by meaning (<see cref="LlmExtractionOptions.MemoryWriterCandidates"/>,
    /// taken before the ones already listed are removed); at most that many plus six in all.
    /// </summary>
    private async Task<IReadOnlyList<MemoryWriterOps.Stored>> StoredAsync(
        string text, MemoryScope scope, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var facts = _services.GetRequiredService<IFactRepository>();
        var preferences = _services.GetRequiredService<IPreferenceRepository>();
        var entities = _services.GetRequiredService<IEntityRepository>();
        var relationships = _services.GetRequiredService<IRelationshipRepository>();
        var embeddings = _services.GetRequiredService<IEmbeddingOrchestrator>();
        var limit = Math.Max(1, _options.MemoryWriterCandidates);
        bool Live(Fact f) => f.InvalidatedAtUtc is null && (f.ValidUntil is null || f.ValidUntil > now);

        // 1. The people and things the turn names, found by their stored names (merged and closed ones left out).
        var named = new List<Entity>();
        foreach (var name in MemoryWriterPrompt.Names(text))
            if (await entities.FindLiveByNameAsync(name, type: null, scope, cancellationToken).ConfigureAwait(false) is { } entity
                && named.All(e => e.EntityId != entity.EntityId))
                named.Add(entity);

        // 2. The live memories that mention them, by their full names.
        var byName = new List<MemoryWriterOps.Stored>();
        if (named.Count > 0)
        {
            var names = named.Select(e => e.Name).ToList();
            var around = await relationships.GetLiveAroundAsync(
                [.. named.Select(e => e.EntityId)], names, limit * 4, now, scope, cancellationToken).ConfigureAwait(false);
            byName.AddRange(around.OrderBy(r => r.Relationship.CreatedAtUtc).Select(MemoryWriterOps.Stored.Of));
            byName.AddRange((await facts.FindMentioningAsync(names, scope, limit * 4, cancellationToken).ConfigureAwait(false))
                .Where(Live).Select(MemoryWriterOps.Stored.Of));
            byName.AddRange((await preferences.FindMentioningAsync(names, scope, limit * 4, cancellationToken).ConfigureAwait(false))
                .Where(p => p.InvalidatedAtUtc is null).Select(MemoryWriterOps.Stored.Of));
        }

        // 3. The most similar by meaning, across facts, preferences and entities: the top ones, then merged with the above.
        var vector = await embeddings.EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        var similar = new List<(double Score, MemoryWriterOps.Stored Item)>();
        foreach (var (fact, score) in await facts.SearchByVectorAsync(vector, ValidTimeMode.Current, limit * 2, 0.0, scope, cancellationToken).ConfigureAwait(false))
            if (Live(fact)) similar.Add((score, MemoryWriterOps.Stored.Of(fact)));
        foreach (var (preference, score) in await preferences.SearchByVectorAsync(vector, limit * 2, 0.0, scope, cancellationToken).ConfigureAwait(false))
            if (preference.InvalidatedAtUtc is null) similar.Add((score, MemoryWriterOps.Stored.Of(preference)));
        foreach (var (entity, score) in await entities.SearchByVectorAsync(vector, limit, 0.0, scope, cancellationToken).ConfigureAwait(false))
            similar.Add((score, MemoryWriterOps.Stored.Of(entity)));
        var byMeaning = similar.OrderByDescending(s => s.Score).Select(s => s.Item).DistinctBy(item => item.Id).Take(limit);

        return MemoryWriterOps.Stored.Number(
            [.. named.Select(MemoryWriterOps.Stored.Of).Concat(byName).Concat(byMeaning).DistinctBy(item => item.Id).Take(limit + 6)]);
    }
}

/// <summary>The writer's prompt: storage round 3's form F (round3.py, storage_round2.py prompt p2), word for word.</summary>
internal static class MemoryWriterPrompt
{
    private const string Rules =
        """
        What the assistant keeps (the labellers' rules):
        - A message stores what it tells: a fact, plan, event, preference, person or connection about {p} or {p}'s world.
        - A change names the stored memory it replaces; a correction names the stored memory that was wrong.
        - A question stores nothing, unless it also tells something ("I'm off to Seville on the 13th, what should I pack?" stores
          the trip and its date).
        - Small talk, thanks, greetings, a passing mood or reaction, the request itself and general knowledge store nothing.
        - What is already stored is not stored again, in any words.
        - A question or a request often tells something in passing: a plan ("what should I bring when I visit my cousin in Porto
          next month?"), someone's wish or need ("my neighbour wants to borrow the ladder, is it still in the shed?"), an
          appointment, a change. Keep that part as a memory; never the question or the request itself.
        """;

    private const string Writer =
        """
        You write {p}'s long-term memory of one kind: {kind_name} = {what}.
        Look only at {p}'s LAST message; earlier turns are context. {rules}
        Operations:
        - add: something new that is not stored yet (check the STORED list);
        - replace: the new value changes a stored memory that stops being true now (moved, new job, quit, changed plans): give its id;
        - correct: a stored memory was wrong all along: give its id.
        Every operation quotes the exact words of the last message it rests on. One memory per thing told; at most {max_ops}.
        Format of "text": {fmt}. For kind person, set "kind" to "entity" or "relationship" on each operation.
        Answer with JSON only: {"ops": [{"op": "add", "kind": "{kind_default}", "text": "...", "quote": "..."},
        {"op": "replace", "id": "F12", "kind": "{kind_default}", "text": "...", "quote": "..."}]} or {"ops": []}.
        """;

    private static readonly (string Kind, string What, string Format)[] Kinds =
    [
        ("fact", "a fact, plan, event, change or correction about {p} or {p}'s world (people, pets, places, work, health, money, "
                 + "plans with their dates)",
         "\"subject | predicate | object\", with {s} as the subject when it is about {p}; keep dates and names as said "
         + "(e.g. \"{s} | is travelling to | Seville on 13 October\")"),
        ("preference", "a lasting taste, like, dislike, habit, or a way {p} wants to be answered",
         "one short sentence (e.g. \"Prefers short answers with bullet points\")"),
        ("person", "someone or something newly named in {p}'s world (a person, pet, place, organisation or thing), and a connection "
                   + "ONLY when the message states how two are related (sister of, works with, owns); never infer a connection from "
                   + "context, and a mere mention of someone already stored needs nothing",
         "an entity as \"Name (PERSON|PLACE|ORGANIZATION|THING)\"; a connection as \"Name -[RELATION]-> Name\""),
    ];

    /// <summary>The system prompt for an owner known by <paramref name="owner"/> (null: "the user", stored as "user").</summary>
    internal static string System(string? owner, int maxOperations)
    {
        var p = owner ?? "the user";
        var s = owner ?? "user";
        string Fill(string template) => template.Replace("{p}", p, StringComparison.Ordinal).Replace("{s}", s, StringComparison.Ordinal);
        var what = "\n" + string.Join("\n", Kinds.Select(k => $"- {k.Kind}: {Fill(k.What)}"));
        var formats = string.Join("; ", Kinds.Select(k => $"{k.Kind}: {Fill(k.Format)}"));
        // "\n" whatever line endings this file was checked out with: the measured prompt's.
        return Writer.ReplaceLineEndings("\n")
            .Replace("{kind_name}", "every kind", StringComparison.Ordinal)
            .Replace("{what}", what, StringComparison.Ordinal)
            .Replace("{rules}", Fill(Rules.ReplaceLineEndings("\n")), StringComparison.Ordinal)
            .Replace("{max_ops}", maxOperations.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{fmt}", formats, StringComparison.Ordinal)
            .Replace("{kind_default}", "fact", StringComparison.Ordinal)
            .Replace("{p}", p, StringComparison.Ordinal)
            + "\nSet \"kind\" on every operation to fact, preference, entity or relationship.";
    }

    /// <summary>
    /// TODAY, the conversation and the stored memories with their ids. The conversation is the context turns (the person's
    /// and the assistant's), then the person's last message; the assistant's reply in the same window is not shown: what
    /// the assistant says is not written (the measured form never saw a reply after the last message).
    /// </summary>
    internal static string Context(DateTimeOffset now, ExtractionWindow window, string? owner, IReadOnlyList<MemoryWriterOps.Stored> stored)
    {
        var speaker = owner ?? "User";
        var lines = new List<string>();
        foreach (var m in window.Context)
            lines.Add($"{(IsUser(m.Role) ? speaker : "Assistant")}: {m.Content}");
        foreach (var m in window.Targets.Where(m => IsUser(m.Role)))
            lines.Add($"{speaker} (LAST MESSAGE): {m.Content}");
        var list = stored.Count == 0 ? "(nothing)" : string.Join("\n", stored.Select(item => $"{item.Label}: {item.Text}"));
        return $"TODAY: {Today(now)}\n\nCONVERSATION:\n{string.Join("\n", lines)}\n\nSTORED (live, most relevant):\n{list}";
    }

    /// <summary>"Thursday 14 January 2027", as the harness wrote today.</summary>
    internal static string Today(DateTimeOffset now) =>
        $"{now.ToString("dddd", CultureInfo.InvariantCulture)} {now.Day} {now.ToString("MMMM", CultureInfo.InvariantCulture)} {now.Year}";

    /// <summary>Candidate names in a message: runs of capitalised words, each run and each of its words, without a possessive.</summary>
    internal static IReadOnlyList<string> Names(string text)
    {
        var names = new List<string>();
        foreach (Match run in Regex.Matches(text, @"\b\p{Lu}[\p{L}\-]+(?:'s)?(?:\s+\p{Lu}[\p{L}\-]+(?:'s)?)*"))
        {
            var clean = Regex.Replace(run.Value, @"'s\b", "");
            names.Add(clean);
            var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1) names.AddRange(words);
        }
        return [.. names.Where(n => n.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(12)];
    }

    /// <summary>
    /// A stated name fit for the system prompt: at most four words and 40 characters, of letters, apostrophes, hyphens and
    /// dots ("Anne-Marie O'Neill", "Jean de la Fontaine"); anything else (a sentence, an instruction) is not a name: null.
    /// </summary>
    internal static string? PlainName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= 40 && Regex.IsMatch(trimmed, @"^\p{L}[\p{L}\p{M} .'\-]*$")
               && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
            ? trimmed
            : null;
    }

    internal static bool IsUser(string? role) => string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The writer's operations: read from the model's JSON as the harness cleaned them, then made into memory items.</summary>
internal static class MemoryWriterOps
{
    /// <summary>One operation: add, replace or correct; its kind, text and (for replace and correct) the stored id it names.</summary>
    internal sealed record Op(string Action, string Kind, string Text, string? Id);

    /// <summary>A stored memory shown to the writer: its short label (F3, P1, E2, R4), its text, and what it really is.</summary>
    internal sealed record Stored(string Id, char Letter, string Text)
    {
        public string Label { get; init; } = "";

        internal static Stored Of(Fact f) => new(f.FactId, 'F', $"{f.Subject} | {f.Predicate} | {f.Object}" + Validity(f));
        internal static Stored Of(Preference p) => new(p.PreferenceId, 'P', p.PreferenceText);
        internal static Stored Of(Entity e) => new(e.EntityId, 'E', $"{e.Name} ({e.Type})");
        internal static Stored Of(RecalledRelationship r) =>
            new(r.Relationship.RelationshipId, 'R', $"{r.SourceName} -[{r.Relationship.RelationshipType}]-> {r.TargetName}");

        /// <summary>Labels in the order shown, numbered per kind as the harness's ids were (F1, F2, ... P1, ...).</summary>
        internal static IReadOnlyList<Stored> Number(IReadOnlyList<Stored> items)
        {
            var counts = new Dictionary<char, int>();
            return [.. items.Select(item =>
            {
                counts[item.Letter] = counts.GetValueOrDefault(item.Letter) + 1;
                return item with { Label = $"{item.Letter}{counts[item.Letter]}" };
            })];
        }

        private static string Validity(Fact f) =>
            (f.ValidFrom is { } from ? $"  [valid from {from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}]" : "")
            + (f.ValidUntil is { } until ? $"  [valid until {until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}]" : "");
    }

    private static readonly string[] Actions = ["add", "replace", "correct"];
    private static readonly string[] ItemKinds = ["fact", "preference", "entity", "relationship"];

    /// <summary>
    /// The operations in a reply, or false when it holds no JSON object. As the harness (ask_json, then keep and clean): any
    /// JSON object is an answer, and its "ops" list, when there is one, holds the operations; the first
    /// <paramref name="max"/> entries are read, and those that are not an add, replace or correct with a text are dropped.
    /// </summary>
    internal static bool TryParse(string? reply, int max, out IReadOnlyList<Op> ops)
    {
        ops = [];
        var json = LlmExtractionRunner.ExtractJson(reply);
        if (json is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("ops", out var list) || list.ValueKind != JsonValueKind.Array) return true;
            var kept = new List<Op>();
            foreach (var o in list.EnumerateArray().Take(max))
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                var action = Str(o, "op")?.Trim().ToLowerInvariant();
                var text = Str(o, "text")?.Trim();
                if (action is null || !Actions.Contains(action) || string.IsNullOrEmpty(text)) continue;
                var kind = Str(o, "kind")?.Trim().ToLowerInvariant();
                kept.Add(new Op(action, kind is not null && ItemKinds.Contains(kind) ? kind : "fact", text, Str(o, "id")?.Trim()));
            }
            ops = kept;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }

        static string? Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            } : null;
    }

    private static readonly Regex Connection = new(@"^\s*(?<s>.+?)\s*-\[\s*(?<r>[^\]]+?)\s*\]->\s*(?<t>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Named = new(@"^\s*(?<n>.+?)\s*\(\s*(?<t>[A-Za-z_ ]+)\s*\)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The memory items: a fact from "subject | predicate | object", a preference from its sentence, an entity from
    /// "Name (TYPE)", a connection from "A -[RELATION]-> B". A replace or correct names the stored memory it closes only when
    /// it names a stored fact (for a fact) or a stored preference (for a preference); otherwise the item is simply added
    /// (on world 4, none of F's 55 closings named a person or a connection).
    /// </summary>
    internal static UnifiedExtractionResult ToResult(IReadOnlyList<Op> ops, IReadOnlyList<Stored> stored, string? owner)
    {
        var byLabel = stored.ToDictionary(s => s.Label, StringComparer.OrdinalIgnoreCase);
        var facts = new List<ExtractedFact>();
        var preferences = new List<ExtractedPreference>();
        var entities = new List<ExtractedEntity>();
        var relationships = new List<ExtractedRelationship>();
        foreach (var op in ops)
        {
            var target = op.Action != "add" && op.Id is not null && byLabel.TryGetValue(op.Id, out var named) ? named : null;
            if (Connection.Match(op.Text) is { Success: true } link)
            {
                relationships.Add(new ExtractedRelationship
                {
                    SourceEntity = link.Groups["s"].Value,
                    TargetEntity = link.Groups["t"].Value,
                    RelationshipType = link.Groups["r"].Value.Trim().ToUpperInvariant().Replace(' ', '_'),
                });
                continue;
            }
            switch (op.Kind)
            {
                case "preference":
                    preferences.Add(new ExtractedPreference
                    {
                        Category = "general", PreferenceText = op.Text, SourceRole = "user",
                        ReplacesId = target?.Letter == 'P' ? target.Id : null,
                    });
                    break;
                case "entity":
                    var entity = Named.Match(op.Text);
                    entities.Add(new ExtractedEntity
                    {
                        Name = entity.Success ? entity.Groups["n"].Value : op.Text.Trim(),
                        Type = entity.Success ? EntityType(entity.Groups["t"].Value) : "OBJECT",
                    });
                    break;
                case "relationship":
                    break;   // a connection the text did not state as "A -[RELATION]-> B" cannot be stored as one
                default:
                    var parts = op.Text.Split('|', 3, StringSplitOptions.TrimEntries);
                    var (subject, predicate, @object) = parts.Length switch
                    {
                        3 => (parts[0], parts[1], parts[2]),
                        2 => (parts[0], "is", parts[1]),
                        _ => (owner ?? "user", "noted", op.Text.Trim()),
                    };
                    if (subject.Length == 0 || predicate.Length == 0 || @object.Length == 0) break;
                    facts.Add(new ExtractedFact
                    {
                        Subject = subject, Predicate = predicate, Object = @object, SourceRole = "user",
                        ReplacesId = target?.Letter == 'F' ? target.Id : null,
                        ReplacementIsCorrection = target?.Letter == 'F' && op.Action == "correct",
                    });
                    break;
            }
        }
        return new UnifiedExtractionResult { Facts = facts, Preferences = preferences, Entities = entities, Relationships = relationships };
    }

    /// <summary>The prompt's PLACE and THING in the library's entity vocabulary (LOCATION, OBJECT).</summary>
    private static string EntityType(string type) => type.Trim().ToUpperInvariant() switch
    {
        "PLACE" => "LOCATION",
        "THING" => "OBJECT",
        "ORGANISATION" => "ORGANIZATION",
        var other => other.Replace(' ', '_'),
    };
}
