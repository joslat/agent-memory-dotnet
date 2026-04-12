using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Core.Resolution;

namespace Neo4j.AgentMemory.Tests.Unit.Resolution;

public sealed class ExactMatchEntityMatcherTests
{
    private static readonly DateTimeOffset FixedTime = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Entity MakeEntity(string name, string? canonical = null, params string[] aliases) =>
        new()
        {
            EntityId = Guid.NewGuid().ToString("N"),
            Name = name,
            CanonicalName = canonical,
            Type = "Person",
            Confidence = 1.0,
            Aliases = aliases,
            CreatedAtUtc = FixedTime
        };

    private static ExtractedEntity MakeCandidate(string name) =>
        new() { Name = name, Type = "Person" };

    private readonly ExactMatchEntityMatcher _sut = new();

    [Fact]
    public async Task TryMatchAsync_ExactNameMatch_ReturnsConfidence1()
    {
        var existing = new[] { MakeEntity("Alice") };
        var result = await _sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().NotBeNull();
        result!.Confidence.Should().Be(1.0);
        result.MatchType.Should().Be("exact");
        result.ResolvedEntity.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task TryMatchAsync_CaseInsensitiveNameMatch_ReturnsResult()
    {
        var existing = new[] { MakeEntity("Alice") };
        var result = await _sut.TryMatchAsync(MakeCandidate("alice"), existing);

        result.Should().NotBeNull();
        result!.ResolvedEntity.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task TryMatchAsync_MatchOnCanonicalName_ReturnsResult()
    {
        var existing = new[] { MakeEntity("Dr. Alice Smith", canonical: "Alice Smith") };
        var result = await _sut.TryMatchAsync(MakeCandidate("Alice Smith"), existing);

        result.Should().NotBeNull();
        result!.ResolvedEntity.Name.Should().Be("Dr. Alice Smith");
    }

    [Fact]
    public async Task TryMatchAsync_MatchOnAlias_ReturnsResult()
    {
        var existing = new[] { MakeEntity("Alice Smith", null, "Alice", "A. Smith") };
        var result = await _sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().NotBeNull();
        result!.ResolvedEntity.Name.Should().Be("Alice Smith");
    }

    [Fact]
    public async Task TryMatchAsync_NoMatch_ReturnsNull()
    {
        var existing = new[] { MakeEntity("Alice") };
        var result = await _sut.TryMatchAsync(MakeCandidate("Bob"), existing);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryMatchAsync_EmptyCandidates_ReturnsNull()
    {
        var result = await _sut.TryMatchAsync(MakeCandidate("Alice"), Array.Empty<Entity>());
        result.Should().BeNull();
    }

    [Fact]
    public void MatchType_IsExact()
    {
        _sut.MatchType.Should().Be("exact");
    }
}
