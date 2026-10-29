using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Diagnostics;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;

namespace AgentMemory.Extensibility.Context.Traversal;

/// <summary>
/// A record a walk reached: its score and how it was reached, how many of the question's words its text holds, and how specific
/// the last link to it was (1 over how many records share that link).
/// </summary>
[Experimental("AMEXT001")]
internal sealed record TraversalItem(ModuleRecord Record, double Score, string Via, int Hops, int Matches = 0, double Specificity = 1)
{
    /// <summary>The order a walk offers records in: the path's score, raised by each question word the record's text holds.</summary>
    internal double Rank => Score * (1 + Matches);
}

/// <summary>
/// What one walk spent against its budgets (red-team M3): store reads, records visited, time, and which budget stopped it, if
/// one did. Reported with every walk, so a host can see that a pack's declared bounds held.
/// </summary>
[Experimental("AMEXT001")]
internal sealed record TraversalSpend(int Reads, int Visited, TimeSpan Elapsed, string? StoppedBy);

/// <summary>What one walk reached, best first, the reads it could not make, and what it spent.</summary>
[Experimental("AMEXT001")]
internal sealed record TraversalResult(IReadOnlyList<TraversalItem> Items, IReadOnlyList<string> Problems, TraversalSpend? Spent = null)
{
    internal static readonly TraversalResult Empty = new([], []);
}

