using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Core.Memory;
using Microsoft.Extensions.AI;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// The event-identity writer's operation contract (<see cref="LlmExtractionOptions.MemoryWriterEventIdentities"/>): a
/// schema-constrained reply, deterministic checks of every operation, at most one targeted correction call, and a
/// visible outcome for whatever still fails. It never coerces an invalid action, accepts a quote the message does not
/// contain, invents an entity or keeps a reference to something the writer was not shown.
/// </summary>
internal static class MemoryWriterContract
{
    /// <summary>One thing the reply got wrong, with what the correction is asked to fix and how a fix is recognised.</summary>
    internal sealed record Issue(MemoryWriterOps.DropReason Reason, int Index, string? Kind, string? Text, string Problem)
    {
        /// <summary>For a reference problem: the operation as kept without its reference.</summary>
        public MemoryWriterOps.Op? Kept { get; init; }

        /// <summary>For a missing event observation: the event name the fact uses.</summary>
        public string? EventName { get; init; }

        /// <summary>For an entity named exactly as a stored one: that name, which a renamed fix must tell apart.</summary>
        public string? RepeatedName { get; init; }
    }

    private static readonly string[] EventPredicates = ["is scheduled for", "takes place at", "participation"];

    // "Name (TYPE)" and nothing else: a fact, a list or JSON debris in an entity's text is not an entity name.
    private static readonly Regex EntityText = new(
        @"^\s*(?<n>[^|(){}\[\]]+?)\s*\(\s*(?<t>PERSON|PLACE|ORGANIZATION|ORGANISATION|THING|EVENT|LOCATION|OBJECT)\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The reply schema: the measured operation shape, every field present, "ref" null when unused.</summary>
    internal static ChatResponseFormat Format(int maxOperations) => ChatResponseFormat.ForJsonSchema(
        Internal.SchemaText.Parse("""
        {
          "type":"object","additionalProperties":false,"required":["ops"],
          "properties":{"ops":{"type":"array","maxItems":MAX,"items":{
            "type":"object","additionalProperties":false,
            "required":["op","kind","text","quote","id","until","ref"],
            "properties":{
              "op":{"type":"string","enum":["add","replace","correct","confirm"]},
              "kind":{"type":"string","enum":["fact","preference","entity","relationship"]},
              "text":{"type":"string"},
              "quote":{"type":"string"},
              "id":{"type":["string","null"]},
              "until":{"type":["string","null"]},
              "ref":{"type":["string","null"]}
            }
          }}}
        }
        """.Replace("MAX", Math.Max(1, maxOperations).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)),
        "agent_memory_writer_ops_v1", "Memory operations, each resting on exact words of the last message.");

    /// <summary>
    /// Checks the kept operations and the dropped ones. Reference problems are removed from <paramref name="ops"/> (the
    /// operation stays, without its reference) and returned as issues, as are drops and missing event observations.
    /// </summary>
    internal static IReadOnlyList<Issue> Check(List<MemoryWriterOps.Op> ops, IReadOnlyList<MemoryWriterOps.DroppedOp> dropped,
        IReadOnlyList<MemoryWriterOps.Stored> stored, string? owner)
    {
        var issues = new List<Issue>();
        foreach (var drop in dropped)
        {
            // A dropped entity whose text is malformed too is asked to fix both in its one correction (ILC-23 ECB+: a
            // quote-only fix kept "Name (PERSON) | null").
            var alsoMalformed = drop.Kind == "entity" && drop.Text is { } text && !EntityText.IsMatch(text);
            issues.Add(new(drop.Reason, drop.Index, drop.Kind, drop.Text,
                DropProblem(drop.Reason) + (alsoMalformed ? "; and " + EntityFormatProblem : "")));
        }
        var byLabel = stored.ToDictionary(item => item.Label, StringComparer.OrdinalIgnoreCase);
        for (var i = ops.Count - 1; i >= 0; i--)
        {
            // One line, no code fence, no embedded operation JSON: debris in a text is never stored (real-04: a copy of
            // the reply inside a fact's object cost the fact its date).
            if (ops[i].Action == "confirm" || ops[i].Kind == "entity" || TextProblem(ops[i].Text) is not { } problem) continue;
            issues.Add(new(MemoryWriterOps.DropReason.TextFormatInvalid, ops[i].Index, ops[i].Kind, ops[i].Text, problem));
            ops.RemoveAt(i);
        }
        for (var i = ops.Count - 1; i >= 0; i--)
        {
            if (ops[i].Kind != "entity" || ops[i].Action != "add" || EntityText.IsMatch(ops[i].Text)) continue;
            // Never coerced into an OBJECT named after the whole text: corrected once, else dropped and reported.
            issues.Add(new(MemoryWriterOps.DropReason.EntityFormatInvalid, ops[i].Index, "entity", ops[i].Text, EntityFormatProblem));
            ops.RemoveAt(i);
        }
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Ref is null) continue;
            // A reference only means something on an entity add; elsewhere it is unused and silently dropped.
            if (ops[i].Kind != "entity" || ops[i].Action != "add") { ops[i] = ops[i] with { Ref = null }; continue; }
            var (reason, problem) = ReferenceProblem(ops[i], byLabel);
            if (reason is null) continue;
            var kept = ops[i] with { Ref = null };
            issues.Add(new(reason.Value, ops[i].Index, ops[i].Kind, ops[i].Text, problem!) { Kept = kept });
            ops[i] = kept;
        }
        // IDENTITY-WRITER-COVERAGE: an entity add without a ref, named and typed exactly as a shown stored entity, is asked
        // once which it is (ikb-cover-1: a second café was stored under the first one's name, so the next turn saw two it
        // could not tell apart, and added a third). Events are observed again on purpose, so they are never asked.
        var storedNames = stored.Where(item => item.Letter == 'E' && StoredType(item.Text) is { } type && type != "EVENT")
            .GroupBy(item => (Name: Canonical(EntityName(item.Text)), Type: StoredType(item.Text)!))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Label).ToList());
        foreach (var op in ops.Where(op => op.Kind == "entity" && op.Action == "add" && op.Ref is null))
        {
            if (EntityText.Match(op.Text) is not { Success: true } said || issues.Any(issue => ReferenceEquals(issue.Kept, op))) continue;
            var name = said.Groups["n"].Value.Trim();
            var type = MemoryWriterOps.EntityType(said.Groups["t"].Value);
            if (type == "EVENT" || !storedNames.TryGetValue((Canonical(name), type), out var labels)) continue;
            issues.Add(new(MemoryWriterOps.DropReason.StoredNameRepeated, op.Index, op.Kind, op.Text,
                $"it has exactly the name of stored {string.Join(" or ", labels)}: if it is that same thing, give its \"ref\"; "
                + $"if it is a different thing, name it with the words of the last message that tell it apart from \"{name}\"")
            { Kept = op, RepeatedName = name });
        }
        var observed = ops.Where(op => op.Kind == "entity" && op.Action == "add")
            .Select(op => Canonical(EntityName(op.Text))).ToHashSet(StringComparer.Ordinal);
        var storedEvents = stored.Where(item => item.Letter == 'E' && StoredType(item.Text) == "EVENT")
            .Select(item => Canonical(EntityName(item.Text))).ToHashSet(StringComparer.Ordinal);
        var storedOthers = stored.Where(item => item.Letter == 'E' && StoredType(item.Text) != "EVENT")
            .Select(item => Canonical(EntityName(item.Text))).ToHashSet(StringComparer.Ordinal);
        var self = Canonical(owner ?? "user");
        foreach (var op in ops.Where(op => op.Kind == "fact" && op.Action != "confirm"))
        {
            var parts = op.Text.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || parts[0].Length == 0) continue;
            var subject = Canonical(parts[0]);
            if (subject == self || subject == "user" || observed.Contains(subject) ||
                (storedOthers.Contains(subject) && !storedEvents.Contains(subject))) continue;
            var eventFact = EventPredicates.Contains(Canonical(parts[1]), StringComparer.Ordinal) || storedEvents.Contains(subject);
            if (!eventFact || issues.Any(issue => issue.EventName is { } name && Canonical(name) == subject)) continue;
            issues.Add(new(MemoryWriterOps.DropReason.EventObservationMissing, op.Index, "entity", op.Text,
                $"the fact names the event \"{parts[0]}\" as its subject, but no entity operation \"{parts[0]} (EVENT)\" observes it")
            { EventName = parts[0] });
        }
        return issues;
    }

    /// <summary>The correction request: the original answer, then only what was wrong with it.</summary>
    internal static string Request(IReadOnlyList<Issue> issues)
    {
        var text = new StringBuilder("Some operations in your answer could not be kept:\n");
        foreach (var issue in issues)
            text.Append(CultureInfo.InvariantCulture, $"- #{issue.Index} ({issue.Kind ?? "unknown kind"}) \"{issue.Text ?? "(no text)"}\": {issue.Problem}.\n");
        text.Append("Answer with JSON only, in the same format: {\"ops\": [...]} holding corrected operations for these items only, "
            + "or {\"ops\": []} to leave them out. Do not repeat operations that were kept. Every quote must be exact, contiguous "
            + "words of the last message. Use \"ref\" only with the label of a stored entity shown above and its exact stored name; "
            + "otherwise set it to null. Never invent an event, a date, a place or a reference to make an operation fit.");
        return text.ToString();
    }

    /// <summary>
    /// Applies a validated correction: an operation is accepted only when it fixes a listed issue (same text, the same
    /// entity for a reference, the missing event for an observation) and passes the same checks as any other. Everything
    /// unfixed becomes a visible rejection; a reference problem leaves its entity kept without the reference.
    /// </summary>
    internal static (List<MemoryWriterOps.Op> Ops, List<Issue> Unfixed) Merge(List<MemoryWriterOps.Op> ops, IReadOnlyList<Issue> issues,
        IReadOnlyList<MemoryWriterOps.Op> corrected, IReadOnlyList<MemoryWriterOps.Stored> stored, int maxOperations)
    {
        var byLabel = stored.ToDictionary(item => item.Label, StringComparer.OrdinalIgnoreCase);
        var result = new List<MemoryWriterOps.Op>(ops);
        var unfixed = new List<Issue>();
        var used = new HashSet<MemoryWriterOps.Op>(ReferenceEqualityComparer.Instance);
        var cap = maxOperations + issues.Count(issue => issue.Reason == MemoryWriterOps.DropReason.EventObservationMissing);
        var nextIndex = ops.Concat(corrected).Select(op => op.Index).DefaultIfEmpty(-1).Max() + 1;
        foreach (var issue in issues)
        {
            // Never sent for a correction, so never given one: a fix meant for another issue stays with that issue.
            var fix = issue.Reason is MemoryWriterOps.DropReason.OverLimit or MemoryWriterOps.DropReason.ConnectionEndEmpty
                ? null : corrected.FirstOrDefault(op => !used.Contains(op) && Fixes(issue, op));
            if (fix is not null && fix.Ref is not null && ReferenceProblem(fix, byLabel).Reason is not null) fix = fix with { Ref = null };
            // A repeated name is resolved by a ref, or by a name that tells the thing apart (Fixes checks it holds the old one).
            var resolvesReference = issue.Kept is null || fix?.Ref is not null ||
                (issue.RepeatedName is { } repeated && fix is not null && Canonical(EntityName(fix.Text)) != Canonical(repeated));
            if (fix is null || (!resolvesReference && issue.Kept is not null))
            {
                unfixed.Add(issue);
                continue;
            }
            // A fix passes the shape checks every first answer passes: one that repairs its listed issue but keeps a
            // malformed entity or JSON debris is still rejected (ILC-23 ECB+: a quote-only fix stored an OBJECT).
            if (ShapeProblem(fix) is { } shape)
            {
                unfixed.Add(issue with { Reason = shape.Reason, Text = fix.Text, Problem = shape.Problem });
                continue;
            }
            used.Add(fix);
            if (issue.Kept is not null)
            {
                var at = result.IndexOf(issue.Kept);
                if (at >= 0) result[at] = fix with { Index = issue.Kept.Index };
                continue;
            }
            if (result.Count >= cap || result.Any(op => SameOperation(op, fix))) { unfixed.Add(issue); continue; }
            result.Add(fix with { Index = nextIndex++ });
        }
        return (result, unfixed);
    }

    /// <summary>A rejection the caller sees in its ingestion outcomes, not only at Debug level.</summary>
    internal static IngestionItemOutcome Outcome(Issue issue) => issue.Kept is not null ? Base(issue) with
    {
        SourceKey = $"writer:#{issue.Index.ToString(CultureInfo.InvariantCulture)}:ref",
        ErrorMessage = Truncate($"{issue.Reason}: {issue.Text ?? "(no text)"}; {issue.Problem}; " + (issue.RepeatedName is null
            ? "the entity was kept without its reference" : "the entity was kept as written"), 512),
    } : Base(issue);

    private static IngestionItemOutcome Base(Issue issue) => new()
    {
        Kind = issue.Kind switch
        {
            "preference" => MemoryItemKind.Preference,
            "entity" => MemoryItemKind.Entity,
            "relationship" => MemoryItemKind.Relationship,
            _ => MemoryItemKind.Fact,
        },
        Stage = IngestionStage.Extraction,
        Status = IngestionItemStatus.Skipped,
        SourceKey = $"writer:#{issue.Index.ToString(CultureInfo.InvariantCulture)}",
        ErrorCode = MemoryErrorCodes.WriterOperationRejected,
        ErrorMessage = Truncate($"{issue.Reason}: {issue.Text ?? "(no text)"}; {issue.Problem}", 512),
        Retryable = false,
    };

    /// <summary>The entity a checked reference names, or null: only labels shown as E items, on an entity add with a located quote.</summary>
    internal static ExtractedEntityReference? Reference(MemoryWriterOps.Op op, IReadOnlyDictionary<string, MemoryWriterOps.Stored> byLabel, string message) =>
        op.Ref is { } label && byLabel.TryGetValue(label, out var item) && item.Letter == 'E' && op.Kind == "entity" && op.Action == "add" &&
        op.QuoteStart is int start && op.QuoteEnd is int end && start >= 0 && end <= message.Length && start < end
            ? new() { EntityId = item.Id, Quote = message[start..end] }
            : null;

    private static (MemoryWriterOps.DropReason? Reason, string? Problem) ReferenceProblem(MemoryWriterOps.Op op,
        IReadOnlyDictionary<string, MemoryWriterOps.Stored> byLabel)
    {
        if (op.Kind != "entity" || op.Action != "add")
            return (MemoryWriterOps.DropReason.ReferenceInvalid, "\"ref\" belongs only on an entity add");
        if (!byLabel.TryGetValue(op.Ref!, out var item) || item.Letter != 'E')
            return (MemoryWriterOps.DropReason.ReferenceInvalid, $"\"ref\" {op.Ref} is not the label of a stored entity shown above");
        if (op.QuoteStart is null)
            return (MemoryWriterOps.DropReason.ReferenceWithoutQuote, "a \"ref\" needs a quote of the last message's words that refer to the stored entity");
        var stored = EntityName(item.Text);
        if (Canonical(EntityName(op.Text)) != Canonical(stored) && AlsoCalled(op, item) is null)
            return (MemoryWriterOps.DropReason.ReferenceNameMismatch, $"\"ref\" {op.Ref} is \"{stored}\"; with a ref, the text must use that exact stored name");
        return (null, null);
    }

    /// <summary>
    /// IDENTITY-NICKNAME-SPLIT: the other name a referred entity add gives a stored person, or null. In six tier 2 runs the
    /// writer, shown the stored "Valentina Ríos", wrote "Vale" as a new person: a reference had to carry the stored name, so
    /// there was no way to say "this stored person, called Vale here". A person's entity add that refers to a stored person
    /// under a short form of the stored name (<see cref="IsShortFormOf"/>), in words its quote holds, says exactly that: the
    /// mention keeps the stored name, which tells namesakes apart, and the short form becomes its alias. Only people: an
    /// organisation, place, thing or event keeps the exact stored-name rule (ILC-10).
    /// </summary>
    internal static string? AlsoCalled(MemoryWriterOps.Op op, MemoryWriterOps.Stored item)
    {
        if (op.Kind != "entity" || op.Action != "add" || item.Letter != 'E' || op.Quote is not { } quote ||
            EntityText.Match(op.Text) is not { Success: true } said ||
            !string.Equals(said.Groups["t"].Value.Trim(), "PERSON", StringComparison.OrdinalIgnoreCase) || StoredType(item.Text) != "PERSON")
            return null;
        var name = said.Groups["n"].Value.Trim();
        return IsShortFormOf(name, EntityName(item.Text)) &&
               Regex.IsMatch(quote, $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(name)}(?![\p{{L}}\p{{M}}])",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            ? name : null;
    }

    private static readonly Regex NameWord = new(@"[\p{L}\p{M}][\p{L}\p{M}'’-]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="said"/> is a short form or nickname of the name <paramref name="full"/>: each of its words is a
    /// word of the full name or a capitalised word of three letters or more that is not one but starts with the first three
    /// letters of one ("Bea" for "Beatriz Molina", "Bea Molina" too), and at least one is the latter. A first name or a
    /// surname alone is part of the full name, not another name of it; a name that shares no start ("Jonas") is another
    /// person's. Only a gate on what a reference may carry: the writer's judgment and identity's own checks still decide.
    /// </summary>
    internal static bool IsShortFormOf(string said, string full)
    {
        var words = NameWord.Matches(full).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var saidWords = NameWord.Matches(said).Select(m => m.Value).ToList();
        if (words.Count == 0 || saidWords.Count == 0) return false;
        var shortened = false;
        foreach (var word in saidWords)
        {
            var lower = word.ToLowerInvariant();
            if (words.Contains(lower)) continue;
            if (lower.Length < 3 || !char.IsUpper(word[0]) ||
                !words.Any(w => w.Length >= 3 && w.StartsWith(lower[..3], StringComparison.Ordinal))) return false;
            shortened = true;
        }
        return shortened;
    }

    private static bool HasDebris(string text) =>
        text.Contains('\n') || text.Contains('\r') || text.Contains("```", StringComparison.Ordinal) ||
        text.Contains("\"ops\"", StringComparison.Ordinal) || text.Contains("{\"op", StringComparison.Ordinal);

    private const string DebrisProblem = "the text must be one line of the stated format, with no code fence, JSON or second operation inside it";
    private const string EmptyEndProblem = "a connection joins two names: each end must hold a name";

    /// <summary>What is wrong with a fact's, preference's or connection's text, if anything: debris, or an empty connection end.</summary>
    private static string? TextProblem(string text) =>
        HasDebris(text) ? DebrisProblem : MemoryWriterOps.HasEmptyConnectionEnd(text) ? EmptyEndProblem : null;
    private const string EntityFormatProblem = "an entity's text must be exactly \"Name (PERSON|PLACE|ORGANIZATION|THING|EVENT)\", with nothing before or after";

    /// <summary>The shape checks every operation passes, a correction's included: an entity's form and a text's debris.</summary>
    private static (MemoryWriterOps.DropReason Reason, string Problem)? ShapeProblem(MemoryWriterOps.Op op)
    {
        if (op.Kind == "entity" && op.Action == "add" && !EntityText.IsMatch(op.Text))
            return (MemoryWriterOps.DropReason.EntityFormatInvalid, EntityFormatProblem);
        if (op.Kind != "entity" && op.Action != "confirm" && TextProblem(op.Text) is { } problem)
            return (MemoryWriterOps.DropReason.TextFormatInvalid, problem);
        return null;
    }

    private static bool Fixes(Issue issue, MemoryWriterOps.Op op)
    {
        if (issue.Reason == MemoryWriterOps.DropReason.TextFormatInvalid)
            return op.Kind == issue.Kind && op.Action != "confirm" && TextProblem(op.Text) is null && op.Text.Length > 0 &&
                issue.Text is { } original && original.Contains(op.Text, StringComparison.OrdinalIgnoreCase);
        // A malformed entity (listed for its form, or dropped for another reason with its form named too) is fixed by a
        // well-formed entity whose name its original text holds.
        if (issue.Reason == MemoryWriterOps.DropReason.EntityFormatInvalid ||
            issue.Kind == "entity" && issue.Text is { } listed && !EntityText.IsMatch(listed))
            return op.Kind == "entity" && op.Action == "add" && EntityText.IsMatch(op.Text) && issue.Text is { } original &&
                original.Contains(EntityName(op.Text), StringComparison.OrdinalIgnoreCase);
        if (issue.EventName is { } name)
            return op.Kind == "entity" && op.Action == "add" && Canonical(EntityName(op.Text)) == Canonical(name);
        // A repeated stored name is fixed by the same name with a ref, or by a longer name that holds it and tells it apart.
        if (issue.RepeatedName is { } repeated)
        {
            if (op.Kind != "entity" || op.Action != "add" || !EntityText.IsMatch(op.Text)) return false;
            var written = Canonical(EntityName(op.Text));
            return op.Ref is not null ? written == Canonical(repeated)
                : written != Canonical(repeated) && written.Contains(Canonical(repeated), StringComparison.Ordinal);
        }
        if (issue.Kept is { } kept)
            return op.Kind == "entity" && op.Action == "add" && Canonical(EntityName(op.Text)) == Canonical(EntityName(kept.Text));
        return issue.Text is { Length: > 0 } text && Canonical(op.Text) == Canonical(text);
    }

    private static bool SameOperation(MemoryWriterOps.Op left, MemoryWriterOps.Op right) =>
        left.Action == right.Action && left.Kind == right.Kind && Canonical(left.Text) == Canonical(right.Text);

    private static string DropProblem(MemoryWriterOps.DropReason reason) => reason switch
    {
        MemoryWriterOps.DropReason.UnknownAction => "\"op\" must be add, replace, correct or confirm (an entity is an add with kind \"entity\")",
        MemoryWriterOps.DropReason.MissingText => "an add, replace or correct needs \"text\"",
        MemoryWriterOps.DropReason.MissingId => "a confirm needs the \"id\" of the stored memory it confirms",
        MemoryWriterOps.DropReason.QuoteNotFound => "its quote is not exact, contiguous words of the last message",
        MemoryWriterOps.DropReason.NotAnObject => "each operation must be a JSON object",
        MemoryWriterOps.DropReason.OverLimit => "it was beyond the most operations one answer may hold",
        MemoryWriterOps.DropReason.ConnectionEndEmpty => "a connection joins two names: each end must hold a name",
        _ => reason.ToString(),
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    internal static string Canonical(string? value) => MemoryTripleCanonicalizer.CanonicalValue(value);

    /// <summary>"Name (TYPE)" to its name; anything else stays as it is.</summary>
    internal static string EntityName(string text)
    {
        var open = text.LastIndexOf('(');
        return open > 0 && text.TrimEnd().EndsWith(')') ? text[..open].Trim() : text.Trim();
    }

    private static string? StoredType(string text)
    {
        var open = text.LastIndexOf('(');
        return open > 0 && text.TrimEnd().EndsWith(')') ? text[(open + 1)..text.TrimEnd().LastIndexOf(')')].Trim().ToUpperInvariant() : null;
    }
}
