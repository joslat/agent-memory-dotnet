using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Resolution;
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

        /// <summary>
        /// For a repeated name: whether a writer's ref can confirm the stored entity's type (a person or an organisation).
        /// When it cannot (a place, a thing), the same thing is left out: its facts carry it under the stored name, and a
        /// ref'd re-mention would only stay pending as another identity of it (ikb-cover2-1, statement 41).
        /// </summary>
        public bool Confirmable { get; init; }
    }

    /// <summary>
    /// IDENTITY-PERSON-REF-GUARD: what the checks of a person need beyond the reply: the last message, the window's earlier
    /// user turns (latest first), and what each stored person the reply refers to or names by a short form is known as, by
    /// label (<see cref="IdentityRelationCue.Known(Entity, IEnumerable{string})"/>).
    /// </summary>
    internal sealed record Persons(string Message, IReadOnlyList<string> Earlier, IReadOnlyDictionary<string, IReadOnlySet<string>> Known);

    /// <summary>
    /// Problems reported as they are, never sent for a correction: an operation past the limit, an empty connection end (only
    /// a new name could fix it), and a person reference the conversation's relation words refuse (nothing is left to decide).
    /// </summary>
    internal static bool ReportedOnly(MemoryWriterOps.DropReason reason) => reason is MemoryWriterOps.DropReason.OverLimit
        or MemoryWriterOps.DropReason.ConnectionEndEmpty or MemoryWriterOps.DropReason.ReferenceRelationConflict;

    /// <summary>The stored types a writer's ref can confirm (IdentityResolutionOptions' conversational set, events aside).</summary>
    private static readonly HashSet<string> RefConfirmable = new(["PERSON", "ORGANIZATION"], StringComparer.Ordinal);

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
        IReadOnlyList<MemoryWriterOps.Stored> stored, string? owner, Persons? persons = null)
    {
        var issues = new List<Issue>();
        // The paid check of 24 Nov (ihc1-v1-glm-2 and 3, statement 50): a rename answered as an entity "replace" whose text
        // held both names ("Brisa Labs (ORGANIZATION) (replacing: Brisa (ORGANIZATION))") was stored as a junk thing. An
        // entity is always an add: its leading "Name (TYPE)" is the entity, its reference kept.
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Kind == "entity" && ops[i].Action is "replace" or "correct" && !EntityText.IsMatch(ops[i].Text) &&
                LeadingEntity.Match(ops[i].Text) is { Success: true } lead)
                ops[i] = ops[i] with { Action = "add", Id = null, Text = lead.Value.Trim() };
            else if (ops[i].Kind == "entity" && ops[i].Action is "replace" or "correct" && EntityText.IsMatch(ops[i].Text))
                ops[i] = ops[i] with { Action = "add", Id = null };
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
        // IDENTITY-THING-REF-LEFT-OUT: a stored thing or place referred to again is left out, as one said again without a ref
        // is (below). A writer's reference confirms only a person, an organisation or an event, so the re-mention would stay
        // a pending observation, another identity of it (the sixth paid check, cover6-1 statement 40: "Atlas (OBJECT)" with
        // "ref": "E1"). Its facts already carry the stored name. A valid ref here names the stored entity's exact name.
        ops.RemoveAll(op => RefersToStoredThing(op, byLabel));
        // IDENTITY-PERSON-REF-GUARD: a person reference the conversation's own relation words contradict is refused before
        // anything is stored, and the facts it carried keep the message's name. The third paid check, cover3-2 statement 13:
        // "My niece Valentina started school this year, she is six." was referred to the stored designer Valentina Ríos and
        // its facts written under her name; identity's relation guard refused the merge, but the facts were already hers.
        var renamed = new HashSet<MemoryWriterOps.Op>(ReferenceEqualityComparer.Instance);
        for (var i = 0; persons is not null && i < ops.Count; i++)
        {
            if (RelationConflict(ops[i], byLabel, persons) is not { } conflict) continue;
            var storedName = EntityName(conflict.Item.Text);
            var written = ops[i].Text;
            var kept = ops[i] with { Text = conflict.Own is null ? written : $"{conflict.Own} (PERSON)", Ref = null };
            ops[i] = kept;
            renamed.Add(kept);
            var moved = 0;
            for (var j = 0; conflict.Own is not null && j < ops.Count; j++)
            {
                if (j == i || ops[j].Kind is not ("fact" or "preference" or "relationship") || ops[j].Action == "confirm") continue;
                var text = Rename(ops[j].Text, storedName, conflict.Own);
                if (text == ops[j].Text) continue;
                ops[j] = ops[j] with { Text = text };
                moved++;
            }
            issues.Add(new(MemoryWriterOps.DropReason.ReferenceRelationConflict, kept.Index, "entity", written,
                $"\"ref\" {conflict.Item.Label} is \"{storedName}\", known as {string.Join(", ", conflict.Known.Order(StringComparer.Ordinal))}, "
                + $"and the conversation calls this person {string.Join(", ", conflict.Said.Order(StringComparer.Ordinal))}: two people, so the reference "
                + (conflict.Own is null ? "was refused" : $"was refused and the entity and {moved} other operation(s) keep the message's name \"{conflict.Own}\"")));
        }
        // IDENTITY-FACT-NAME-GUARD: the same rule when the reply names a stored person only in its facts, preferences or
        // connections, with no entity: the fifth paid check, cover5-2 and cover5-4 statement 13, answered only "Valentina Ríos |
        // started school | 2026" and "Valentina Ríos | is | six years old" for "My niece Valentina started school this year,
        // she is six.", so there was no reference to refuse. A message that holds the stored full name names that person.
        for (var p = 0; persons is not null && p < stored.Count; p++)
        {
            var person = stored[p];
            if (person.Letter != 'E' || StoredType(person.Text) != "PERSON" || !persons.Known.TryGetValue(person.Label, out var known)) continue;
            var storedName = EntityName(person.Text);
            if (!NamedInFactsOnly(ops, storedName, persons.Message) || OwnName(storedName, storedName, persons.Message) is not { } own) continue;
            var said = RelationsSaid(own, persons);
            if (!IdentityRelationCue.Disagree(said, known)) continue;
            var first = -1;
            string? written = null;
            var moved = 0;
            for (var j = 0; j < ops.Count; j++)
            {
                if (!NamesInText(ops[j]) || !HoldsName(ops[j].Text, storedName)) continue;
                if (first < 0) (first, written) = (j, ops[j].Text);
                ops[j] = ops[j] with { Text = Rename(ops[j].Text, storedName, own) };
                moved++;
            }
            issues.Add(new(MemoryWriterOps.DropReason.ReferenceRelationConflict, ops[first].Index, ops[first].Kind, written,
                $"it names stored {person.Label} \"{storedName}\", known as {string.Join(", ", known.Order(StringComparer.Ordinal))}, and the "
                + $"conversation calls this person {string.Join(", ", said.Order(StringComparer.Ordinal))}: two people, so {moved} operation(s) keep the "
                + $"message's name \"{own}\""));
        }
        // IDENTITY-HARD-CASES A4, the paid check of 24 Nov (ihc1-v2-glm-1, statement 18): a person stored by a first name only,
        // met as a partner's parent; "My boss Fiona, the chief pharmacist, put my name forward" was answered with facts about
        // "Fiona" and no entity. The message holds the stored name itself, so the guard above left it: the boss's facts went
        // under the parent's name and a later "the boss" was confirmed as the parent. The message's relation that the stored
        // person is not known by names a namesake: the operations take the name with that relation, and it gets an entity.
        for (var p = 0; persons is not null && p < stored.Count; p++)
        {
            var person = stored[p];
            if (person.Letter != 'E' || StoredType(person.Text) != "PERSON" || !persons.Known.TryGetValue(person.Label, out var known)) continue;
            var storedName = EntityName(person.Text);
            if (!NamesakeInFactsOnly(ops, storedName, persons.Message)) continue;
            var said = IdentityRelationCue.InTurn(persons.Message, [storedName]);
            if (!IdentityRelationCue.Disagree(said, known)) continue;
            var own = $"{storedName}, {owner ?? "user"}'s {said.Order(StringComparer.Ordinal).First()}";
            var first = -1;
            string? written = null;
            var moved = 0;
            for (var j = 0; j < ops.Count; j++)
            {
                if (!NamesInText(ops[j]) || !HoldsName(ops[j].Text, storedName)) continue;
                if (first < 0) (first, written) = (j, ops[j].Text);
                ops[j] = ops[j] with { Text = Rename(ops[j].Text, storedName, own) };
                moved++;
            }
            var at = Regex.Match(persons.Message, WholeName(storedName), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, NameTimeout);
            ops.Add(new MemoryWriterOps.Op("add", "entity", $"{own} (PERSON)", null)
            {
                Quote = at.Value, QuoteStart = at.Index, QuoteEnd = at.Index + at.Length,
                Index = ops.Select(op => op.Index).DefaultIfEmpty(-1).Max() + 1,
            });
            issues.Add(new(MemoryWriterOps.DropReason.ReferenceRelationConflict, ops[first].Index, ops[first].Kind, written,
                $"it names stored {person.Label} \"{storedName}\", known as {string.Join(", ", known.Order(StringComparer.Ordinal))}, and the "
                + $"message calls this person {string.Join(", ", said.Order(StringComparer.Ordinal))}: another person, so {moved} operation(s) "
                + $"and a new entity are named \"{own}\""));
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
            var confirmable = RefConfirmable.Contains(type);
            var same = confirmable
                ? "if it is that same thing, give its \"ref\""
                : $"if it is that same thing, leave it out: its facts already name it \"{name}\"";
            issues.Add(new(MemoryWriterOps.DropReason.StoredNameRepeated, op.Index, op.Kind, op.Text,
                $"it has exactly the name of stored {string.Join(" or ", labels)}: {same}; "
                + $"if it is a different thing, name it with the words of the last message that tell it apart from \"{name}\"")
            { Kept = op, RepeatedName = name, Confirmable = confirmable });
        }
        // IDENTITY-PERSON-REF-GUARD: a new person named by a short form of a shown stored person's name, or by a name the
        // stored person is already also called, is asked once which it is. The third paid check: shown "Valentina Ríos", the
        // writer wrote "Vale" with "ref" null in all four runs. Not asked of a person the relation words already tell apart.
        var people = stored.Where(item => item.Letter == 'E' && StoredType(item.Text) == "PERSON").ToList();
        foreach (var op in ops.Where(op => op.Kind == "entity" && op.Action == "add" && op.Ref is null && !renamed.Contains(op)))
        {
            if (EntityText.Match(op.Text) is not { Success: true } said || MemoryWriterOps.EntityType(said.Groups["t"].Value) != "PERSON" ||
                issues.Any(issue => ReferenceEquals(issue.Kept, op))) continue;
            var name = said.Groups["n"].Value.Trim();
            var candidates = people.Where(item => MayBeCalled(name, item) && !RelationsDisagree(name, item, persons)).ToList();
            if (candidates.Count == 0) continue;
            issues.Add(new(MemoryWriterOps.DropReason.StoredPersonShortForm, op.Index, op.Kind, op.Text,
                $"\"{name}\" may be a short form or another name of stored "
                + string.Join(" or ", candidates.Select(item => $"{item.Label} \"{EntityName(item.Text)}\""))
                + $": if it is that same person, give its \"ref\" and keep the text \"{name} (PERSON)\"; if it is someone else, give it again "
                + "as it is, with \"ref\" null") { Kept = op });
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
            + "words of the last message. Use \"ref\" only with the label of a stored entity shown above and its exact stored name "
            + "(a person may keep the short form or other name the message calls them by); otherwise set it to null. Never invent "
            + "an event, a date, a place or a reference to make an operation fit.");
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
            var fix = ReportedOnly(issue.Reason) ? null : corrected.FirstOrDefault(op => !used.Contains(op) && Fixes(issue, op));
            // IDENTITY-HARD-CASES A3 (whos-who-v2 run 2): a repeated name told apart in brackets ("Luna (the neighbours'
            // kitten) (PERSON)") is that name with its qualifier. Not taken, the entity kept the bare stored name and identity
            // merged the two. Only for a repeated name: elsewhere a bracketed description is no name and is asked to be fixed.
            if (fix is null && issue.RepeatedName is not null && !ReportedOnly(issue.Reason))
                foreach (var op in corrected.Where(op => !used.Contains(op)))
                    if (Unbracketed(op) is { } plain && Fixes(issue, plain))
                    {
                        used.Add(op);
                        fix = plain;
                        break;
                    }
            // A person named by a short form, given again with no ref: someone else, as the writer says; kept as written.
            if (issue.Reason == MemoryWriterOps.DropReason.StoredPersonShortForm && fix is { Ref: null })
            {
                used.Add(fix);
                continue;
            }
            if (fix is not null && fix.Ref is not null && ReferenceProblem(fix, byLabel).Reason is not null) fix = fix with { Ref = null };
            // IDENTITY-THING-REF-LEFT-OUT holds for a correction too (the paid check of 24 Nov, ihc1-v1-glm-1 statement 54:
            // a malformed "Luna (OBJECT) (also called: Luna) (OBJECT)" fixed as "Luna (OBJECT)" with "ref": "E1" was stored,
            // and the re-mention stayed pending as another identity of the stored thing).
            if (fix is not null && RefersToStoredThing(fix, byLabel))
            {
                used.Add(fix);
                if (issue.Kept is not null) result.Remove(issue.Kept);
                continue;
            }
            // A place or a thing said again under exactly its stored name, with no name that tells it apart, is left out: its
            // facts carry it, and a ref'd observation of it would only stay pending (only a person's or an organisation's ref
            // can confirm one).
            if (issue.RepeatedName is { } again && !issue.Confirmable && (fix is null || Canonical(EntityName(fix.Text)) == Canonical(again)))
            {
                if (fix is not null) used.Add(fix);
                result.Remove(issue.Kept!);
                continue;
            }
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
        ErrorMessage = Truncate($"{issue.Reason}: {issue.Text ?? "(no text)"}; {issue.Problem}; " +
            (issue.RepeatedName is null && issue.Reason != MemoryWriterOps.DropReason.StoredPersonShortForm
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
        if (Renamed(op, item) is { } renamed) return renamed;
        if (op.Kind != "entity" || op.Action != "add" || item.Letter != 'E' || op.Quote is not { } quote ||
            EntityText.Match(op.Text) is not { Success: true } said ||
            !string.Equals(said.Groups["t"].Value.Trim(), "PERSON", StringComparison.OrdinalIgnoreCase) || StoredType(item.Text) != "PERSON")
            return null;
        var name = said.Groups["n"].Value.Trim();
        // IDENTITY-REF-SHORT-FORM-KEPT (the seventh paid check, cover7-2 statement 11): referred to under the full stored
        // name while the quote calls the person by a short form of it ("Vale finished ..."), the short form is kept too,
        // so a later turn that says it alone finds them. A first name alone is part of the name, not a short form.
        if (Canonical(name) == Canonical(EntityName(item.Text)))
            return NameWord.Matches(quote).Select(m => m.Value).FirstOrDefault(word => IsShortFormOf(word, name));
        return MayBeCalled(name, item) &&
               Regex.IsMatch(quote, $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(name)}(?![\p{{L}}\p{{M}}])",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            ? name : null;
    }

    private static readonly Regex NameWord = new(@"[\p{L}\p{M}][\p{L}\p{M}'’-]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a person may be called <paramref name="name"/> as another name of a stored person: a short form of the stored
    /// name (<see cref="IsShortFormOf"/>), or a name the stored person is already shown as also called ("Pepe" for José).
    /// </summary>
    /// <summary>Words that say a name is corrected or changed: "it's Okafor, not Okoro", "renamed it MedPath", "has a new name".</summary>
    private static readonly Regex RenameCue = new(
        @"\b(?:not|renamed|rebranded|new\s+name|now\s+called|now\s+named|is\s+now|(?:is|are|was|were)\s+(?:now\s+)?(?:called|named)|spell(?:ed|ing|t)?|misspel\w*|actually|correction)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// IDENTITY-HARD-CASES E (whos-who-v2, every run): "it's Ms. Okafor, not Okoro". The writer referred the corrected name to
    /// the stored person and the contract refused a reference under another name, so the correction became a second person.
    /// A reference under a new name is that stored entity, also called by it, when the quote says the new name, a word of
    /// the stored one and a correction or a rename, for the kinds a reference may confirm (a person, an organisation, an
    /// event); a place or a thing keeps the exact-name rule. Null otherwise.
    /// </summary>
    private static string? Renamed(MemoryWriterOps.Op op, MemoryWriterOps.Stored item)
    {
        if (op.Kind != "entity" || op.Action != "add" || item.Letter != 'E' || op.Quote is not { } quote ||
            EntityText.Match(op.Text) is not { Success: true } said) return null;
        var type = MemoryWriterOps.EntityType(said.Groups["t"].Value);
        if (type != StoredType(item.Text) || type is not ("PERSON" or "ORGANIZATION" or "EVENT")) return null;
        var name = said.Groups["n"].Value.Trim();
        var stored = EntityName(item.Text);
        if (Canonical(name) == Canonical(stored) || !RenameCue.IsMatch(quote) || !Says(quote, name)) return null;
        var newWords = NameWord.Matches(name).Select(m => Canonical(m.Value)).ToHashSet(StringComparer.Ordinal);
        return NameWord.Matches(stored).Select(m => m.Value)
            .Any(word => word.Length >= 3 && !newWords.Contains(Canonical(word)) && Says(quote, word)) ? name : null;

        static bool Says(string text, string words) => Regex.IsMatch(text,
            $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(words)}(?![\p{{L}}\p{{M}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    private static bool MayBeCalled(string name, MemoryWriterOps.Stored item) =>
        Canonical(name) != Canonical(EntityName(item.Text)) &&
        (IsShortFormOf(name, EntityName(item.Text)) || OtherNames(item).Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>The other names a stored entity was shown with (", also called ...").</summary>
    private static IReadOnlyList<string> OtherNames(MemoryWriterOps.Stored item) =>
        item.AlsoCalled is { } names ? names.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

    /// <summary>
    /// IDENTITY-PERSON-REF-GUARD: the stored people a reply's person entities refer to or name by another name, whose
    /// description <see cref="Check"/> reads; none when the reply names no stored person.
    /// </summary>
    internal static IReadOnlyList<MemoryWriterOps.Stored> PersonsToRead(IReadOnlyList<MemoryWriterOps.Op> ops, IReadOnlyList<MemoryWriterOps.Stored> stored,
        string? message = null)
    {
        var people = stored.Where(item => item.Letter == 'E' && StoredType(item.Text) == "PERSON").ToList();
        var read = new List<MemoryWriterOps.Stored>();
        // IDENTITY-FACT-NAME-GUARD: a stored person a reply's facts name in full, where the message does not.
        if (message is not null)
            read.AddRange(people.Where(item => NamedInFactsOnly(ops, EntityName(item.Text), message) ||
                                               NamesakeInFactsOnly(ops, EntityName(item.Text), message)));
        foreach (var op in ops)
        {
            if (op.Kind != "entity" || op.Action != "add" || EntityText.Match(op.Text) is not { Success: true } said ||
                MemoryWriterOps.EntityType(said.Groups["t"].Value) != "PERSON") continue;
            var name = said.Groups["n"].Value.Trim();
            read.AddRange(op.Ref is { } label
                ? people.Where(item => string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase))
                : people.Where(item => MayBeCalled(name, item)));
        }
        return [.. read.DistinctBy(item => item.Label)];
    }

    private static bool NamedInFactsOnly(IReadOnlyList<MemoryWriterOps.Op> ops, string storedName, string message) =>
        !HoldsName(message, storedName) && ops.Any(op => NamesInText(op) && HoldsName(op.Text, storedName));

    /// <summary>
    /// IDENTITY-HARD-CASES A4: the message holds a stored person's name and gives it a relation ("my boss Marta"), and the
    /// reply names it only in its facts, preferences or connections, with no entity of that name.
    /// </summary>
    private static bool NamesakeInFactsOnly(IReadOnlyList<MemoryWriterOps.Op> ops, string storedName, string message) =>
        HoldsName(message, storedName) && IdentityRelationCue.InTurn(message, [storedName]).Count > 0 &&
        !ops.Any(op => op.Kind == "entity" && op.Action == "add" && Canonical(EntityName(op.Text)) == Canonical(storedName)) &&
        ops.Any(op => NamesInText(op) && HoldsName(op.Text, storedName));

    private static bool NamesInText(MemoryWriterOps.Op op) => op.Kind is "fact" or "preference" or "relationship" && op.Action != "confirm";

    private sealed record Conflict(MemoryWriterOps.Stored Item, string? Own, IReadOnlySet<string> Said, IReadOnlySet<string> Known);

    /// <summary>
    /// A person reference whose conversation gives the person a relation the stored person is not known by, while the
    /// stored person is known by another: identity's own rule (<see cref="IdentityRelationCue.Disagree"/>), applied before
    /// anything is stored. A message that holds the stored full name names that person itself and is left to identity.
    /// </summary>
    private static Conflict? RelationConflict(MemoryWriterOps.Op op, IReadOnlyDictionary<string, MemoryWriterOps.Stored> byLabel, Persons persons)
    {
        if (op.Kind != "entity" || op.Action != "add" || op.Ref is not { } label || !byLabel.TryGetValue(label, out var item) ||
            item.Letter != 'E' || StoredType(item.Text) != "PERSON" || EntityText.Match(op.Text) is not { Success: true } written ||
            MemoryWriterOps.EntityType(written.Groups["t"].Value) != "PERSON" || !persons.Known.TryGetValue(item.Label, out var known))
            return null;
        var storedName = EntityName(item.Text);
        if (HoldsName(persons.Message, storedName)) return null;
        var name = written.Groups["n"].Value.Trim();
        var own = OwnName(name, storedName, persons.Message);
        var said = RelationsSaid(own ?? name, persons);
        return IdentityRelationCue.Disagree(said, known) ? new(item, own, said, known) : null;
    }

    /// <summary>Whether the relation words said of <paramref name="name"/> tell it apart from the stored person.</summary>
    private static bool RelationsDisagree(string name, MemoryWriterOps.Stored item, Persons? persons) =>
        persons is not null && persons.Known.TryGetValue(item.Label, out var known) &&
        IdentityRelationCue.Disagree(RelationsSaid(name, persons), known);

    /// <summary>
    /// The relations the conversation gives the person (<see cref="IdentityRelationCue.InTurn"/>) under the name the message
    /// calls them by: in the last message, or, when it gives none, in the latest earlier turn that names the person, and only
    /// that one ("Valentina lost her first tooth" after "My niece Valentina started school"). Only that name: the stored
    /// name's other words belong to whoever else the message names ("My niece Valentina wants to draw like Vale").
    /// </summary>
    private static IReadOnlySet<string> RelationsSaid(string name, Persons persons)
    {
        string[] names = [name];
        var said = IdentityRelationCue.InTurn(persons.Message, names);
        if (said.Count > 0) return said;
        foreach (var turn in persons.Earlier)
            if (HoldsName(turn, name)) return IdentityRelationCue.InTurn(turn, names);
        return said;
    }

    /// <summary>
    /// The name the message itself gives a person a reference put under a stored name: the writer's own name when it is not
    /// the stored one and the message holds it ("Bea"), else the longest run of the message's capitalised words that are words
    /// of the stored name or short forms of it ("My niece Valentina" gives "Valentina"); null when there is none.
    /// </summary>
    private static string? OwnName(string written, string storedName, string message)
    {
        if (Canonical(written) != Canonical(storedName)) return HoldsName(message, written) ? written : null;
        var storedWords = NameWord.Matches(storedName).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        string? best = null;
        int start = -1, end = -1;
        void Close()
        {
            if (start >= 0 && (best is null || end - start > best.Length)) best = message[start..end];
            start = end = -1;
        }
        foreach (Match match in NameWord.Matches(message))
        {
            // A possessive's "'s" is not part of the name: "Valentina's school" names Valentina.
            var word = Regex.Replace(match.Value, "['’]s$", "");
            var fits = word.Length > 0 && char.IsUpper(word[0]) &&
                (storedWords.Contains(word.ToLowerInvariant()) || IsShortFormOf(word, storedName));
            if (!fits || start < 0 || !string.IsNullOrWhiteSpace(message[end..match.Index])) Close();
            if (!fits) continue;
            if (start < 0) start = match.Index;
            end = match.Index + word.Length;
        }
        Close();
        return best is null || Canonical(best) == Canonical(storedName) ? null : best;
    }

    private static readonly TimeSpan NameTimeout = TimeSpan.FromMilliseconds(50);

    private static string WholeName(string name) => $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(name)}(?![\p{{L}}\p{{M}}])";

    /// <summary>Whether <paramref name="text"/> holds <paramref name="name"/> as whole words, in any case.</summary>
    private static bool HoldsName(string text, string name) => name.Length > 0 &&
        Regex.IsMatch(text, WholeName(name), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, NameTimeout);

    /// <summary>Every whole-word <paramref name="from"/> in <paramref name="text"/> becomes <paramref name="to"/>.</summary>
    private static string Rename(string text, string from, string to) =>
        Regex.Replace(text, WholeName(from), to.Replace("$", "$$", StringComparison.Ordinal),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, NameTimeout);

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

    /// <summary>
    /// An entity add whose valid ref names a stored thing or place: a writer's reference confirms only a person, an
    /// organisation or an event, so such a re-mention would stay pending as another identity of it. It is left out.
    /// </summary>
    private static bool RefersToStoredThing(MemoryWriterOps.Op op, IReadOnlyDictionary<string, MemoryWriterOps.Stored> byLabel) =>
        op.Kind == "entity" && op.Action == "add" && op.Ref is { } label &&
        byLabel.TryGetValue(label, out var item) && item.Letter == 'E' && StoredType(item.Text) is { } type &&
        type != "EVENT" && !RefConfirmable.Contains(type);

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
    private static readonly Regex LeadingEntity = new(
        @"^\s*[^|(){}\[\]]+?\s*\(\s*(?:PERSON|PLACE|ORGANIZATION|ORGANISATION|THING|EVENT|LOCATION|OBJECT)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Bracketed = new(
        @"^\s*(?<n>[^|(){}\[\]]+?)\s*\(\s*(?<q>[^|(){}\[\]]+?)\s*\)\s*\(\s*(?<t>PERSON|PLACE|ORGANIZATION|ORGANISATION|THING|EVENT|LOCATION|OBJECT)\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Name (qualifier) (TYPE)" as the entity "Name, qualifier (TYPE)", or null when the text is not of that form.</summary>
    private static MemoryWriterOps.Op? Unbracketed(MemoryWriterOps.Op op) =>
        op.Kind == "entity" && op.Action == "add" && Bracketed.Match(op.Text) is { Success: true } m
            ? op with { Text = $"{m.Groups["n"].Value.Trim()}, {m.Groups["q"].Value.Trim()} ({m.Groups["t"].Value.Trim()})" }
            : null;

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
