using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.AgentFramework;

public sealed class Neo4jChatHistoryProviderTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private static readonly DateTimeOffset _now = new(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);

    public Neo4jChatHistoryProviderTests()
    {
        _clock.UtcNow.Returns(_now);
        _idGen.GenerateId().Returns("test-id");
    }

    private Neo4jChatHistoryProvider CreateSut(AgentFrameworkOptions? options = null) =>
        new(
            _memoryService,
            _clock,
            _idGen,
            options ?? new AgentFrameworkOptions(),
            NullLogger<Neo4jChatHistoryProvider>.Instance);

    [Fact]
    public void Constructor_NullMemoryService_Throws() =>
        FluentActions.Invoking(() => new Neo4jChatHistoryProvider(
            null!, _clock, _idGen, new AgentFrameworkOptions(),
            NullLogger<Neo4jChatHistoryProvider>.Instance))
        .Should().Throw<ArgumentNullException>().WithParameterName("memoryService");

    [Fact]
    public void Constructor_NullOptions_Throws() =>
        FluentActions.Invoking(() => new Neo4jChatHistoryProvider(
            _memoryService, _clock, _idGen, null!,
            NullLogger<Neo4jChatHistoryProvider>.Instance))
        .Should().Throw<ArgumentNullException>().WithParameterName("options");

    [Fact]
    public void Constructor_NullClock_Throws() =>
        FluentActions.Invoking(() => new Neo4jChatHistoryProvider(
            _memoryService, null!, _idGen, new AgentFrameworkOptions(),
            NullLogger<Neo4jChatHistoryProvider>.Instance))
        .Should().Throw<ArgumentNullException>().WithParameterName("clock");

    [Fact]
    public void Constructor_NullLogger_Throws() =>
        FluentActions.Invoking(() => new Neo4jChatHistoryProvider(
            _memoryService, _clock, _idGen, new AgentFrameworkOptions(), null!))
        .Should().Throw<ArgumentNullException>().WithParameterName("logger");

    [Fact]
    public void StateKeys_ContainsTypeName()
    {
        var sut = CreateSut();
        sut.StateKeys.Should().Contain(nameof(Neo4jChatHistoryProvider));
    }

    [Fact]
    public void IsAssignableTo_ChatHistoryProvider()
    {
        var sut = CreateSut();
        sut.Should().BeAssignableTo<Microsoft.Agents.AI.ChatHistoryProvider>();
    }

    [Fact]
    public void AutoExtractEnabled_ConstructsWithoutError()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        sut.Should().NotBeNull();
    }
}
