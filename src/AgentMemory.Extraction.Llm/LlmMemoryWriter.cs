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
/// The store-aware writer : one model call per turn that sees the owner's most relevant stored memories and
/// proposes only what is new (add), what changes a stored memory (replace, naming it) or what was wrong (correct, naming
/// it), or nothing. Storage round 3's selected form "F", ported from the research harness (round3.py, storage_round2.py):
/// with later confirmation, temporal and aspiration rules. Historical form-F measurements on a fresh 12-session world: storage accuracy
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
    private readonly ChatResponseFormat? _operationFormat;

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
        // Event-identity mode only: the default writer's request carries no response format, as measured.
        _operationFormat = _options.MemoryWriterEventIdentities == true ? MemoryWriterContract.Format(_options.MemoryWriterMaxOperations) : null;
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
        var now = _services.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var owner = await OwnerNameAsync(scope, cancellationToken).ConfigureAwait(false);
        var text = string.Join("\n", said.Select(m => m.Content));
        var stored = await StoredAsync(text, owner, scope, now, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("memory.write.candidates", stored.Count);

        var system = MemoryWriterPrompt.System(owner, _options.MemoryWriterMaxOperations, _options.MemoryWriterEventIdentities == true);
        if (_options.MemoryWriterEventIdentities == true) system += MemoryWriterPrompt.EventIdentityInstructions;
        var context = MemoryWriterPrompt.Context(now, request.Window, owner, stored);
        var (answered, dropped, reply) = await AskAsync(system, context, cancellationToken).ConfigureAwait(false);

        // #249. Every operation that writes must quote the words it rests on, and the quote must be in the message: one
        // that is not is a claim the message does not make, and is dropped (and recorded, #250). A located quote's offsets
        // go to the stored memory's provenance. An operation without a quote is kept as it was, without offsets.
        var message = said[^1].Content;
        var (ops, drops) = Locate(answered, dropped, message);
        IReadOnlyList<IngestionItemOutcome> rejected = [];
        if (_operationFormat is not null)
            (ops, drops, rejected) = await EnforceAsync(system, context, reply, ops, drops, stored, owner, message, activity, cancellationToken)
                .ConfigureAwait(false);

        if (drops.Count > 0)
        {
            activity?.SetTag("memory.write.dropped", drops.Count);
            activity?.SetTag("memory.write.dropped_reasons", string.Join(",", drops.GroupBy(d => d.Reason).Select(g => $"{g.Key}:{g.Count()}")));
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Memory writer: dropped {Count} operation(s): {Dropped}.", drops.Count,
                    string.Join(" ; ", drops.Select(d => $"#{d.Index} {d.Reason}{(d.Text is null ? "" : ": " + d.Text)}")));
        }

        var result = MemoryWriterOps.ToResult(ops, stored, owner, _operationFormat is null ? null : message);
        if (_options.DatesFromText)
        {
            // The dates the facts' own words state, as date fields too (the words stay as written). A yearless date is
            // read against when the message was said, not when it is written: a replayed November 2025 turn saying
            // "December 3" means December 2025. One user message is the writer's turn; with several, the last one.
            var saidAt = said.Select(m => m.TimestampUtc).DefaultIfEmpty(now).Max();
            result = result with { Facts = [.. result.Facts.Select(fact => AgentMemory.Core.Memory.FactDateReader.Apply(fact, saidAt))] };
        }
        if (rejected.Count > 0) result = result with { Outcomes = rejected };
        activity?.SetTag("memory.write.operations", ops.Count);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Memory writer: shown [{Stored}]; answered [{Ops}].",
                string.Join(" ; ", stored.Select(s => $"{s.Label} {s.Text}")),
                string.Join(" ; ", ops.Select(o => $"{o.Action}{(o.Id is null ? "" : " " + o.Id)} {o.Kind}: {o.Text}")));
        _logger.LogInformation(
            "Memory writer: {Candidates} stored memories shown; {Ops} operation(s): {Facts} fact(s), {Preferences} preference(s), "
            + "{Entities} entity(ies), {Relationships} relationship(s); {Named} name a stored memory as replaced; {Confirmed} confirm one.",
            stored.Count, ops.Count, result.Facts.Count, result.Preferences.Count, result.Entities.Count, result.Relationships.Count,
            result.Facts.Count(f => f.ReplacesId is not null) + result.Preferences.Count(p => p.ReplacesId is not null)
                + result.Relationships.Count(r => r.ReplacesId is not null),
            result.ConfirmedFactIds.Count + result.ConfirmedPreferenceIds.Count);
        return result;
    }

    /// <summary>
    /// One call; an answer without JSON is asked again with twice the room, and once more at that room, then fails loudly
    /// (the harness's rule): a turn whose model gives no JSON stores nothing and is recorded as failed, never read as
    /// "nothing to keep". The third try (2026-10-10): over 3,634 turns of the Dreaming jar's store runs the first answer held
    /// no JSON 104 times and the second 10, each a model that reasoned through its whole room and answered nothing (12,000
    /// output tokens over the two): a runaway the next call rarely repeats, so one more call saves most of the lost turns.
    /// </summary>
    private async Task<(IReadOnlyList<MemoryWriterOps.Op> Ops, IReadOnlyList<MemoryWriterOps.DroppedOp> Dropped, string Reply)> AskAsync(
        string system, string context, CancellationToken cancellationToken)
    {
        const int Attempts = 3;
        var room = _options.MemoryWriterMaxOutputTokens;
        string? reply = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            ChatMessage[] messages = [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, context)];
            var response = _operationFormat is null
                ? await _runner.CompleteAsync(messages, room, json: false, cancellationToken).ConfigureAwait(false)
                : await _runner.CompleteAsync(messages, room, _operationFormat, cancellationToken).ConfigureAwait(false);
            reply = response.Text;
            if (MemoryWriterOps.TryParse(reply, _options.MemoryWriterMaxOperations, out var ops, out var dropped))
                return (ops, dropped, reply);
            _logger.LogWarning("Memory writer: the reply held no JSON (attempt {Attempt} of {Attempts}, {Room} tokens of room).", attempt, Attempts, room);
            // A schema-constrained reply without JSON is a runaway or a truncation that more room does not cure; the
            // default writer keeps its measured doubling.
            if (attempt == 1 && _operationFormat is null) room *= 2;
        }
        throw new FormatException($"The memory writer's model gave no JSON: {(reply is null ? "" : reply[..Math.Min(160, reply.Length)])}");
    }

    /// <summary>Locates each quote in the message (#249); an operation whose quote is not there is dropped and recorded.</summary>
    private static (List<MemoryWriterOps.Op> Ops, List<MemoryWriterOps.DroppedOp> Drops) Locate(
        IReadOnlyList<MemoryWriterOps.Op> answered, IReadOnlyList<MemoryWriterOps.DroppedOp> dropped, string message)
    {
        var ops = new List<MemoryWriterOps.Op>(answered.Count);
        var drops = new List<MemoryWriterOps.DroppedOp>(dropped);
        foreach (var op in answered)
        {
            if (op.Action == "confirm" || op.Quote is null) { ops.Add(op); continue; }
            if (MemoryWriterQuotes.Locate(message, op.Quote) is { } span) ops.Add(op with { QuoteStart = span.Start, QuoteEnd = span.End });
            else drops.Add(new MemoryWriterOps.DroppedOp(op.Index, MemoryWriterOps.DropReason.QuoteNotFound, op.Text) { Kind = op.Kind });
        }
        return (ops, drops);
    }

    /// <summary>
    /// The event-identity contract (ILC-07): checks the reply, asks one targeted correction when something failed, and
    /// returns what is still wrong as ingestion outcomes. A correction that cannot be read or reached fixes nothing; it
    /// never costs the turn the operations that were already valid. Never a third call.
    /// </summary>
    private async Task<(List<MemoryWriterOps.Op> Ops, List<MemoryWriterOps.DroppedOp> Drops, IReadOnlyList<IngestionItemOutcome> Rejected)> EnforceAsync(
        string system, string context, string reply, List<MemoryWriterOps.Op> ops, List<MemoryWriterOps.DroppedOp> drops,
        IReadOnlyList<MemoryWriterOps.Stored> stored, string? owner, string message, System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken)
    {
        var issues = MemoryWriterContract.Check(ops, drops, stored, owner);
        if (issues.Count == 0) return (ops, [], []);
        var correctable = issues.Where(issue => issue.Reason != MemoryWriterOps.DropReason.OverLimit).ToList();
        IReadOnlyList<MemoryWriterOps.Op> corrected = [];
        if (correctable.Count > 0)
        {
            activity?.SetTag("memory.write.correction_requested", correctable.Count);
            try
            {
                var response = await _runner.CompleteAsync(
                    [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, context),
                     new ChatMessage(ChatRole.Assistant, reply), new ChatMessage(ChatRole.User, MemoryWriterContract.Request(correctable))],
                    _options.MemoryWriterMaxOutputTokens, _operationFormat!, cancellationToken).ConfigureAwait(false);
                if (MemoryWriterOps.TryParse(response.Text, _options.MemoryWriterMaxOperations, out var fixes, out _))
                    corrected = Locate(fixes, [], message).Ops;
                else _logger.LogWarning("Memory writer: the correction reply held no JSON; {Count} problem(s) stay rejected.", correctable.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory writer: the correction call failed; {Count} problem(s) stay rejected.", correctable.Count);
            }
        }
        var (merged, unfixed) = MemoryWriterContract.Merge(ops, issues, corrected, stored, _options.MemoryWriterMaxOperations);
        if (unfixed.Count > 0)
        {
            activity?.SetTag("memory.write.rejected", unfixed.Count);
            _logger.LogWarning("Memory writer: {Count} problem(s) remain after one correction: {Problems}.", unfixed.Count,
                string.Join(" ; ", unfixed.Select(issue => $"#{issue.Index} {issue.Reason}{(issue.Text is null ? "" : ": " + issue.Text)}")));
        }
        return (merged, [], [.. unfixed.Select(MemoryWriterContract.Outcome)]);
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
            var name = await facts.FindLatestObjectAsync(MemoryUserNames.SelfWords, MemoryUserNames.NamingPredicates,
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
    /// taken before the ones already listed are removed); then the live connections of the people found by meaning; then the
    /// other memories of the people the two closest memories name, when few memories name them; at most that many plus six in
    /// all.
    /// </summary>
    /// <summary>How many memories a name from the closest memories may lead to before it counts as too common to follow.</summary>
    private const int AroundPerName = 6;

    private async Task<IReadOnlyList<MemoryWriterOps.Stored>> StoredAsync(
        string text, string? owner, MemoryScope scope, DateTimeOffset now, CancellationToken cancellationToken)
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
        var byMeaning = similar.OrderByDescending(s => s.Score).Select(s => s.Item).DistinctBy(item => item.Id).Take(limit).ToList();

        // 4. The live connections of the people found by meaning, not by their exact name ("Simone and I got married" finds
        // "Simone Gagné"): without them a wedding could not end "Owen -[ENGAGED_TO]-> Simone Gagné" (the scenario suite, H6).
        var foundByMeaning = byMeaning.Where(item => item.Letter == 'E' && named.All(e => e.EntityId != item.Id)).Select(item => item.Id).ToList();
        var theirs = new List<MemoryWriterOps.Stored>();
        if (foundByMeaning.Count > 0)
            theirs.AddRange((await relationships.GetLiveAroundAsync(foundByMeaning, [], 6, now, scope, cancellationToken).ConfigureAwait(false))
                .OrderBy(r => r.Relationship.CreatedAtUtc).Select(MemoryWriterOps.Stored.Of));

        // 5. The other memories of the people the two closest memories name: "We're married!" resembles the wedding plan
        // ("Owen and Simone's wedding"), not "Owen Hnatiuk is engaged to Simone Gagné", which it ends (the suite's H3: the
        // engagement was never shown, so never replaced). Each name is looked up alone and kept only when few memories mention
        // it: a name most of them carry (the person themself) says nothing about this turn.
        var nearby = new List<MemoryWriterOps.Stored>();
        var said = MemoryWriterPrompt.Names(text);
        var theirNames = byMeaning.Where(item => item.Letter == 'F').Take(2)
            .SelectMany(item => MemoryWriterPrompt.Names(item.Text))
            .Where(n => !said.Contains(n, StringComparer.OrdinalIgnoreCase) && !named.Any(e => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
                        && (owner is null || !owner.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        foreach (var name in theirNames)
        {
            var mentioning = (await facts.FindMentioningAsync([name], scope, AroundPerName + 1, cancellationToken).ConfigureAwait(false)).Where(Live).ToList();
            if (mentioning.Count <= AroundPerName) nearby.AddRange(mentioning.Select(MemoryWriterOps.Stored.Of));
        }

        return MemoryWriterOps.Stored.Number(
            [.. named.Select(MemoryWriterOps.Stored.Of).Concat(byName).Concat(byMeaning).Concat(theirs).Concat(nearby)
                .DistinctBy(item => item.Id).Take(limit + 6)]);
    }
}

/// <summary>The writer's prompt: storage round 3's form F plus the documented retention and transition rules.</summary>
internal static class MemoryWriterPrompt
{
    private const string Rules =
        """
        What the assistant keeps (the labellers' rules):
        - A message stores what it tells: a fact, plan, event, preference, person or connection about {p} or {p}'s world.
        - A change names the stored memory it replaces; a correction names the stored memory that was wrong.
        - A new event ends what it changes, and that is a change too: a move ends where they lived, a death ends where and
          how the person lived, a birth ends an expecting, a wedding ends an engagement, a breakup ends a relationship, a
          new job or a retirement ends the old job. Only the person's own event ends their state: someone else's wedding or
          move ends nothing of theirs. Replace the stored memory it ends (a fact or a connection), even when the message
          names the person by a role (mum, my sister) and the stored memory names them by name. When one event ends more
          than one stored memory (the wedding plan and the engagement), write one replace for each.
        - A plan or a state true only for a day or a short while (tonight's plan, today's ailment or mood, this weekend's
          stay) is stored with "until": the last date it holds. Something that happened (the dog stole the butter this
          morning, she asked me to braid her hair today) stays true: store it with its date, never with "until".
        - A question stores nothing, unless it also tells something ("I'm off to Seville on the 13th, what should I pack?" stores
          the trip and its date).
        - Separate a personal aspiration from its emotional wording. Wanting to contribute or make an impact in a named
          activity is a goal even without a date, concrete steps or a promise. Keep it as a wish, aim or value, not as
          an activity already started or a firm commitment. A stored taste does not already cover a new goal.
        - Small talk, thanks, greetings, a passing mood or reaction, the request itself and general knowledge store nothing.
          Praise or excitement alone is not a personal goal. Earlier assistant suggestions alone are not the person's
          intentions; keep only what the last user message actually says about their wishes, plans, values or facts.
        - What is already stored is not stored again, in any words: when the last message says it again and adds nothing,
          confirm it. When it says it again with anything more (a reason, a goal, a detail, a number, a name, a date),
          replace the stored memory with the fuller one.
        - A question or a request often tells something in passing: a plan ("what should I bring when I visit my cousin in Porto
          next month?"), someone's wish or need ("my neighbour wants to borrow the ladder, is it still in the shed?"), an
          appointment, a change. Keep that part as a memory; never the question or the request itself.
        """;

    private const string Writer =
        """
        You write {p}'s long-term memory of one kind: {kind_name} = {what}.
        Look only at {p}'s LAST message; earlier turns are context. {rules}
        Operations:
        - add: something new that is not stored yet (check the STORED list); "until": "YYYY-MM-DD" when it holds only until then;
        - replace: the new value changes a stored memory that stops being true now (moved, new job, quit, changed plans): give its id;
        - correct: a stored memory was wrong all along: give its id;
        - confirm: the last message says again what a stored memory already says, adding nothing: give its id (it is not
          stored again; it counts as said again).
        Every operation quotes the exact words of the last message it rests on. One memory per thing told; at most {max_ops}.
        Format of "text": {fmt}. For kind person, set "kind" to "entity" or "relationship" on each operation.
        Answer with JSON only: {"ops": [{"op": "add", "kind": "{kind_default}", "text": "...", "quote": "..."},
        {"op": "replace", "id": "F12", "kind": "{kind_default}", "text": "...", "quote": "..."},
        {"op": "confirm", "id": "P3", "quote": "..."}]} or {"ops": []}.
        """;

    private const string EventEntityWhat =
        "a named entity or a connection explicitly stated in the message; do not infer connections. "
        + "Emit a current EVENT observation with each new fact about its date, place or participation, even for an already stored event. "
        + "A person's own event participation is still a fact about that event. Other already stored entity mentions need no duplicate.";

    private const string EventEntityFormat =
        "an entity as \"Name (PERSON|PLACE|ORGANIZATION|THING|EVENT)\"; a connection as \"Name -[RELATION]-> Name\"";

    private const string EventFactFormat =
        "\"subject | predicate | object\"; use the exact named event as subject for its date, location or participation "
        + "requirement/permission, including first-person statements about attending or running it. "
        + "For other facts about {p}, use {s} as subject. Keep stated names, dates and negation.";

    internal const string EventIdentityInstructions = """

        Explicit event references:
        - A named event is an entity operation with kind "entity" and text "Exact event name (EVENT)".
        - Whenever you add facts about a named event, also emit its EVENT observation, even if a stored entity has that name.
          This records the current mention; it does not assert that two observations are the same identity.
        - Put explicitly stated date, place and participation requirements in separate atomic facts whose subject is the
          exact event name. Use shapes such as "<event name> | is scheduled for | <stated date>",
          "<event name> | takes place at | <stated location>", and
          "<event name> | participation | <stated requirement or permission>". Replace the placeholders only with supported
          words from the message; never output the placeholders or invent a date, location or presence requirement.
        - Keep the owner's involvement separately, for example "user | leads | <event name>". Do not bury the only event
          reference inside a longer object sentence. Keep date words in the dated fact; do not copy that date onto place
          or presence facts unless the current statement explicitly dates them too.
        - Use an existing event name only when the current statement and visible context unambiguously refer to it.
          Similar names, dates, shared people or places alone never establish identity. If the reference is ambiguous,
          preserve the stated words and uncertainty rather than inventing a connection or transferring another event's facts.
        - Event date, location and participation facts are the explicit exception to the general owner-subject and
          already-stored-mention rules. Before returning JSON, check that each such new fact has its current EVENT
          observation and the event as subject. A first-person attendance requirement/permission is an event participation
          fact; keep the participant and any negation in its object. Do not replace it solely with a user-subject sentence.
          If context cannot resolve the event reference unambiguously, preserve uncertainty instead of guessing a link.
        - Every emitted item must remain supported by the current message. Keep its source quote rules and operation cap.

        Stored entity references:
        - Every operation has a "ref" field; on facts, preferences and connections it is always null. An entity's "text" is
          exactly "Name (TYPE)", nothing before or after. When an entity operation is about a person, organisation, place, thing or event
          already listed in STORED as an E item, and the current statement and visible context unambiguously refer to that
          stored entity, set "ref" to its E label and use that stored entity's exact name and kind in "text". Its quote is
          the words of the last message that refer to it, even when they name it differently.
        - Otherwise set "ref" to null: for a similar name, the same date, a shared person or place alone, or when two stored
          entities could match. A "ref" only says which stored entity this mention is about; it never replaces or corrects
          a stored memory, and facts about that entity use the same exact name as their subject.
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
    internal static string System(string? owner, int maxOperations, bool eventIdentities = false)
    {
        var p = owner ?? "the user";
        var s = owner ?? "user";
        string Fill(string template) => template.Replace("{p}", p, StringComparison.Ordinal).Replace("{s}", s, StringComparison.Ordinal);
        var what = "\n" + string.Join("\n", Kinds.Select(k => $"- {k.Kind}: {Fill(eventIdentities && k.Kind == "person" ? EventEntityWhat : k.What)}"));
        var formats = string.Join("; ", Kinds.Select(k => $"{k.Kind}: {Fill(eventIdentities && k.Kind == "fact" ? EventFactFormat : eventIdentities && k.Kind == "person" ? EventEntityFormat : k.Format)}"));
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
    /// <summary>One operation: add, replace, correct or confirm; its kind, text and (for all but add) the stored id it names.</summary>
    internal sealed record Op(string Action, string Kind, string Text, string? Id, string? Until = null)
    {
        /// <summary>The words of the message the operation says it rests on (#249), as the model gave them.</summary>
        public string? Quote { get; init; }

        /// <summary>Where <see cref="Quote"/> stands in the message once located: start (inclusive) and end (exclusive).</summary>
        public int? QuoteStart { get; init; }

        /// <inheritdoc cref="QuoteStart"/>
        public int? QuoteEnd { get; init; }

        /// <summary>The operation's position in the reply's "ops" list, from 0.</summary>
        public int Index { get; init; }

        /// <summary>
        /// The label of a stored entity the writer was shown (E2) that an entity operation says it is about, as the model
        /// gave it. Read only in event-identity mode, and kept only after it is checked against the labels shown.
        /// </summary>
        public string? Ref { get; init; }
    }

    /// <summary>Why an operation in a reply was not kept (#250).</summary>
    internal enum DropReason
    {
        /// <summary>An entry of "ops" that is not a JSON object.</summary>
        NotAnObject,

        /// <summary>No "op", or one that is not add, replace, correct or confirm.</summary>
        UnknownAction,

        /// <summary>An add, replace or correct without "text".</summary>
        MissingText,

        /// <summary>A confirm without "id".</summary>
        MissingId,

        /// <summary>An entry past the most operations a reply may hold.</summary>
        OverLimit,

        /// <summary>A quote the message does not contain (#249).</summary>
        QuoteNotFound,

        /// <summary>A "ref" that is not the label of a stored entity shown in this call, or on an operation other than an entity add (the entity is kept without it).</summary>
        ReferenceInvalid,

        /// <summary>A "ref" on an operation with no located quote to rest on (the entity is kept without it).</summary>
        ReferenceWithoutQuote,

        /// <summary>A "ref" whose operation names the entity differently from the stored entity it refers to (the entity is kept without it).</summary>
        ReferenceNameMismatch,

        /// <summary>A fact about a named event with no entity operation observing that event in the same reply.</summary>
        EventObservationMissing,

        /// <summary>An entity whose text is not exactly "Name (TYPE)" (a fact, a list or JSON debris): never coerced into an OBJECT.</summary>
        EntityFormatInvalid,

        /// <summary>A fact, preference or connection whose text holds a line break, a code fence or embedded operation JSON.</summary>
        TextFormatInvalid,
    }

    /// <summary>An operation the writer did not keep, where it was in the reply, why, and its text when it had one.</summary>
    internal sealed record DroppedOp(int Index, DropReason Reason, string? Text)
    {
        /// <summary>The kind the operation said it was (fact, preference, entity, relationship), when it said one.</summary>
        public string? Kind { get; init; }
    }

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

    private static readonly string[] Actions = ["add", "replace", "correct", "confirm"];
    private static readonly string[] ItemKinds = ["fact", "preference", "entity", "relationship"];

    /// <summary>
    /// The operations in a reply, or false when it holds no JSON object. As the harness (ask_json, then keep and clean): any
    /// JSON object is an answer, and its "ops" list, when there is one, holds the operations; the first
    /// <paramref name="max"/> entries are read, and those that are not an add, replace or correct with a text, or a confirm with
    /// an id, are dropped.
    /// </summary>
    internal static bool TryParse(string? reply, int max, out IReadOnlyList<Op> ops) => TryParse(reply, max, out ops, out _);

    /// <summary>
    /// As <see cref="TryParse(string?, int, out IReadOnlyList{Op})"/>, with the entries it did not keep and why (#250): a
    /// reply whose facts are real but mis-shaped no longer loses them without a trace.
    /// </summary>
    internal static bool TryParse(string? reply, int max, out IReadOnlyList<Op> ops, out IReadOnlyList<DroppedOp> dropped)
    {
        ops = [];
        dropped = [];
        var json = LlmExtractionRunner.ExtractJson(reply);
        if (json is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("ops", out var list) || list.ValueKind != JsonValueKind.Array) return true;
            var kept = new List<Op>();
            var lost = new List<DroppedOp>();
            var index = -1;
            foreach (var o in list.EnumerateArray())
            {
                index++;
                var kind = o.ValueKind == JsonValueKind.Object ? Str(o, "kind")?.Trim().ToLowerInvariant() : null;
                kind = kind is not null && ItemKinds.Contains(kind) ? kind : null;
                if (index >= max) { lost.Add(new DroppedOp(index, DropReason.OverLimit, o.ValueKind == JsonValueKind.Object ? Str(o, "text") : null) { Kind = kind }); continue; }
                if (o.ValueKind != JsonValueKind.Object) { lost.Add(new DroppedOp(index, DropReason.NotAnObject, null)); continue; }
                var action = Str(o, "op")?.Trim().ToLowerInvariant();
                var text = Str(o, "text")?.Trim();
                var id = Str(o, "id")?.Trim();
                if (action is null || !Actions.Contains(action)) { lost.Add(new DroppedOp(index, DropReason.UnknownAction, text) { Kind = kind }); continue; }
                if (action == "confirm" && string.IsNullOrEmpty(id)) { lost.Add(new DroppedOp(index, DropReason.MissingId, text) { Kind = kind }); continue; }
                if (action != "confirm" && string.IsNullOrEmpty(text)) { lost.Add(new DroppedOp(index, DropReason.MissingText, null) { Kind = kind }); continue; }
                var quote = Str(o, "quote");
                var reference = Str(o, "ref")?.Trim();
                kept.Add(new Op(action, kind ?? "fact", text ?? "", id, Str(o, "until")?.Trim())
                {
                    Quote = string.IsNullOrWhiteSpace(quote) ? null : quote,
                    Index = index,
                    Ref = string.IsNullOrEmpty(reference) ? null : reference,
                });
            }
            ops = kept;
            dropped = lost;
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
    /// "Name (TYPE)", a connection from "A -[RELATION]-> B". A replace or correct names the stored memory it closes: a stored
    /// fact or connection for a fact (a wedding ends the engagement, 2026-10-10: world 9 kept "Owen -[ENGAGED_TO]-> Simone"
    /// live after the wedding in all three stores, the writer having no way to end it), a stored preference for a
    /// preference, a stored connection for a connection; a connection-shaped replacement of a fact remains a fact so its
    /// target is preserved without requiring separately persisted entity endpoints. Anything else is simply added.
    /// </summary>
    internal static UnifiedExtractionResult ToResult(IReadOnlyList<Op> ops, IReadOnlyList<Stored> stored, string? owner, string? referenceMessage = null)
    {
        var byLabel = stored.ToDictionary(s => s.Label, StringComparer.OrdinalIgnoreCase);
        var facts = new List<ExtractedFact>();
        var preferences = new List<ExtractedPreference>();
        var entities = new List<ExtractedEntity>();
        var relationships = new List<ExtractedRelationship>();
        var confirmedFacts = new List<string>();
        var confirmedPreferences = new List<string>();
        foreach (var op in ops)
        {
            var target = op.Action != "add" && op.Id is not null && byLabel.TryGetValue(op.Id, out var named) ? named : null;
            if (op.Action == "confirm")
            {
                // Said again: the stored fact or preference it names is reinforced and nothing is written (an entity or a
                // connection said again needs nothing, as the prompt says of a mere mention).
                if (target?.Letter == 'F') confirmedFacts.Add(target.Id);
                else if (target?.Letter == 'P') confirmedPreferences.Add(target.Id);
                continue;
            }
            if (Connection.Match(op.Text) is { Success: true } link)
            {
                if (target?.Letter == 'F')
                {
                    facts.Add(CreateFact(op, target, link.Groups["s"].Value,
                        link.Groups["r"].Value.Trim().ToLowerInvariant().Replace('_', ' '), link.Groups["t"].Value));
                    continue;
                }
                relationships.Add(new ExtractedRelationship
                {
                    SourceEntity = link.Groups["s"].Value,
                    TargetEntity = link.Groups["t"].Value,
                    RelationshipType = link.Groups["r"].Value.Trim().ToUpperInvariant().Replace(' ', '_'),
                    ReplacesId = target?.Letter == 'R' ? target.Id : null,
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
                        SourceQuoteStart = op.QuoteStart, SourceQuoteEnd = op.QuoteEnd,
                    });
                    break;
                case "entity":
                    var entity = Named.Match(op.Text);
                    entities.Add(new ExtractedEntity
                    {
                        Name = entity.Success ? entity.Groups["n"].Value : op.Text.Trim(),
                        Type = entity.Success ? EntityType(entity.Groups["t"].Value) : "OBJECT",
                        // Event-identity mode only (the message is passed): a checked label shown in this call, never a guess.
                        Reference = referenceMessage is null ? null : MemoryWriterContract.Reference(op, byLabel, referenceMessage),
                    });
                    break;
                case "relationship":
                    break;   // a connection the text did not state as "A -[RELATION]-> B" cannot be stored as one
                default:
                    var parts = FactText(op.Text).Split('|', 3, StringSplitOptions.TrimEntries);
                    var (subject, predicate, @object) = parts.Length switch
                    {
                        3 => (parts[0], parts[1], parts[2]),
                        2 => (parts[0], "is", parts[1]),
                        _ => (owner ?? "user", "noted", op.Text.Trim()),
                    };
                    if (subject.Length == 0 || predicate.Length == 0 || @object.Length == 0) break;
                    facts.Add(CreateFact(op, target, subject, predicate, @object));
                    break;
            }
        }
        return new UnifiedExtractionResult
        {
            Facts = facts, Preferences = preferences, Entities = entities, Relationships = relationships,
            ConfirmedFactIds = [.. confirmedFacts.Distinct(StringComparer.Ordinal)],
            ConfirmedPreferenceIds = [.. confirmedPreferences.Distinct(StringComparer.Ordinal)],
        };
    }

    private static ExtractedFact CreateFact(Op op, Stored? target, string subject, string predicate, string value) => new()
    {
        Subject = subject, Predicate = predicate, Object = value, SourceRole = "user",
        ReplacesId = target?.Letter is 'F' or 'R' ? target.Id : null,
        ReplacementIsCorrection = (target?.Letter is 'F' or 'R') && op.Action == "correct",
        ValidUntil = EndOfDay(op.Until),
        ValidUntilPrecision = EndOfDay(op.Until) is null ? DatePrecision.Unspecified : DatePrecision.Day,
        SourceQuoteStart = op.QuoteStart,
        SourceQuoteEnd = op.QuoteEnd,
    };

    // Models can copy the prompt's format label and outer quotes into the JSON text value.
    // Remove only that complete triple wrapper; quotes within a field remain meaningful content.
    private static string FactText(string text)
    {
        const string Prefix = "fact: \"";
        if (text.Length <= Prefix.Length || !text.StartsWith(Prefix, StringComparison.Ordinal) || !text.EndsWith('"')) return text;
        var inner = text[Prefix.Length..^1];
        var parts = inner.Split('|', 3, StringSplitOptions.TrimEntries);
        return parts.Length == 3 && parts.All(p => p.Length > 0) ? inner : text;
    }

    /// <summary>
    /// The end of the day an "until" names (a one-off: tonight's plan holds until the end of that day), in UTC; null when
    /// there is none or it is not a date.
    /// </summary>
    internal static DateTimeOffset? EndOfDay(string? until) =>
        DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
            ? new DateTimeOffset(day.Year, day.Month, day.Day, 23, 59, 59, TimeSpan.Zero)
            : null;

    /// <summary>The prompt's PLACE and THING in the library's entity vocabulary (LOCATION, OBJECT).</summary>
    private static string EntityType(string type) => type.Trim().ToUpperInvariant() switch
    {
        "PLACE" => "LOCATION",
        "THING" => "OBJECT",
        "ORGANISATION" => "ORGANIZATION",
        var other => other.Replace(' ', '_'),
    };
}
