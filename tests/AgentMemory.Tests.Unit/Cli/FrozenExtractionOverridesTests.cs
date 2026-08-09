using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Cli.Perf;
using FluentAssertions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class FrozenExtractionOverridesTests
{
    [Fact]
    public async Task FrozenMarker_ReturnsExactTypedShape_WithoutCallingDelegates()
    {
        var entityInner = Substitute.For<IEntityExtractor>();
        var factInner = Substitute.For<IFactExtractor>();
        var preferenceInner = Substitute.For<IPreferenceExtractor>();
        var relationshipInner = Substitute.For<IRelationshipExtractor>();
        var messages = FrozenMessages();

        var entities = await new FrozenExtractionOverrides.FrozenEntityExtractor(entityInner)
            .ExtractAsync(messages);
        var facts = await new FrozenExtractionOverrides.FrozenFactExtractor(factInner)
            .ExtractAsync(messages);
        var preferences = await new FrozenExtractionOverrides.FrozenPreferenceExtractor(preferenceInner)
            .ExtractAsync(messages);
        var relationships = await new FrozenExtractionOverrides.FrozenRelationshipExtractor(relationshipInner)
            .ExtractAsync(messages);

        entities.Select(item => (item.Name, item.Type)).Should().Equal(
            ("Northstar P0 Labs", "ORGANIZATION"),
            ("Rowan Vale", "PERSON"));
        facts.Select(item => item.Predicate).Should().Equal("works_at", "leads");
        preferences.Should().ContainSingle()
            .Which.PreferenceText.Should().Be("prefers terse status notes");
        relationships.Should().ContainSingle()
            .Which.RelationshipType.Should().Be("LAB_P0_WORKS_AT");

        await entityInner.DidNotReceiveWithAnyArgs()
            .ExtractAsync(default!, default);
        await factInner.DidNotReceiveWithAnyArgs()
            .ExtractAsync(default!, default);
        await preferenceInner.DidNotReceiveWithAnyArgs()
            .ExtractAsync(default!, default);
        await relationshipInner.DidNotReceiveWithAnyArgs()
            .ExtractAsync(default!, default);
    }

    [Fact]
    public async Task NonFrozenInput_DelegatesUnchanged()
    {
        var expected = new[]
        {
            new ExtractedEntity { Name = "delegate-result", Type = "TEST" },
        };
        var inner = Substitute.For<IEntityExtractor>();
        var messages = new[]
        {
            new Message
            {
                MessageId = "ordinary",
                ConversationId = "ordinary-conversation",
                SessionId = "ordinary-session",
                Role = "user",
                Content = "ordinary source",
                TimestampUtc = DateTimeOffset.UnixEpoch,
            },
        };
        inner.ExtractAsync(messages, Arg.Any<CancellationToken>())
            .Returns(expected);
        var sut = new FrozenExtractionOverrides.FrozenEntityExtractor(inner);

        var actual = await sut.ExtractAsync(messages);

        actual.Should().BeSameAs(expected);
        await inner.Received(1).ExtractAsync(messages, Arg.Any<CancellationToken>());
    }

    private static IReadOnlyList<Message> FrozenMessages() =>
    [
        new()
        {
            MessageId = "p0-source",
            ConversationId = "p0-conversation",
            SessionId = "p0-session",
            Role = "user",
            Content = FrozenExtractionOverrides.SourceMarker,
            TimestampUtc = DateTimeOffset.UnixEpoch,
        },
    ];
}
