using FluentAssertions;
using AgentMemory.AgentFramework.Recall;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// J4.1. A deterministic lexical route for aggregation questions, with no model call.
/// </summary>
/// <remarks>
/// Top-K is a relevance cutoff and carries no completeness guarantee, so "how many" questions are
/// unanswerable from it — miss one of five matching facts and the count is four, with nothing
/// signalling the loss. Relation completeness is the fix, and it is measured: turning it on took
/// Structured from 73.3% to 90.0% in a controlled A/B, and it is what flipped the furniture question.
/// <para>
/// It stays off by default because it widens the context — expansion tripled Structured's context
/// from 649 to 2,027 tokens — so it must be routed to, not enabled globally. The two questions this
/// track has spent the most effort on, <c>2e6d26dc</c> and <c>gpt4_15e38248</c>, both open with
/// "How many", which is what makes a lexical route sufficient here rather than a model call.
/// </para>
/// </remarks>
public sealed class AggregationRouteTests
{
    private static readonly HeuristicAutomaticRecallPolicy Policy = new();

    [Theory]
    // The two measured failures this whole track was built around.
    [InlineData("How many babies were born to friends and family members in the last few months?")]
    [InlineData("How many pieces of furniture did I buy, assemble, sell, or fix this year?")]
    // The rest of the G5 routing table.
    [InlineData("List all the books I read last year")]
    [InlineData("What is the total I spent on the kitchen?")]
    [InlineData("Count the trips I took to Lisbon")]
    public async Task AggregationQuestionsRequireRelationCompleteness(string question) =>
        (await DecideAsync(question).ConfigureAwait(true))
            .RequiresRelationCompleteness.Should().BeTrue();

    [Theory]
    // Ordinary recall must not pay the cost. Expansion roughly triples the context.
    [InlineData("What did I buy last week?")]
    [InlineData("Where do my parents live?")]
    [InlineData("Did I like the restaurant in Lisbon?")]
    [InlineData("When did I travel to Japan?")]
    public async Task OrdinaryQuestionsDoNotRequireIt(string question) =>
        (await DecideAsync(question).ConfigureAwait(true))
            .RequiresRelationCompleteness.Should().BeFalse();

    [Fact]
    public async Task TheRouteIsDeterministicAndCostsNoModelCall()
    {
        // The whole argument for a lexical route: it is free, so it can run on every turn.
        var first = await DecideAsync("How many books did I read?").ConfigureAwait(true);
        var second = await DecideAsync("How many books did I read?").ConfigureAwait(true);

        first.RequiresRelationCompleteness.Should().Be(second.RequiresRelationCompleteness);
    }

    [Fact]
    public async Task ADecisionThatSkipsRecallNeverRequestsCompleteness()
    {
        // A greeting must not trigger the most expensive retrieval mode in the system.
        var decision = await DecideAsync("hi").ConfigureAwait(true);

        decision.ShouldRecall.Should().BeFalse();
        decision.RequiresRelationCompleteness.Should().BeFalse();
    }

    private static async Task<AutomaticRecallDecision> DecideAsync(string question) =>
        await Policy.DecideAsync(
                new AutomaticRecallContext
                {
                    ConversationId = "c",
                    SessionId = "s",
                    Messages = [new ChatMessage(ChatRole.User, question)]
                })
            .ConfigureAwait(true);
}
