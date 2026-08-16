using AgentMemory.Abstractions.Domain;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Domain;

/// <summary>
/// 30.5 step 1. <see cref="MemoryDelta.IsEmpty"/> has to answer for <b>every</b> bucket.
/// </summary>
/// <remarks>
/// <para>
/// This looks like a test of an eight-clause boolean, and it is, but the clause that gets forgotten is
/// the one added last. <c>IsEmpty</c> is what decides whether anything is rendered at all: a bucket
/// missing from it means a delta containing only that kind of change reports itself as empty and the
/// agent is told nothing happened. That is the reassuring-fabrication failure, reached through a
/// one-line omission.
/// </para>
/// <para>
/// So it is a theory over the buckets rather than eight hand-written cases — a ninth bucket added
/// without a clause fails here the moment its row is added, and the row is impossible to forget because
/// the population helper will not compile without it.
/// </para>
/// </remarks>
public sealed class MemoryDeltaTests
{
    private static readonly DateTimeOffset Since = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset Taken = Since.AddHours(1);

    private static Fact SomeFact => new()
    {
        FactId = "f1", Subject = "Ada", Predicate = "works_at", Object = "Initech",
        Confidence = 0.9, CreatedAtUtc = Since,
    };

    private static Preference SomePreference => new()
    {
        PreferenceId = "p1", Category = "food", PreferenceText = "vegetarian",
        Confidence = 0.9, CreatedAtUtc = Since,
    };

    private static Entity SomeEntity => new()
    {
        EntityId = "e1", Name = "Initech", Type = "ORGANIZATION",
        Confidence = 0.9, CreatedAtUtc = Since,
    };

    private static MemoryDelta Empty => new() { Since = Since, TakenAtUtc = Taken };

    public static TheoryData<string, MemoryDelta> OneBucketPopulated() => new()
    {
        { nameof(MemoryDelta.NewFacts), Empty with { NewFacts = [SomeFact] } },
        {
            nameof(MemoryDelta.SupersededPairs),
            Empty with { SupersededPairs = [new SupersededFactPair(SomeFact, SomeFact)] }
        },
        { nameof(MemoryDelta.InvalidatedFacts), Empty with { InvalidatedFacts = [SomeFact] } },
        { nameof(MemoryDelta.ExpiredValidity), Empty with { ExpiredValidity = [SomeFact] } },
        { nameof(MemoryDelta.NewlyDueProspective), Empty with { NewlyDueProspective = [SomeFact] } },
        { nameof(MemoryDelta.NewPreferences), Empty with { NewPreferences = [SomePreference] } },
        {
            nameof(MemoryDelta.SupersededPreferences),
            Empty with
            {
                SupersededPreferences = [new SupersededPreferencePair(SomePreference, SomePreference)],
            }
        },
        { nameof(MemoryDelta.NewEntities), Empty with { NewEntities = [SomeEntity] } },
    };

    [Theory]
    [MemberData(nameof(OneBucketPopulated))]
    public void ADeltaWithAnythingInAnyBucketIsNotEmpty(string bucket, MemoryDelta delta)
    {
        delta.IsEmpty.Should().BeFalse(
            "a delta whose only content is in {0} still has something to report", bucket);
    }

    [Fact]
    public void ADeltaWithEveryBucketEmptyIsEmpty()
    {
        Empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void EveryBucketIsCoveredByTheTheory()
    {
        // The guard on the guard. IsEmpty reads eight collections; if a ninth is added and this theory
        // is not extended, the new bucket goes unchecked and the test file still passes green.
        var collectionProperties = typeof(MemoryDelta).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            .Select(p => p.Name)
            .Where(name => name != nameof(MemoryDelta.TruncatedSections))  // metadata, not content
            .ToList();

        var covered = OneBucketPopulated().Select(row => (string)row[0]!).ToList();

        collectionProperties.Should().BeEquivalentTo(covered);
    }

    [Fact]
    public void TruncatedSectionsAloneDoesNotMakeADeltaNonEmpty()
    {
        // TruncatedSections is metadata ABOUT the buckets, not a bucket. A delta that truncated nothing
        // into nothing is still nothing, and rendering a heading for it would assert a change set that
        // does not exist.
        var delta = Empty with { TruncatedSections = ["NewFacts"] };

        delta.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TheWindowIsCarriedOnTheResultSoTheCallerNeedNotRememberWhatItAsked()
    {
        // TakenAtUtc is the next checkpoint. Handing it back on the result -- rather than making the
        // caller re-read a clock -- is what closes the gap between deltas.
        var delta = Empty;

        delta.Since.Should().Be(Since);
        delta.TakenAtUtc.Should().Be(Taken);
    }
}
