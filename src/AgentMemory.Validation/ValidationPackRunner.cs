using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Validation;

/// <summary>
/// Runs validation packs against a live Neo4j store, each on its own stack built the way a host builds one: the pack's
/// options, the pack's extraction (V1), stub embeddings, and a replayed clock (V4). Every run writes under a fresh
/// prefix, so runs never meet; nothing is deleted.
/// </summary>
/// <param name="configureNeo4j">The store to run against (URI, credentials, database, embedding dimensions).</param>
/// <param name="embeddings">
/// A real embedding model instead of the stub (40.68: the routing matrix records recall scores, which the stub's vectors
/// cannot give); the store's embedding dimensions must match it. Null: the stub, as every pack check runs.
/// </param>
/// <param name="logging">
/// Where each pack's stack logs (41.08: a failure inside a pack, an embedding that could not be made, must reach the
/// caller's output, not a logger nobody reads). Null: no provider, as before.
/// </param>
public sealed class ValidationPackRunner(
    Action<Neo4jOptions> configureNeo4j,
    Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>>? embeddings = null,
    Action<ILoggingBuilder>? logging = null)
{
    /// <summary>Recall caps for every kind (V2: membership, not rank); the similarity floor is 0.</summary>
    internal const int Cap = 50;

    /// <summary>Runs one pack. Never throws for a failing pack: every failure is a check in the result.</summary>
    public async Task<PackRunResult> RunAsync(ValidationPack pack, string? runPrefix = null, CancellationToken cancellationToken = default)
    {
        var prefix = runPrefix ?? $"pack-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..28];
        var checks = new List<PackCheckResult>();
        var routes = new List<PackRouteRecord>();
        PackRunResult Result() => new() { PackId = pack.Id, Title = pack.Title, RunPrefix = prefix, Checks = checks, Routes = routes };

        var (options, problems) = Configure(pack);
        if (problems.Count > 0)
        {
            checks.AddRange(problems.Select((p, i) => new PackCheckResult($"pack:{i + 1}", "pack", false, p)));
            return Result();
        }

        var store = await LoadAsync(pack, options, prefix, cancellationToken).ConfigureAwait(false);
        await using var disposeStore = store.ConfigureAwait(false);
        var provider = store.Provider;
        string Owner(string owner) => store.Owner(owner);

        // Storage.
        using (var scope = provider.CreateScope())
        {
            var facts = scope.ServiceProvider.GetRequiredService<IFactRepository>();
            var entities = scope.ServiceProvider.GetRequiredService<IEntityRepository>();
            foreach (var (check, index) in pack.Storage.Select((c, i) => (c, i)))
                checks.Add(await CheckStorageAsync($"storage:{index + 1}", check, MemoryScope.For(Owner(check.Owner), includeShared: false),
                    facts, entities, cancellationToken).ConfigureAwait(false));
        }

        // G6 (40.50): what ingestion wrote keeps the store's integrity rules, owner by owner (warnings do not fail).
        using (var scope = provider.CreateScope())
        {
            var integrity = scope.ServiceProvider.GetService<IMemoryIntegrityService>();
            foreach (var owner in integrity is null ? [] : pack.Owners)
            {
                var report = await integrity!.CheckAsync(Owner(owner), cancellationToken).ConfigureAwait(false);
                var broken = report.Rules.Where(r => r.Violations > 0 && r.Severity == MemoryIntegrityRule.Error).ToList();
                checks.Add(new($"integrity:{owner}", "integrity", broken.Count == 0, broken.Count == 0
                    ? $"{owner}: every integrity rule holds"
                    : $"{owner}: {string.Join("; ", broken.Select(r => $"{r.Id} ({r.Violations}: {string.Join(", ", r.Examples)})"))}"));
            }
        }

        // Questions.
        foreach (var question in pack.Questions)
        {
            var owner = Owner(question.Owner);
            var context = await store.RecallAsync(question.Owner, question.Session, question.Ask, question.At,
                recall => recall with
                {
                    MaxFacts = Cap, MaxEntities = Cap, MaxRelationships = Cap, MaxPreferences = Cap,
                    MaxRelevantMessages = Cap, MaxRecentMessages = Cap, MaxTraces = 0, MinSimilarityScore = 0,
                }, question.AsOf, cancellationToken).ConfigureAwait(false);
            var recalled = new Recalled(context);

            var foundIn = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (item, index) in question.Expect.Select((x, i) => (x, i)))
            {
                var kind = recalled.Find(item);
                if (kind is not null) foundIn.Add(kind);
                checks.Add(new($"recall:{question.Id}:expect:{index + 1}", "recall", kind is not null,
                    kind is not null ? $"{question.Id}: {item} recalled ({kind})"
                        : $"{question.Id}: {item} not recalled. {recalled.Describe(item)}"));
            }
            foreach (var (item, index) in question.Exclude.Select((x, i) => (x, i)))
            {
                var kind = recalled.Find(item);
                checks.Add(new($"recall:{question.Id}:exclude:{index + 1}", "recall", kind is null,
                    kind is null ? $"{question.Id}: {item} not recalled, as it should not be"
                        : $"{question.Id}: {item} recalled ({kind}) and should not be"));
            }
            var foreign = recalled.OwnedByOthers(owner).ToList();
            checks.Add(new($"isolation:{question.Id}", "isolation", foreign.Count == 0,
                foreign.Count == 0 ? $"{question.Id}: nothing of another owner's recalled"
                    : $"{question.Id}: another owner's items recalled: {string.Join("; ", foreign.Take(5))}"));
            routes.Add(new(question.Id, question.Kinds, [.. foundIn]));
        }
        return Result();
    }

    /// <summary>
    /// Loads a pack's sessions into a fresh store (40.68): its options, its scripted extraction, a replayed clock, and the
    /// runner's embeddings; the questions are the caller's. Throws when the pack's options do not apply.
    /// </summary>
    public async Task<PackStore> LoadAsync(ValidationPack pack, string? runPrefix = null, CancellationToken cancellationToken = default)
    {
        var (options, problems) = Configure(pack);
        if (problems.Count > 0) throw new InvalidOperationException($"pack {pack.Id}: {string.Join("; ", problems)}");
        return await LoadAsync(pack, options, runPrefix ?? $"pack-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..28],
            cancellationToken).ConfigureAwait(false);
    }

    private static (MemoryOptions Options, List<string> Problems) Configure(ValidationPack pack)
    {
        var problems = ValidationPackReader.Check(pack).ToList();
        var options = pack.Options.Preset == "conversational" ? MemoryOptions.CreateConversational() : new MemoryOptions();
        foreach (var (path, value) in pack.Options.Set)
            if (OptionPaths.Apply(options, path, value) is { } problem) problems.Add(problem);
        return (options, problems);
    }

    private async Task<PackStore> LoadAsync(ValidationPack pack, MemoryOptions options, string prefix, CancellationToken cancellationToken)
    {
        string Owner(string owner) => $"{prefix}-{owner}";
        var neo4j = new Neo4jOptions();
        configureNeo4j(neo4j);
        var script = new PackScript();
        var clock = new ReplayClock(pack.Sessions.SelectMany(s => s.Messages).Select(m => m.At).DefaultIfEmpty(DateTimeOffset.UtcNow).Min());

        var services = new ServiceCollection();
        services.AddLogging(builder => logging?.Invoke(builder));
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IEntityExtractor>(new ScriptedEntities(script));
        services.AddSingleton<IFactExtractor>(new ScriptedFacts(script));
        services.AddSingleton<IRelationshipExtractor>(new ScriptedRelationships(script));
        services.AddSingleton<IPreferenceExtractor>(new ScriptedPreferences(script));
        services.AddNeo4jAgentMemory(options, configureNeo4j);
        // The caller's generator is borrowed, never owned: registered through a factory, the container would dispose it with
        // this pack's store, and every later pack of the same runner would embed with a disposed client (empty vectors,
        // logged only as warnings). Found 2026-10-04: a runner loading one store per turn wrote every turn after the first
        // without embeddings.
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => embeddings is not null
            ? new BorrowedEmbeddingGenerator(embeddings(sp))
            : new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), neo4j.EmbeddingDimensions));
        var provider = services.BuildServiceProvider(validateScopes: true);
        var store = new PackStore(provider, prefix, clock, options);
        try
        {
            await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(cancellationToken).ConfigureAwait(false);
            await provider.GetRequiredService<INeo4jTransactionRunner>().WriteAsync(async runner =>
                await runner.RunAsync("CALL db.awaitIndexes(60)").ConfigureAwait(false)).ConfigureAwait(false);

            // Ingestion: each message stored and extracted at its own time.
            foreach (var session in pack.Sessions)
            {
                // A shared session is written for everyone: no user, the explicit shared write (it cannot carry one).
                var owner = session.Shared ? null : Owner(session.Owner);
                var sessionId = $"{prefix}-{session.Id}";
                using var scope = provider.CreateScope();
                var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
                var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
                clock.Now = session.Messages.Select(m => m.At).DefaultIfEmpty(clock.Now).First();
                await shortTerm.AddConversationAsync(sessionId, sessionId, userId: owner, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var said in session.Messages)
                {
                    clock.Now = said.At;
                    var messageId = $"{sessionId}-m{script.Count}";
                    script.Add(messageId, said);
                    var message = await shortTerm.AddMessageAsync(new Message
                    {
                        MessageId = messageId,
                        ConversationId = sessionId,
                        SessionId = sessionId,
                        Role = said.Role,
                        Content = said.Text,
                        TimestampUtc = said.At,
                    }, cancellationToken).ConfigureAwait(false);
                    await pipeline.ExtractAsync(new ExtractionRequest
                    {
                        SessionId = sessionId, UserId = owner, Messages = [message], ShareWithEveryone = session.Shared,
                    }, cancellationToken).ConfigureAwait(false);
                }
                if (session.Traces.Count > 0)
                {
                    var reasoning = scope.ServiceProvider.GetRequiredService<IReasoningMemoryService>();
                    foreach (var trace in session.Traces)
                    {
                        clock.Now = trace.At;
                        var started = await reasoning.StartTraceAsync(sessionId, trace.Task, ownerId: owner, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        foreach (var (step, n) in trace.Steps.Select((s, i) => (s, i + 1)))
                            await reasoning.AddStepAsync(started.TraceId, n, step.Thought, step.Action, step.Observation, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                        await reasoning.CompleteTraceAsync(started.TraceId, trace.Outcome, trace.Success, cancellationToken).ConfigureAwait(false);
                        if (trace.Kind == "procedure")
                            await reasoning.PromoteTraceAsync(started.TraceId, TraceKind.Procedure, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            return store;
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<PackCheckResult> CheckStorageAsync(
        string id, PackStorageCheck check, MemoryScope scope, IFactRepository facts, IEntityRepository entities, CancellationToken cancellationToken)
    {
        if (check.Fact is { } text && ValidationPackReader.TryTriple(text, out var subject, out var predicate, out var @object))
        {
            var stored = (await facts.GetBySubjectAsync(subject, scope, cancellationToken).ConfigureAwait(false))
                .Where(f => Same(f.Predicate, predicate) && Same(f.Object, @object))
                .ToList();
            var live = stored.Where(f => f.InvalidatedAtUtc is null).ToList();
            var state = live.Count > 0 ? "live" : stored.Count > 0 ? "closed" : "absent";
            var closedAs = state == "closed" ? stored.OrderByDescending(f => f.InvalidatedAtUtc).First().InvalidatedReason : null;
            var passed = state == check.State && (check.ClosedAs is null || string.Equals(closedAs, check.ClosedAs, StringComparison.Ordinal));
            var found = state == "closed" ? $"closed ({closedAs ?? "no reason recorded"})" : state;
            return new(id, "storage", passed, passed
                ? $"fact '{text}' is {found}"
                : $"fact '{text}' should be {check.State}{(check.ClosedAs is null ? "" : $" ({check.ClosedAs})")}, is {found}");
        }

        var name = check.Entity!;
        var entity = await entities.FindLiveByNameAsync(name, null, scope, cancellationToken).ConfigureAwait(false);
        var entityState = entity is null ? "absent" : Same(entity.Name, name) ? "live" : "alias";
        var ok = entityState == check.State && (check.Of is null || (entity is not null && Same(entity.Name, check.Of)));
        var seen = entity is null ? "absent" : entityState == "live" ? "live" : $"an alias of '{entity.Name}'";
        return new(id, "storage", ok, ok
            ? $"entity '{name}' is {seen}"
            : $"entity '{name}' should be {(check.State == "alias" ? $"an alias of '{check.Of}'" : check.State)}, is {seen}");
    }

    internal static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A recalled context, searched for a pack's items.</summary>
    internal sealed class Recalled(MemoryContext context)
    {
        private IEnumerable<Fact> Facts => context.RelevantFacts.Items.Concat(context.DueFacts.Items).Concat(context.ExpiringFacts.Items);

        private IEnumerable<Message> Messages => context.RecentMessages.Items.Concat(context.RelevantMessages.Items);

        /// <summary>The kind the item was recalled as, or null.</summary>
        public string? Find(PackItem item)
        {
            if (item.Fact is { } fact && ValidationPackReader.TryTriple(fact, out var s, out var p, out var o))
                return Facts.Any(f => Same(f.Subject, s) && Same(f.Predicate, p) && Same(f.Object, o)) ? "facts" : null;
            if (item.Entity is { } name)
                return context.RelevantEntities.Items.Any(e => Same(e.Name, name) || e.Aliases.Any(a => Same(a, name))) ? "entities" : null;
            if (item.Relationship is { } text && ValidationPackReader.TryRelationship(text, out var source, out var type, out var target))
                return context.RelevantRelationships.Items.Any(r =>
                    Same(r.SourceName, source) && Same(r.Relationship.RelationshipType, type) && Same(r.TargetName, target)) ? "relationships" : null;
            if (item.Preference is { } preference)
                return context.RelevantPreferences.Items.Any(x => Contains(x.PreferenceText, preference)) ? "preferences" : null;
            if (item.Message is { } said)
                return Messages.Any(m => Contains(m.Content, said)) ? "messages" : null;
            if (item.Working is { } working)
                return Contains(context.WorkingMemoryBlock, working) ? "working" : null;
            return null;
        }

        /// <summary>What was recalled of the item's kind, for a failure's detail.</summary>
        public string Describe(PackItem item)
        {
            static string List(IEnumerable<string> items)
            {
                var all = items.ToList();
                return all.Count == 0 ? "none" : string.Join("; ", all.Take(8)) + (all.Count > 8 ? $"; … ({all.Count})" : "");
            }
            return item switch
            {
                { Fact: not null } => $"Facts recalled: {List(Facts.Select(f => $"{f.Subject} | {f.Predicate} | {f.Object}"))}",
                { Entity: not null } => $"Entities recalled: {List(context.RelevantEntities.Items.Select(e => e.Name))}",
                { Relationship: not null } => $"Relationships recalled: {List(context.RelevantRelationships.Items.Select(r => $"{r.SourceName} -[{r.Relationship.RelationshipType}]-> {r.TargetName}"))}",
                { Preference: not null } => $"Preferences recalled: {List(context.RelevantPreferences.Items.Select(x => x.PreferenceText))}",
                { Message: not null } => $"Messages recalled: {Messages.Count()}",
                _ => context.WorkingMemoryBlock is null ? "No working-memory block." : "The working-memory block does not contain it.",
            };
        }

        /// <summary>Recalled items whose owner is someone else (shared items, with no owner, are not).</summary>
        public IEnumerable<string> OwnedByOthers(string owner)
        {
            bool Foreign(string? itemOwner) => itemOwner is not null && !string.Equals(itemOwner, owner, StringComparison.Ordinal);
            return Facts.Where(f => Foreign(f.OwnerId)).Select(f => $"fact '{f.Subject} | {f.Predicate} | {f.Object}'")
                .Concat(context.RelevantEntities.Items.Where(e => Foreign(e.OwnerId)).Select(e => $"entity '{e.Name}'"))
                .Concat(context.RelevantPreferences.Items.Where(x => Foreign(x.OwnerId)).Select(x => $"preference '{x.PreferenceText}'"));
        }

        private static bool Contains(string? text, string part) =>
            text?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;
    }
}

/// <summary>
/// A pack loaded into a store (40.68): its stack, its run prefix and its replayed clock. Recall runs as a host's would,
/// for an owner of the pack, in one of its sessions. Disposing it disposes the stack; the data stays (runs never meet).
/// </summary>
public sealed class PackStore : IAsyncDisposable
{
    private readonly ReplayClock _clock;

    internal PackStore(ServiceProvider provider, string prefix, ReplayClock clock, MemoryOptions options)
    {
        Provider = provider;
        Prefix = prefix;
        _clock = clock;
        Options = options;
    }

    /// <summary>The stack.</summary>
    public ServiceProvider Provider { get; }

    /// <summary>The run prefix every owner and session id carries.</summary>
    public string Prefix { get; }

    /// <summary>The options the pack runs with.</summary>
    public MemoryOptions Options { get; }

    /// <summary>The store's id for a pack owner.</summary>
    public string Owner(string owner) => $"{Prefix}-{owner}";

    /// <summary>
    /// Recalls for <paramref name="owner"/> at <paramref name="at"/>, in the pack session <paramref name="session"/> (or a
    /// session of its own), with the pack's recall options changed by <paramref name="adjust"/>; as of the given clocks
    /// when <paramref name="asOf"/> is set.
    /// </summary>
    public async Task<MemoryContext> RecallAsync(string owner, string? session, string query, DateTimeOffset at,
        Func<RecallOptions, RecallOptions>? adjust = null, PackAsOf? asOf = null, CancellationToken cancellationToken = default)
    {
        _clock.Now = at;
        using var scope = Provider.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var request = new RecallRequest
        {
            SessionId = session is { } asked ? $"{Prefix}-{asked}" : $"{Prefix}-ask-{owner}",
            UserId = Owner(owner),
            Query = query,
            TemporalReferenceTime = at,
            Options = adjust is null ? Options.Recall : adjust(Options.Recall),
        };
        return (asOf is { } clocks
            ? await memory.RecallAsOfAsync(request, clocks.Valid ?? at, clocks.System ?? at, cancellationToken).ConfigureAwait(false)
            : await memory.RecallAsync(request, cancellationToken).ConfigureAwait(false)).Context;
    }

    /// <summary>
    /// Runs the decay pass for <paramref name="owner"/>'s own memories as of <paramref name="at"/> (40.72, the forgetting
    /// switch): what has faded by then is aged out, non-destructively, and legible forgetting can say so later.
    /// </summary>
    /// <returns>How many memories faded.</returns>
    public async Task<int> DecayAsync(string owner, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        _clock.Now = at;
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMemoryDecayService>()
            .PruneExpiredMemoriesAsync(MemoryScope.For(Owner(owner), includeShared: false), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}
