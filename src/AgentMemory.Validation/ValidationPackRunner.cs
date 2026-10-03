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
public sealed class ValidationPackRunner(Action<Neo4jOptions> configureNeo4j)
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

        var problems = ValidationPackReader.Check(pack).ToList();
        var options = pack.Options.Preset == "conversational" ? MemoryOptions.CreateConversational() : new MemoryOptions();
        foreach (var (path, value) in pack.Options.Set)
            if (OptionPaths.Apply(options, path, value) is { } problem) problems.Add(problem);
        if (problems.Count > 0)
        {
            checks.AddRange(problems.Select((p, i) => new PackCheckResult($"pack:{i + 1}", "pack", false, p)));
            return Result();
        }

        string Owner(string owner) => $"{prefix}-{owner}";
        var neo4j = new Neo4jOptions();
        configureNeo4j(neo4j);
        var script = new PackScript();
        var clock = new ReplayClock(pack.Sessions.SelectMany(s => s.Messages).Select(m => m.At).DefaultIfEmpty(DateTimeOffset.UtcNow).Min());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IEntityExtractor>(new ScriptedEntities(script));
        services.AddSingleton<IFactExtractor>(new ScriptedFacts(script));
        services.AddSingleton<IRelationshipExtractor>(new ScriptedRelationships(script));
        services.AddSingleton<IPreferenceExtractor>(new ScriptedPreferences(script));
        services.AddNeo4jAgentMemory(options, configureNeo4j);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), neo4j.EmbeddingDimensions));
        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var disposeProvider = provider.ConfigureAwait(false);

        await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(cancellationToken).ConfigureAwait(false);
        await provider.GetRequiredService<INeo4jTransactionRunner>().WriteAsync(async runner =>
            await runner.RunAsync("CALL db.awaitIndexes(60)").ConfigureAwait(false)).ConfigureAwait(false);

        // Ingestion: each message stored and extracted at its own time.
        foreach (var session in pack.Sessions)
        {
            var owner = Owner(session.Owner);
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
                await pipeline.ExtractAsync(new ExtractionRequest { SessionId = sessionId, UserId = owner, Messages = [message] }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Storage.
        using (var scope = provider.CreateScope())
        {
            var facts = scope.ServiceProvider.GetRequiredService<IFactRepository>();
            var entities = scope.ServiceProvider.GetRequiredService<IEntityRepository>();
            foreach (var (check, index) in pack.Storage.Select((c, i) => (c, i)))
                checks.Add(await CheckStorageAsync($"storage:{index + 1}", check, MemoryScope.For(Owner(check.Owner), includeShared: false),
                    facts, entities, cancellationToken).ConfigureAwait(false));
        }

        // Questions.
        foreach (var question in pack.Questions)
        {
            clock.Now = question.At;
            var owner = Owner(question.Owner);
            using var scope = provider.CreateScope();
            var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var request = new RecallRequest
            {
                SessionId = question.Session is { } asked ? $"{prefix}-{asked}" : $"{prefix}-ask-{question.Owner}",
                UserId = owner,
                Query = question.Ask,
                TemporalReferenceTime = question.At,
                Options = options.Recall with
                {
                    MaxFacts = Cap, MaxEntities = Cap, MaxRelationships = Cap, MaxPreferences = Cap,
                    MaxRelevantMessages = Cap, MaxRecentMessages = Cap, MaxTraces = 0, MinSimilarityScore = 0,
                },
            };
            var context = (question.AsOf is { } asOf
                ? await memory.RecallAsOfAsync(request, asOf.Valid ?? question.At, asOf.System ?? question.At, cancellationToken).ConfigureAwait(false)
                : await memory.RecallAsync(request, cancellationToken).ConfigureAwait(false)).Context;
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
