using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>37.2: the object the model wrote twice, at the end of the predicate and as the object, is written once.</summary>
public sealed class PredicateEchoTests
{
    private static string Said(string predicate, string @object) =>
        PredicateEcho.Trim(new ExtractedFact { Subject = "S", Predicate = predicate, Object = @object }).Predicate;

    [Theory]
    [InlineData("is a chef", "chef", "is")]                                   // seen: "Daniel is a chef chef"
    [InlineData("is a cat", "cat", "is")]                                     // seen: "Dinah is a cat cat"
    [InlineData("is to go down the chimney", "the chimney", "is to go down")]   // seen, with the article
    [InlineData("was lost in the woods", "woods", "was lost in")]               // the article goes with the object
    [InlineData("is_a_chef", "chef", "is")]                                   // snake case
    [InlineData("Is A Chef", "chef", "Is")]                                   // case kept, compared case-blind
    public void The_repeated_object_leaves_the_predicate(string predicate, string @object, string expected) =>
        Said(predicate, @object).Should().Be(expected);

    [Theory]
    [InlineData("works at", "Acme")]
    [InlineData("lives in", "Oslo")]
    [InlineData("chef", "chef")]                    // nothing would be left of the predicate
    [InlineData("is", "a chef")]
    [InlineData("likes chefs", "chef")]             // a whole word only
    public void Anything_else_is_unchanged(string predicate, string @object) =>
        Said(predicate, @object).Should().Be(predicate);

    [Theory]
    [InlineData("lives in the Netherlands", "Netherlands", "lives in", "the Netherlands")]   // review round 1: a relation again
    [InlineData("is a chef", "chef", "is", "a chef")]
    [InlineData("is to go down the chimney", "the chimney", "is to go down", "the chimney")]
    public void A_dangling_article_goes_with_the_object(string predicate, string @object, string expectedPredicate, string expectedObject)
    {
        var trimmed = PredicateEcho.Trim(new ExtractedFact { Subject = "S", Predicate = predicate, Object = @object });

        trimmed.Predicate.Should().Be(expectedPredicate);
        trimmed.Object.Should().Be(expectedObject);
    }
}