/// <summary>
/// The traversal walk (CMP-1): anchors match records through declared anchor properties; declared links are followed for at
/// most the hops each module declares (two by default, three at most); each reached record keeps its best score (anchor quality
/// times the weights along the path) and that path. Bounded in hops, fan-out, visited records and a score floor, so a hub
/// cannot flood the gate.
/// </summary>
/// <remarks>
/// <b>Budgets bound the work, not only the result (red-team M3).</b> A walk stops at <see cref="MaximumVisited"/> records, at
/// <see cref="MaximumReads"/> store reads, or after <see cref="TimeBudget"/>, whichever comes first, and it stops everywhere, not
/// only in the relation it was expanding. One pack holds at most <see cref="MaximumVisitedPerModule"/> of the visited records, so a
/// pack earlier in dependency order cannot fill the budget at its first hop and starve the others' later hops. A record with
/// more than <see cref="ModuleExpandQuery.MaximumLinks"/> live links of a relation is not expanded through it (the store checks
/// its degree first), so a hub's degree no longer sets the cost; within that, every link is ranked (review 2).
/// <b>Reasons are built from relation verbs and record names, paths or keys</b> in a strict character set (red-team M6): a
/// record's free text never enters its reason.
/// </remarks>
/// <remarks>
/// <b>The question orders what the anchors reach (CMP-4).</b> A type can have hundreds of tests, all one link from it and all with
/// the same score; which few reach the gate was decided by record id. Now an expansion keeps first the records whose text holds
/// the question's words, then the most specific (a test linked to one type over one linked to twelve), and the walk offers records
/// by score raised per matched word, then specificity. Measured on this repository's own history (40 changes, and 40 older ones
/// held out while tuning): the share of changes whose own tests were offered rose from 0.375 to 0.775, and 0.80 held out.
/// </remarks>
[Experimental("AMEXT001")]
internal static class TraversalWalk
{
    internal const int MaximumHops = ModuleTraversal.MaximumHops;
    internal const int MaximumVisited = 256;
    internal const int MaximumVisitedPerModule = 192;
    internal const int MaximumReads = 128;
    internal static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The anchors a walk takes (pack format v1.2 §6.1), the same for every entry point (review 3: serve cut to 32 before the
    /// duplicates went; review 5: every step here, for every entry point): in request order, each value normalized as a path
    /// or a symbol (<see cref="ContextAnchor.Normalized"/>), then in NFC by the format's own Unicode 15 tables, whatever the host's
    /// globalization mode (review 4: under invariant globalization .NET left NFD text as it was), a value with an unpaired
    /// surrogate dropped, those not blank (ASCII white space) and at most 512 characters,
    /// distinct by kind and value, the first 32.
    /// </summary>
    internal static List<ContextAnchor> Anchors(IEnumerable<ContextAnchor> anchors)
    {
        var taken = new List<ContextAnchor>();
        var seen = new HashSet<(ContextAnchorKind, string)>();
        foreach (var anchor in anchors)
        {
            if (anchor?.Value is not { } raw) continue;
            // Bound before the work (review 5: a long run of combining marks cost seconds to normalize): NFC shortens UTF-16
            // text at most 4 to 1 under Unicode 15, so a value over 4 x 512 units cannot be 512 or fewer after it.
            if (raw.Length > 4 * ContextAnchor.MaximumLength) continue;
            // Every step here, whichever entry point built the anchor (review 5: the factories normalized, the constructor did not).
            var value = ContextAnchor.Normalized(anchor.Kind, raw);
            if (value.Length == 0 || Unicode.Nfc.Normalize(value) is not { } nfc || nfc.Length > ContextAnchor.MaximumLength) continue;
            if (!seen.Add((anchor.Kind, nfc))) continue;
            taken.Add(nfc == anchor.Value ? anchor : anchor with { Value = nfc });
            if (taken.Count == ContextAnchor.MaximumPerRequest) break;
        }
        return taken;
    }


    internal const double MinimumScore = 0.1;

    // timeBudget: TimeBudget unless a test sets a shorter one.
    // live: whether a module may take part now (review 1, M2: a pack past its validUntil neither seeds, steers nor offers).
    internal static async Task<TraversalResult> RunAsync(ModuleResolution resolution, IModuleTraversalReader reader, ContextRequest request,
        CancellationToken cancellationToken, TimeSpan? timeBudget = null, Func<string, bool>? live = null)
    {
        var anchors = Anchors(request.Anchors);
        if (anchors.Count == 0) return TraversalResult.Empty;
        var context = new ModuleReadContext
        {
            Scope = request.ResolvedScope ?? (request.Owner is null ? null : MemoryScope.For(request.Owner)),
            SessionId = request.SessionId, SystemAsOfUtc = request.SystemAsOf
        };
        var modules = resolution.Modules.Where(m => m.Traversal is not null && m.Ontology is not null && (live?.Invoke(m.Id) ?? true)).ToList();
        var best = new Dictionary<string, TraversalItem>(StringComparer.Ordinal);
        var perModule = new Dictionary<string, int>(StringComparer.Ordinal);
        var problems = new List<string>();
        var clock = Stopwatch.StartNew();
        var budget = timeBudget ?? TimeBudget;
        // The time budget also bounds a read in progress: a slow store stops the walk, not only the next read (PACK-review 1.5).
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(budget);
        var reads = 0;
        string? stoppedBy = null;
        bool Spent()
        {
            stoppedBy ??= best.Count >= MaximumVisited ? "visited" : reads >= MaximumReads ? "reads" : clock.Elapsed > budget ? "time" : null;
            return stoppedBy is not null;
        }
        async Task<T?> Within<T>(Func<CancellationToken, Task<T>> read) where T : class
        {
            try { return await read(timer.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stoppedBy ??= "time";
                return null;
            }
        }
        bool Offer(TraversalItem item)
        {
            if (live is not null && !live(item.Record.ModuleId)) return false;
            if (best.TryGetValue(item.Record.Id, out var known))
            {
                if (known.Score >= item.Score) return false;
                best[item.Record.Id] = item;
                return true;
            }
            var held = perModule.GetValueOrDefault(item.Record.ModuleId);
            if (held >= MaximumVisitedPerModule || best.Count >= MaximumVisited) return false;
            perModule[item.Record.ModuleId] = held + 1;
            best[item.Record.Id] = item;
            return true;
        }
        var words = TraversalWords.Of(request.Query);
        var texts = new Dictionary<(string, string), IReadOnlyList<string>>();
        int Matches(ModuleRecord record)
        {
            if (words.Count == 0) return 0;
            if (!texts.TryGetValue((record.ModuleId, record.Type), out var properties))
                texts[(record.ModuleId, record.Type)] = properties = resolution.Modules.SingleOrDefault(m => m.Id == record.ModuleId)?.Ontology?.Types
                    .SingleOrDefault(t => t.Name == record.Type)?.TextProperties() ?? [];
            var text = string.Join(' ', properties.Select(p => record.Properties.TryGetValue(p, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null));
            text = AnchorMatcher.Folded(text);
            return words.Count(w => text.Contains(w, StringComparison.Ordinal));
        }

        // Seeds: records whose anchor properties match the anchors.
        foreach (var module in modules)
            foreach (var declared in module.Traversal!.Anchors)
            {
                var values = anchors.Where(a => a.Kind == declared.Kind).Select(a => a.Value).ToList();
                if (values.Count == 0) continue;
                if (Spent()) break;
                reads++;
                var found = await Within(token => reader.MatchAsync(new ModuleAnchorQuery
                {
                    ModuleId = module.Id, Type = declared.Type, Property = declared.Property, Match = declared.Match, Values = values,
                    Limit = ModuleAnchorQuery.MaximumLimit,
                }, context, token)).ConfigureAwait(false);
                if (found is null) break;
                if (found.Status != ModuleReadStatus.Succeeded) { problems.AddRange(found.Errors.Select(e => module.Id + ": " + e)); continue; }
                if (found.Records.Count >= ModuleAnchorQuery.MaximumLimit)
                    problems.Add($"{module.Id}: more than {ModuleAnchorQuery.MaximumLimit} records may match {declared.Type}.{declared.Property}; the rest are not seeds.");
                // Every record that matches, offered best match first and then in the format's order (review 2: offered in the
                // store's order, which differed between stores, so which records the caps kept differed too).
                var seeds = new List<TraversalItem>();
                foreach (var record in found.Records)
                {
                    var value = record.Properties.TryGetValue(declared.Property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var (quality, anchor, pattern) = AnchorMatcher.Best(declared.Match, value, values);
                    if (quality <= 0) continue;
                    // The pattern is the anchor when it names the same path (or, for a name, the same text): then the reason is the anchor alone.
                    var same = declared.Match == ModuleAnchorMatch.Name
                        ? string.Equals(AnchorMatcher.Trimmed(pattern!), AnchorMatcher.Trimmed(anchor!), StringComparison.Ordinal)
                        : AnchorMatcher.SamePath(pattern!, anchor!);
                    var via = Short(anchor!) + (same ? "" : " matches " + declared.Property + " " + Capped(pattern!));
                    seeds.Add(new TraversalItem(record, quality, via, 0, Matches(record)));
                }
                foreach (var seed in seeds.OrderByDescending(i => i.Score).ThenBy(i => i.Record, TraversalOrder.Records))
                {
                    // At 256 kept records the walk stops, here as in the hops (review 3: the seeds went on raising scores).
                    if (best.Count >= MaximumVisited) break;
                    Offer(seed);
                }
            }

        // Hops: owned links, in their declared direction, from what the last hop reached.
        var frontier = best.Values.ToList();
        var hops = modules.Count == 0 ? 0 : Math.Min(MaximumHops, modules.Max(m => m.Traversal!.Hops));
        for (var hop = 1; hop <= hops && frontier.Count > 0 && !Spent(); hop++)
        {
            var next = new List<TraversalItem>();
            foreach (var module in modules.Where(m => hop <= m.Traversal!.Hops))
                foreach (var declared in module.Traversal!.Relations)
                {
                    if (Spent()) break;
                    var relation = module.Ontology!.Relations.Single(r => r.Name == declared.Name);
                    if (Consent(resolution, module, relation) is { } refusal)
                    {
                        if (hop == 1) problems.Add(refusal);
                        continue;
                    }
                    var start = declared.Direction == ModuleTraversalDirection.Out ? (module.Id, relation.From) : Target(module, relation);
                    if (start is not { } from) continue;
                    var starting = frontier.Where(i => i.Record.ModuleId == from.Module && i.Record.Type == from.Type)
                        .GroupBy(i => i.Record.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.MaxBy(i => i.Score)!, StringComparer.Ordinal);
                    if (starting.Count == 0) continue;
                    reads++;
                    var step = await Within(token => reader.ExpandAsync(new ModuleExpandQuery
                    {
                        ModuleId = module.Id, Relation = declared.Name, Direction = declared.Direction,
                        FromIds = [.. starting.Keys.Order(StringComparer.Ordinal)], FanOut = declared.FanOut, Words = words
                    }, context, token)).ConfigureAwait(false);
                    if (step is null) break;
                    if (step.Status != ModuleReadStatus.Succeeded) { problems.AddRange(step.Errors.Select(e => module.Id + ": " + e)); continue; }
                    // A record past the store's link limit is not expanded, and the walk says so.
                    problems.AddRange(step.Errors.Select(e => module.Id + ": " + e));
                    var reached = new List<(TraversalItem Item, ModuleRecord From)>();
                    foreach (var edge in step.Edges)
                    {
                        if (!starting.TryGetValue(edge.FromId, out var parent)) continue;
                        var score = parent.Score * declared.Weight;
                        if (score < MinimumScore) continue;
                        var verb = Verb(module, declared.Name);
                        var via = parent.Via + " → " + (declared.Direction == ModuleTraversalDirection.Out
                            ? verb + " " + Label(edge.To) : Label(edge.To) + " " + verb + " it");
                        reached.Add((new TraversalItem(edge.To, score, via, hop, Matches(edge.To), 1.0 / Math.Max(1, edge.Degree)), parent.Record));
                    }
                    // Offered in the format's order, whatever order the store answered in (review 2).
                    foreach (var (item, _) in reached.OrderByDescending(r => r.Item.Score).ThenByDescending(r => r.Item.Matches)
                                 .ThenByDescending(r => r.Item.Specificity).ThenBy(r => r.Item.Record, TraversalOrder.Records).ThenBy(r => r.From, TraversalOrder.Records))
                    {
                        if (Offer(item)) next.Add(item);
                        if (best.Count >= MaximumVisited) break;
                    }
                }
            frontier = next;
        }
        Spent(); // a stop is reported in the spend, not as a problem: it denies nothing
        var spent = new TraversalSpend(reads, best.Count, clock.Elapsed, stoppedBy);
        // Reported with every walk, on the contribution's activity: a host sees that a pack's declared bounds held.
        Activity.Current?.SetTag(ModuleTelemetry.TraversalReads, spent.Reads)
            .SetTag(ModuleTelemetry.TraversalVisited, spent.Visited)
            .SetTag(ModuleTelemetry.TraversalElapsedMs, Math.Round(spent.Elapsed.TotalMilliseconds, 1))
            .SetTag(ModuleTelemetry.TraversalStoppedBy, spent.StoppedBy ?? "none");
        return new([.. best.Values.OrderByDescending(i => i.Rank).ThenByDescending(i => i.Specificity).ThenBy(i => i.Hops)
            .ThenBy(i => i.Record, TraversalOrder.Records)], problems.AsReadOnly(), spent);
    }

    /// <summary>
    /// Why a walk may not follow <paramref name="relation"/>, or null when it may: a link into another module needs that
    /// module's consent, its <see cref="ModuleManifest.ContainableBy"/> naming this one (red-team M4).
    /// </summary>
    internal static string? Consent(ModuleResolution resolution, ModuleManifest module, ModuleRelation relation) =>
        resolution.ConsentRefusal(module, relation);

    private static (string Module, string Type)? Target(ModuleManifest module, ModuleRelation relation)
    {
        if (relation.To.StartsWith("core:", StringComparison.Ordinal)) return null;   // core entities are not walked in phase A
        var parts = relation.To.Split(':');
        return parts.Length == 2 ? (parts[0], parts[1]) : (module.Id, relation.To);
    }

    // DECISIONS_SUPERSEDES → "supersedes", CODEMAP_TESTS → "tests".
    private static string Verb(ModuleManifest module, string relation)
    {
        var prefix = ModuleManifestValidator.SchemaName(module.Ontology!.LabelPrefix).ToUpperInvariant() + "_";
        var name = relation.StartsWith(prefix, StringComparison.Ordinal) ? relation[prefix.Length..] : relation;
        return name.Replace('_', ' ').ToLowerInvariant();
    }

    // A record's name for a reason: its name or path when it has one, else its key, shortened and kept to a strict character
    // set. Never its title or other free text: a signed pack is not a safe one, and the reason reaches the model (red-team M6).
    private static string Label(ModuleRecord record)
    {
        foreach (var property in (string[])["name", "path"])
            // Not blank after trimming (review 5: a name of spaces gave an empty reason).
            if (record.Properties.TryGetValue(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !AnchorMatcher.IsBlank(text))
                return Short(text);
        return Short(record.Key);
    }

    // Every path ends in Strict() and the 80-character cap: a symbol's simple name, a path's last segment, or the text itself.
    internal static string Short(string value)
    {
        var text = AnchorMatcher.Trimmed(value);
        // A symbol when it looks like one and has a simple name (review 4: one without kept the whole text, not the path rule).
        if ((text.Contains(' ') && text.Contains('#') || text.EndsWith('#') || text.EndsWith(')') || text.EndsWith('.'))
            && AnchorMatcher.SimpleName(text) is { Length: > 0 } simple)
            text = simple;
        else
        {
            var slash = text.LastIndexOf('/');
            text = slash >= 0 && slash < text.Length - 1 ? text[(slash + 1)..] : text;
        }
        text = Strict(text);
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    // A pattern in a reason: strict and at most 80 characters, kept whole otherwise (review 2: a name match put whole SCIP
    // symbols, or any stored value, into reasons).
    internal static string Capped(string value)
    {
        var text = Strict(AnchorMatcher.Trimmed(value));
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    /// <summary>Letters, digits, spaces and the punctuation of paths, symbols and globs; anything else becomes '_'.</summary>
    internal static string Strict(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsAsciiLetterOrDigit(source[i]) || StrictPunctuation.Contains(source[i]) ? source[i] : '_';
        });

    private const string StrictPunctuation = "._-/:#*()<>,+@ ";
}

/// <summary>
/// The format's order of records (pack format v1.2 §6.1): module id, then type, then key, each by UTF-16 code unit, then the
/// version id. Every tie in a walk is broken by it, in every store, so two hosts offer the same records (review 2: ties were
/// broken by store-internal ids, which differ between stores).
/// </summary>
[Experimental("AMEXT001")]
internal static class TraversalOrder
{
    internal static readonly IComparer<ModuleRecord> Records = Comparer<ModuleRecord>.Create(static (a, b) =>
    {
        var c = string.CompareOrdinal(a.ModuleId, b.ModuleId);
        if (c == 0) c = string.CompareOrdinal(a.Type, b.Type);
        if (c == 0) c = string.CompareOrdinal(a.Key, b.Key);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    });
}

/// <summary>One walk per request, shared by every module's traversal contributor in it (CMP-1).</summary>
[Experimental("AMEXT001")]
internal sealed class TraversalService(ModuleResolution resolution, IModuleTraversalReader? reader, Func<string, bool>? live = null)
{
    private readonly ConditionalWeakTable<ContextRequest, Task<TraversalResult>> _walks = new();

    /// <summary>Whether the store can walk at all.</summary>
    internal bool Available => reader is not null;

    internal Task<TraversalResult> WalkAsync(ContextRequest request, CancellationToken cancellationToken) =>
        reader is null || request.Anchors.Count == 0
            ? Task.FromResult(TraversalResult.Empty)
            : _walks.GetValue(request, r => TraversalWalk.RunAsync(resolution, reader, r, cancellationToken, live: live));
}

/// <summary>
/// A question's words as traversal ranks by them (CMP-4): identifiers split where the case changes, lower case, three characters
/// or more, without common English words and task verbs, which every record's text would share; at most
/// <see cref="ModuleExpandQuery.MaximumWords"/>, in the question's order.
/// </summary>
[Experimental("AMEXT001")]
internal static partial class TraversalWords
{
    private static readonly System.Collections.Frozen.FrozenSet<string> Common = System.Collections.Frozen.FrozenSet.ToFrozenSet(
        "the and for with from that this into when are not but its has have was were make made add adds fix fixes test tests one two three all any only now new also than then them they their our out off"
            .Split(' '), StringComparer.Ordinal);

    // The question in NFC by the format's own tables first (review 5: "cafe" and a combining accent gave the word "cafe", the
    // precomposed form "caf"), as given when it has none (an unpaired surrogate).
    internal static IReadOnlyList<string> Of(string? question) =>
        string.IsNullOrWhiteSpace(question) ? []
        : [.. Word().Matches(AnchorMatcher.Folded(CaseChange().Replace(Unicode.Nfc.Normalize(question) ?? question, "$1 $2"))).Select(m => m.Value)
            .Where(w => w.Length is >= 3 and <= ModuleExpandQuery.MaximumWordLength && !Common.Contains(w)).Distinct(StringComparer.Ordinal)
            .Take(ModuleExpandQuery.MaximumWords)];

    [System.Text.RegularExpressions.GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial System.Text.RegularExpressions.Regex CaseChange();

    [System.Text.RegularExpressions.GeneratedRegex("[a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex Word();
}
