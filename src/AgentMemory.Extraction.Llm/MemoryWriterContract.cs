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
    }

    private static readonly string[] EventPredicates = ["is scheduled for", "takes place at", "participation"];

    // "Name (TYPE)" and nothing else: a fact, a list or JSON debris in an entity's text is not an entity name.
    private static readonly Regex EntityText = new(
        @"^\s*(?<n>[^|(){}\[\]]+?)\s*\(\s*(?<t>PERSON|PLACE|ORGANIZATION|ORGANISATION|THING|EVENT|LOCATION|OBJECT)\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The reply schema: the measured operation shape, every field present, "ref" null when unused.</summary>
    internal static ChatResponseFormat Format(int maxOperations) => ChatResponseFormat.ForJsonSchema(
        JsonSerializer.Deserialize<JsonElement>("""
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
            issues.Add(new(drop.Reason, drop.Index, drop.Kind, drop.Text, DropProblem(drop.Reason)));
        var byLabel = stored.ToDictionary(item => item.Label, StringComparer.OrdinalIgnoreCase);
        for (var i = ops.Count - 1; i >= 0; i--)
        {
            // One line, no code fence, no embedded operation JSON: debris in a text is never stored (real-04: a copy of
            // the reply inside a fact's object cost the fact its date).
            if (ops[i].Action == "confirm" || ops[i].Kind == "entity" || !HasDebris(ops[i].Text)) continue;
            issues.Add(new(MemoryWriterOps.DropReason.TextFormatInvalid, ops[i].Index, ops[i].Kind, ops[i].Text,
                "the text must be one line of the stated format, with no code fence, JSON or second operation inside it"));
            ops.RemoveAt(i);
        }
        for (var i = ops.Count - 1; i >= 0; i--)
        {
            if (ops[i].Kind != "entity" || ops[i].Action != "add" || EntityText.IsMatch(ops[i].Text)) continue;
            // Never coerced into an OBJECT named after the whole text: corrected once, else dropped and reported.
            issues.Add(new(MemoryWriterOps.DropReason.EntityFormatInvalid, ops[i].Index, "entity", ops[i].Text,
                "an entity's text must be exactly \"Name (PERSON|PLACE|ORGANIZATION|THING|EVENT)\", with nothing before or after"));
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
            var fix = issue.Reason == MemoryWriterOps.DropReason.OverLimit ? null : corrected.FirstOrDefault(op => !used.Contains(op) && Fixes(issue, op));
            if (fix is not null && fix.Ref is not null && ReferenceProblem(fix, byLabel).Reason is not null) fix = fix with { Ref = null };
            var resolvesReference = issue.Kept is null || fix?.Ref is not null;
            if (fix is null || (!resolvesReference && issue.Kept is not null))
            {
                unfixed.Add(issue);
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
        ErrorMessage = Truncate($"{issue.Reason}: {issue.Text ?? "(no text)"}; {issue.Problem}; the entity was kept without its reference", 512),
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
        if (Canonical(EntityName(op.Text)) != Canonical(stored))
            return (MemoryWriterOps.DropReason.ReferenceNameMismatch, $"\"ref\" {op.Ref} is \"{stored}\"; with a ref, the text must use that exact stored name");
        return (null, null);
    }

    private static bool HasDebris(string text) =>
        text.Contains('\n') || text.Contains('\r') || text.Contains("```", StringComparison.Ordinal) ||
        text.Contains("\"ops\"", StringComparison.Ordinal) || text.Contains("{\"op", StringComparison.Ordinal);

    private static bool Fixes(Issue issue, MemoryWriterOps.Op op)
    {
        if (issue.Reason == MemoryWriterOps.DropReason.TextFormatInvalid)
            return op.Kind == issue.Kind && op.Action != "confirm" && !HasDebris(op.Text) && op.Text.Length > 0 &&
                issue.Text is { } original && original.Contains(op.Text, StringComparison.OrdinalIgnoreCase);
        if (issue.Reason == MemoryWriterOps.DropReason.EntityFormatInvalid)
            return op.Kind == "entity" && op.Action == "add" && EntityText.IsMatch(op.Text) && issue.Text is { } original &&
                original.Contains(EntityName(op.Text), StringComparison.OrdinalIgnoreCase);
        if (issue.EventName is { } name)
            return op.Kind == "entity" && op.Action == "add" && Canonical(EntityName(op.Text)) == Canonical(name);
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
