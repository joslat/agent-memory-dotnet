using AgentMemory.Abstractions.Domain;
using AgentMemory.Validation;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Validation;

/// <summary>
/// 40.57: the checks a pack's questions run, on a context built by hand. The isolation check cannot be planted through
/// options (every mode filters by the asking owner), so it is proven here: another owner's item fails it, a shared one
/// (no owner) does not.
/// </summary>
public sealed class PackRecallCheckTests
{
    [Fact]
    public void Another_owners_fact_in_a_recall_is_named()
    {
        var recalled = new ValidationPackRunner.Recalled(Context(Fact("Bob", "lives in", "Porto", "pack-bob"), Fact("Ana", "lives in", "Madrid", "pack-ana")));

        recalled.OwnedByOthers("pack-ana").Should().Equal(["fact 'Bob | lives in | Porto'"]);
    }

    [Fact]
    public void A_shared_item_is_not_another_owners()
    {
        var recalled = new ValidationPackRunner.Recalled(Context(Fact("Earth", "orbits", "the Sun", owner: null)));

        recalled.OwnedByOthers("pack-ana").Should().BeEmpty();
    }

    [Fact]
    public void Items_match_by_their_text_ignoring_case_and_spacing()
    {
        var recalled = new ValidationPackRunner.Recalled(Context(Fact("Ana", "lives in", "Madrid", "pack-ana")));

        recalled.Find(new PackItem { Fact = "ana |  Lives In | madrid" }).Should().Be("facts");
        recalled.Find(new PackItem { Fact = "Ana | lives in | Bilbao" }).Should().BeNull();
        recalled.Describe(new PackItem { Fact = "Ana | lives in | Bilbao" }).Should().Contain("Ana | lives in | Madrid");
    }

    private static Fact Fact(string subject, string predicate, string @object, string? owner) => new()
    {
        FactId = Guid.NewGuid().ToString("N"), Subject = subject, Predicate = predicate, Object = @object,
        Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UnixEpoch, OwnerId = owner,
    };

    private static MemoryContext Context(params Fact[] facts) => new()
    {
        SessionId = "s",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        RelevantFacts = new MemoryContextSection<Fact> { Items = facts },
    };
}
