using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Core.Resolution;

namespace Neo4j.AgentMemory.Tests.Unit.Resolution;

public sealed class FuzzyMatchEntityMatcherTests
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

    private static FuzzyMatchEntityMatcher CreateSut(double threshold = 0.85) =>
        new(new EntityResolutionOptions { FuzzyMatchThreshold = threshold });

    [Fact]
    public async Task TryMatchAsync_TokenSortMatch_JohnSmithVsSmithJohn_ReturnsResult()
    {
        // token_sort_ratio rearranges tokens before comparing, so "John Smith" == "Smith John"
        var existing = new[] { MakeEntity("John Smith") };
        var sut = CreateSut(threshold: 0.85);

        var result = await sut.TryMatchAsync(MakeCandidate("Smith John"), existing);

        result.Should().NotBeNull();
        result!.MatchType.Should().Be("fuzzy");
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.85);
    }

    [Fact]
    public async Task TryMatchAsync_BelowThreshold_ReturnsNull()
    {
        var existing = new[] { MakeEntity("Alice Wonderland") };
        var sut = CreateSut(threshold: 0.85);

        // "Bob" vs "Alice Wonderland" is clearly below threshold
        var result = await sut.TryMatchAsync(MakeCandidate("Bob"), existing);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryMatchAsync_AboveThreshold_ReturnsCorrectConfidence()
    {
        var existing = new[] { MakeEntity("OpenAI") };
        var sut = CreateSut(threshold: 0.5); // low threshold to ensure match

        var result = await sut.TryMatchAsync(MakeCandidate("OpenAI"), existing);

        result.Should().NotBeNull();
        result!.Confidence.Should().BeGreaterThanOrEqualTo(0.5);
        result.Confidence.Should().BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public async Task TryMatchAsync_AliasFuzzyMatch_ReturnsResult()
    {
        var existing = new[] { MakeEntity("International Business Machines", aliases: "IBM Corporation") };
        var sut = CreateSut(threshold: 0.65);

        // "IBM Corp" vs "IBM Corporation" scores ~70 on token sort ratio
        var result = await sut.TryMatchAsync(MakeCandidate("IBM Corp"), existing);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task TryMatchAsync_EmptyCandidates_ReturnsNull()
    {
        var sut = CreateSut();
        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), Array.Empty<Entity>());
        result.Should().BeNull();
    }

    [Fact]
    public void MatchType_IsFuzzy()
    {
        CreateSut().MatchType.Should().Be("fuzzy");
    }

    [Fact]
    public async Task TryMatchAsync_ConfidenceIsIn0To1Range()
    {
        var existing = new[] { MakeEntity("Alice") };
        var sut = CreateSut(threshold: 0.0); // accept any score

        var result = await sut.TryMatchAsync(MakeCandidate("Alice"), existing);

        result.Should().NotBeNull();
        result!.Confidence.Should().BeInRange(0.0, 1.0);
    }
}
