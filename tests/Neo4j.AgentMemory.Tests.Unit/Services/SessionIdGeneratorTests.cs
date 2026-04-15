using FluentAssertions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Core.Services;

namespace Neo4j.AgentMemory.Tests.Unit.Services;

public sealed class SessionIdGeneratorTests
{
    private static SessionIdGenerator CreateSut(SessionStrategy strategy) =>
        new(Options.Create(new ShortTermMemoryOptions { SessionStrategy = strategy }));

    [Fact]
    public void PerConversation_ReturnsNewGuidEachCall()
    {
        var sut = CreateSut(SessionStrategy.PerConversation);

        var id1 = sut.GenerateSessionId();
        var id2 = sut.GenerateSessionId();

        id1.Should().NotBeNullOrEmpty();
        id2.Should().NotBeNullOrEmpty();
        id1.Should().NotBe(id2);
        Guid.TryParse(id1, out _).Should().BeTrue("PerConversation should produce a valid GUID string");
    }

    [Fact]
    public void PerConversation_IgnoresUserId()
    {
        var sut = CreateSut(SessionStrategy.PerConversation);

        var id = sut.GenerateSessionId("user-abc");

        Guid.TryParse(id, out _).Should().BeTrue();
    }

    [Fact]
    public void PerDay_WithUserId_ReturnsUserIdDashDate()
    {
        var sut = CreateSut(SessionStrategy.PerDay);
        var expectedDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var id = sut.GenerateSessionId("alice");

        id.Should().Be($"alice-{expectedDate}");
    }

    [Fact]
    public void PerDay_WithoutUserId_UsesAnonymous()
    {
        var sut = CreateSut(SessionStrategy.PerDay);
        var expectedDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var id = sut.GenerateSessionId();

        id.Should().Be($"anonymous-{expectedDate}");
    }

    [Fact]
    public void PerDay_TwoCallsSameDay_ReturnSameId()
    {
        var sut = CreateSut(SessionStrategy.PerDay);

        var id1 = sut.GenerateSessionId("bob");
        var id2 = sut.GenerateSessionId("bob");

        id1.Should().Be(id2);
    }

    [Fact]
    public void PersistentPerUser_WithUserId_ReturnsUserId()
    {
        var sut = CreateSut(SessionStrategy.PersistentPerUser);

        var id = sut.GenerateSessionId("user-xyz");

        id.Should().Be("user-xyz");
    }

    [Fact]
    public void PersistentPerUser_WithoutUserId_Throws()
    {
        var sut = CreateSut(SessionStrategy.PersistentPerUser);

        var act = () => sut.GenerateSessionId();

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("userId");
    }

    [Fact]
    public void PersistentPerUser_WithNullUserId_Throws()
    {
        var sut = CreateSut(SessionStrategy.PersistentPerUser);

        var act = () => sut.GenerateSessionId(null);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("userId");
    }
}
