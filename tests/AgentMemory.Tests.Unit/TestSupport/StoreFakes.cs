using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Memory;
using NSubstitute;

namespace AgentMemory.Tests.Unit.TestSupport;

/// <summary>
/// In-memory fact and preference stores that behave as the Cypher does, for persistence tests that follow a value
/// through supersession. One fake for every such test: review round 8 found tests passing against fakes that revived a
/// closed node completely and never stamped an end, which the store does not do.
/// </summary>
internal static class StoreFakes
{
    /// <summary>
    /// Facts MERGE on the triple (item and batch writes alike); a restatement clears <c>invalidated_at</c> only (the
    /// valid-time end supersession stamped stays: bitemporal history); supersession stamps <c>invalidated_at</c> and, when
    /// absent, <c>valid_until</c>; candidates are live, of the relation's stored forms, and hold at <paramref name="now"/>.
    /// </summary>
    internal static IFactRepository Facts(List<Fact> store, DateTimeOffset now, ISet<string>? failOn = null)
    {
        var facts = Substitute.For<IFactRepository, IBatchMemoryRepository<Fact>>();

        Fact Merge(Fact fact)
        {
            if (failOn?.Contains(fact.Object) == true) throw new InvalidOperationException("write failed");
            var i = store.FindIndex(f => Key(f) == Key(fact));
            if (i < 0) { store.Add(fact); return fact; }
            var was = store[i];
            store[i] = was with
            {
                InvalidatedAtUtc = null,
                ValidUntil = fact.ValidUntil ?? was.ValidUntil,
                ValidFrom = fact.ValidFrom ?? was.ValidFrom,
                OccurredOn = fact.OccurredOn ?? was.OccurredOn,
            };
            return store[i];
        }

        facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(Merge(ci.Arg<Fact>())));
        ((IBatchMemoryRepository<Fact>)facts).UpsertBatchAsync(Arg.Any<IReadOnlyList<Fact>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Fact>>(ci.Arg<IReadOnlyList<Fact>>().Select(Merge).ToList()));
        facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var keys = MemoryRelationCardinality.ReplacedKeys(ci.ArgAt<string>(2));
                return Task.FromResult<IReadOnlyList<Fact>>(store
                    .Where(f => f.InvalidatedAtUtc is null && f.FactId != ci.ArgAt<string>(0) &&
                                MemoryTripleCanonicalizer.CanonicalValue(f.Subject) == MemoryTripleCanonicalizer.CanonicalValue(ci.ArgAt<string>(1)) &&
                                keys.Contains(MemoryTripleCanonicalizer.Canonical(f.Predicate)) &&
                                MemoryTripleCanonicalizer.CanonicalValue(f.Object) != MemoryTripleCanonicalizer.CanonicalValue(ci.ArgAt<string>(3)) &&
                                (f.ValidUntil is null || f.ValidUntil > now) &&
                                ((f.ValidFrom ?? f.OccurredOn) is not { } from || from <= now))
                    .ToList());
            });
        facts.InvalidateAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var i = store.FindIndex(f => f.FactId == ci.ArgAt<string>(0));
                if (i < 0) return Task.FromResult(false);
                store[i] = store[i] with { InvalidatedAtUtc = store[i].InvalidatedAtUtc ?? now };
                return Task.FromResult(true);
            });
        facts.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(store.FirstOrDefault(f => f.FactId == ci.ArgAt<string>(0))));
        facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Fact>>(store.Where(f => f.Subject == ci.ArgAt<string>(0)).ToList()));
        facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var i = store.FindIndex(f => f.FactId == ci.ArgAt<string>(0));
                if (i < 0 || ci.ArgAt<string>(0) == ci.ArgAt<string>(1)) return Task.FromResult(false);
                store[i] = store[i] with { InvalidatedAtUtc = store[i].InvalidatedAtUtc ?? now, ValidUntil = store[i].ValidUntil ?? now };
                return Task.FromResult(true);
            });
        return facts;
    }

    /// <summary>Preferences: written by id, closed by supersession.</summary>
    internal static IPreferenceRepository Preferences(List<Preference> store, DateTimeOffset now)
    {
        var preferences = Substitute.For<IPreferenceRepository, IBatchMemoryRepository<Preference>>();
        preferences.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(ci => { store.Add(ci.Arg<Preference>()); return Task.FromResult(ci.Arg<Preference>()); });
        ((IBatchMemoryRepository<Preference>)preferences).UpsertBatchAsync(Arg.Any<IReadOnlyList<Preference>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { store.AddRange(ci.Arg<IReadOnlyList<Preference>>()); return Task.FromResult(ci.Arg<IReadOnlyList<Preference>>()); });
        preferences.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(store.FirstOrDefault(p => p.PreferenceId == ci.ArgAt<string>(0))));
        preferences.GetByCategoryAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Preference>>(store.Where(p => p.Category == ci.ArgAt<string>(0)).ToList()));
        preferences.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var i = store.FindIndex(p => p.PreferenceId == ci.ArgAt<string>(0));
                if (i < 0) return Task.FromResult(false);
                store[i] = store[i] with { InvalidatedAtUtc = store[i].InvalidatedAtUtc ?? now };
                return Task.FromResult(true);
            });
        return preferences;
    }

    // One owner per test: the triple is the MERGE key.
    private static (string, string, string) Key(Fact fact) =>
        (MemoryTripleCanonicalizer.CanonicalValue(fact.Subject), MemoryTripleCanonicalizer.Canonical(fact.Predicate),
         MemoryTripleCanonicalizer.CanonicalValue(fact.Object));
}
