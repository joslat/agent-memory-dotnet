using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core.Routing;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Unit.Routing;

/// <summary>
/// Extensibility 0.10's two core contracts, frozen with 1.9: a module's own routing rules run in the same engine as core's
/// (so the two never disagree about the core kinds), held to the untrusted-text rules; and one derivation of the question.
/// </summary>
public sealed class ModuleRoutingContractTests
{
    private static readonly RuleBasedMemoryRouter Router = new(new MemoryRoutingOptions());

    private static readonly IReadOnlyList<MemoryRoutingRule> Reviews =
        [new("io.agentmemory.fixture.reviews", "reviews-due", @"\b(?:review|revise|flashcards?|due)\b")];

    [Fact]
    public void A_module_rule_adds_its_kind_and_leaves_the_core_kinds_as_core_routes_them()
    {
        const string question = "Which words are due for review with my teacher?";

        var routed = Router.Route(question, Reviews);

        routed.Kinds.Should().Contain("io.agentmemory.fixture.reviews");
        routed.Kinds.Where(MemoryRoute.CoreKinds.Contains).Should().Equal(Router.Route(question).Kinds);
        routed.ChosenBy["io.agentmemory.fixture.reviews"].Should().Be("reviews-due");
        Router.Route("Where do I live?", Reviews).Kinds.Should().NotContain("io.agentmemory.fixture.reviews");
    }

    [Fact]
    public void A_statement_reads_nothing_however_many_module_rules_there_are()
    {
        Router.Route("I reviewed my flashcards this morning.", Reviews).Recall.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"(?<=my )teacher", "look-behind")]
    [InlineData(@"(a)\1", "back-reference")]
    [InlineData(@"[unclosed", "does not compile")]
    public void A_module_pattern_that_needs_backtracking_or_does_not_compile_is_refused(string pattern, string why)
    {
        var problems = Router.Check([new MemoryRoutingRule("io.agentmemory.fixture.reviews", "bad", pattern)]);

        problems.Should().ContainSingle(why).Which.Should().Contain("'bad'");
    }

    [Fact]
    public void A_rule_needs_a_kind_and_a_name_and_a_good_rule_passes()
    {
        Router.Check([new MemoryRoutingRule("", "nameless kind", "x")]).Should().ContainSingle();
        Router.Check(Reviews).Should().BeEmpty();
    }

    [Fact]
    public void The_question_is_the_last_user_message_with_text()
    {
        ChatMessage[] thread =
        [
            new(ChatRole.User, "I'm Ana."), new(ChatRole.Assistant, "Hi Ana."),
            new(ChatRole.User, "Where do I live?"), new(ChatRole.User, "   "), new(ChatRole.Assistant, "…"),
        ];

        QuestionProbe.Of(thread).Should().Be("Where do I live?");
        QuestionProbe.Of([new ChatMessage(ChatRole.Assistant, "only the agent spoke")]).Should().BeNull();
    }

    /// <summary>Reaches the protected derivation the way a subclass does; never constructed.</summary>
    private sealed class QuestionProbe : Neo4jMemoryContextProvider
    {
        private QuestionProbe() : base(null!, null!, null!, null!, null!, null!, null!, null!) { }

        public static string? Of(IEnumerable<ChatMessage> messages) => QuestionOf(messages);
    }
}
