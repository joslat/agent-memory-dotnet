using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// ER-CANNOT-LINK (the entity study's R14, 26 Nov): names that a similarity score puts close together but that say they are
/// different things are never offered for review, so no later step (the writer's reference, the adjudicator) can confirm
/// them: other numbers or versions, other months, a name that differs only by a negation, a very short name that is not
/// the same. A host-verified key still decides. A prefix is not one of them: "Priya" for "Priya Nair" or "Brisa" for
/// "Brisa Studio" is usually the same thing, and refusing it would split what is joined today.
/// </summary>
public sealed class IdentityCannotLinkTests
{
    private static IdentityPolicyResult Evaluate(string mention, string stored, string type, double fuzzy) =>
        IdentityPolicy.Evaluate(new ExtractedEntity { Name = mention, Type = type, Confidence = 1 },
            [new IdentityCandidate
            {
                Entity = new() { EntityId = "stored", Name = stored, Type = type, OwnerId = "bruna", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch },
                Signals = new() { FuzzySimilarity = fuzzy },
            }],
            new EntityIdentityResolutionContext { SourceMessageIds = ["current"] }, new EntityIdentityPolicy(), MemoryScope.For("bruna", false));

    [Theory]
    [InlineData("invoice batch 6", "invoice batch 1", "OBJECT", "cannot_link_numbers")]
    [InlineData("Room 102", "Room 101", "LOCATION", "cannot_link_numbers")]
    [InlineData("the 2025 budget", "the 2024 budget", "OBJECT", "cannot_link_numbers")]
    [InlineData("Part III", "Part II", "OBJECT", "cannot_link_numbers")]
    [InlineData("the June trip", "the May trip", "EVENT", "cannot_link_dates")]
    [InlineData("the non-smoking room", "the smoking room", "LOCATION", "cannot_link_negation")]
    [InlineData("Al", "Ali", "PERSON", "cannot_link_very_short_name")]
    public void Names_that_say_they_are_different_things_are_never_reviewed(string mention, string stored, string type, string reason)
    {
        var result = Evaluate(mention, stored, type, fuzzy: 0.9);

        result.RejectedEntityIds.Should().Equal("stored");
        result.PendingEntityIds.Should().BeEmpty();
        result.Reasons["stored"].Should().Be(reason);
    }

    [Theory]
    [InlineData("the Q3 report", "Finance's Q3 report", "OBJECT")]
    [InlineData("Priya", "Priya Nair", "PERSON")]
    [InlineData("Kansas", "Kansas City", "LOCATION")]
    [InlineData("Room 101", "Room 101", "LOCATION")]
    [InlineData("the trip", "the May trip", "EVENT")]
    [InlineData("the smoking room", "the smoking room on the second floor", "LOCATION")]
    [InlineData("Jo", "Jo Smith", "PERSON")]
    [InlineData("UK", "U.K.", "LOCATION")]
    public void Names_that_do_not_say_so_are_reviewed_as_before(string mention, string stored, string type)
    {
        var result = Evaluate(mention, stored, type, fuzzy: 0.9);

        result.RejectedEntityIds.Should().BeEmpty();
        result.PendingEntityIds.Should().Equal("stored");
    }
}
