using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Routing;
using AgentMemory.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Routing;

/// <summary>
/// 40.56: the core router's rules, on sentences of their own (not the routing set's, which scores them). A statement reads
/// nothing; a question reads facts and what its cues add; a host's rule routes a kind of its own; caps only shrink.
/// </summary>
public sealed class RuleBasedMemoryRouterTests
{
    private static readonly RuleBasedMemoryRouter Router = new(new MemoryRoutingOptions());

    [Theory]
    [InlineData("My cousin Marta just got a job in Oslo.")]
    [InlineData("Actually, it was Tuesday, not Monday.")]
    [InlineData("Hi, I'm Omar.")]
    [InlineData("   ")]
    public void A_statement_reads_nothing(string said)
    {
        var route = Router.Route(said);

        route.Recall.Should().BeFalse();
        route.Kinds.Should().BeEmpty();
        route.SkipReason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Suggest something for dinner", "preferences")]
    [InlineData("Remind me what my sister does", "graph")]
    [InlineData("Could you list my appointments.", "facts")]
    public void A_request_without_a_question_mark_still_reads(string asked, string kind)
    {
        Router.Route(asked).Kinds.Should().Contain([MemoryRoute.Facts, kind]);
    }

    [Theory]
    [InlineData("When does my passport expire?", new[] { "facts" })]
    [InlineData("What does my nephew study?", new[] { "facts", "graph" })]
    [InlineData("Who called yesterday?", new[] { "facts", "graph" })]
    [InlineData("What does Tomasz think of the plan?", new[] { "facts", "graph" })]
    [InlineData("Is Ines's flat near the river?", new[] { "facts", "graph" })]
    [InlineData("What kind of music would I enjoy tonight?", new[] { "facts", "preferences" })]
    [InlineData("How did the interview go last time?", new[] { "facts", "messages" })]
    [InlineData("What were we discussing about the budget?", new[] { "facts", "messages" })]
    public void A_question_reads_facts_and_what_its_cues_add(string asked, string[] kinds)
    {
        Router.Route(asked).Kinds.Should().Equal(kinds);
    }

    [Theory]
    [InlineData("What did I do in October?")]
    [InlineData("What's on for Friday?")]
    [InlineData("Where was I in March, and what did I eat on Monday?")]
    public void Months_days_and_question_words_are_not_names(string asked)
    {
        Router.Route(asked).Kinds.Should().NotContain(MemoryRoute.Graph);
    }

    [Fact]
    public void The_route_says_which_rule_chose_each_kind()
    {
        var route = Router.Route("Who is my manager?");

        route.ChosenBy.Should().Contain(MemoryRoute.Facts, "facts-always");
        route.ChosenBy[MemoryRoute.Graph].Should().StartWith("graph-");
    }

    [Fact]
    public void A_host_rule_routes_a_kind_of_its_own_and_the_defaults_can_be_dropped()
    {
        var options = new MemoryRoutingOptions { UseDefaultRules = false };
        options.Rules.Add(new MemoryRoutingRule("lessons", "lessons-vocabulary", @"\b(?:vocabulary|word list|flashcards?)\b"));
        var router = new RuleBasedMemoryRouter(options);

        router.Route("Can we go over my vocabulary?").Kinds.Should().Equal([MemoryRoute.Facts, "lessons"]);
        router.Route("Who is my manager?").Kinds.Should().Equal([MemoryRoute.Facts], "the default rules are off");
    }

    [Fact]
    public void Routing_restricts_the_configured_caps_and_never_raises_them()
    {
        var configured = RecallOptions.Default with { MaxFacts = 7, MaxEntities = 4, MaxRelationships = 2, MaxPreferences = 3, MaxRelevantMessages = 6, MaxRecentMessages = 9 };
        var request = new RecallRequest { SessionId = "s", Query = "q" };
        var route = new MemoryRoute { Recall = true, Kinds = [MemoryRoute.Facts, MemoryRoute.Preferences] };

        var caps = MemoryService.Restricted(request, route, configured).Options;

        caps.MaxFacts.Should().Be(7, "the host's cap, not the library default");
        caps.MaxPreferences.Should().Be(3);
        (caps.MaxEntities, caps.MaxRelationships, caps.MaxRelevantMessages).Should().Be((0, 0, 0));
        caps.MaxRecentMessages.Should().Be(9, "recent messages are continuity, not routed");
        MemoryService.Restricted(request, new MemoryRoute { Recall = false }, configured).Options.MaxFacts.Should().Be(0);
    }
}
