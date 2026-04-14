using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

public sealed class MultiExtractorPipelineTests
{
    private static readonly IReadOnlyList<Message> TestMessages = new[]
    {
        new Message
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            SessionId = "session-1",
            Role = "user",
            Content = "Hello world",
            TimestampUtc = DateTimeOffset.UtcNow
        }
    };

    private static ExtractionRequest MakeRequest(ExtractionTypes types = ExtractionTypes.All) =>
        new() { Messages = TestMessages, SessionId = "session-1", TypesToExtract = types };

    private static IOptions<ExtractionOptions> Options(MergeStrategyType strategy = MergeStrategyType.Union) =>
        Microsoft.Extensions.Options.Options.Create(new ExtractionOptions { MergeStrategy = strategy });

    private static ILogger<MultiExtractorPipeline> Logger =>
        NullLogger<MultiExtractorPipeline>.Instance;

    // --- Basic pipeline tests ---

    [Fact]
    public async Task ExtractAsync_NoExtractors_ReturnsEmptyResult()
    {
        var pipeline = new MultiExtractorPipeline(
            Array.Empty<IEntityExtractor>(),
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Entities.Should().BeEmpty();
        result.Facts.Should().BeEmpty();
        result.Preferences.Should().BeEmpty();
        result.Relationships.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_SingleExtractor_PassesThroughWithoutMerge()
    {
        var entityExtractor = Substitute.For<IEntityExtractor>();
        entityExtractor.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Bob") });

        var pipeline = new MultiExtractorPipeline(
            new[] { entityExtractor },
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Entities.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractAsync_MultipleEntityExtractors_MergesResults()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Bob") });

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Charlie") });

        var pipeline = new MultiExtractorPipeline(
            new[] { ext1, ext2 },
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.Union),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        // Union: Alice + Bob + Charlie = 3.
        result.Entities.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExtractAsync_MultipleFactExtractors_MergesResults()
    {
        var ext1 = Substitute.For<IFactExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "cats" }
            });

        var ext2 = Substitute.For<IFactExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "cats" },
                new ExtractedFact { Subject = "Bob", Predicate = "works_at", Object = "Acme" }
            });

        var pipeline = new MultiExtractorPipeline(
            Array.Empty<IEntityExtractor>(),
            new[] { ext1, ext2 },
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.Union),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        // Union dedup by SPO: 2 unique facts.
        result.Facts.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractAsync_IntersectionStrategy_OnlyKeepsCommon()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Bob") });

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Charlie") });

        var pipeline = new MultiExtractorPipeline(
            new[] { ext1, ext2 },
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.Intersection),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        // Only Alice is common to both.
        result.Entities.Should().ContainSingle()
            .Which.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task ExtractAsync_CascadeStrategy_UsesFirstNonEmpty()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedEntity>());

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("FromSecond") });

        var pipeline = new MultiExtractorPipeline(
            new[] { ext1, ext2 },
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.Cascade),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Entities.Should().ContainSingle()
            .Which.Name.Should().Be("FromSecond");
    }

    [Fact]
    public async Task ExtractAsync_ExtractorThrows_GracefullyReturnsEmpty()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Survivor") });

        var pipeline = new MultiExtractorPipeline(
            new[] { ext1, ext2 },
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.Union),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Entities.Should().ContainSingle()
            .Which.Name.Should().Be("Survivor");
    }

    [Fact]
    public async Task ExtractAsync_FirstSuccessStrategy_SkipsFailedExtractors()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Success") });

        var pipeline = new MultiExtractorPipeline(
            new[] { ext1, ext2 },
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.FirstSuccess),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Entities.Should().ContainSingle()
            .Which.Name.Should().Be("Success");
    }

    [Fact]
    public async Task ExtractAsync_ConfidenceStrategy_KeepsBestPerEntity()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice", 0.6) });

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice", 0.95) });

        var pipeline = new MultiExtractorPipeline(
            new[] { ext1, ext2 },
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.Confidence),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Entities.Should().ContainSingle()
            .Which.Confidence.Should().Be(0.95);
    }

    [Fact]
    public async Task ExtractAsync_OnlyEntitiesRequested_DoesNotRunOtherExtractors()
    {
        var entityExt = Substitute.For<IEntityExtractor>();
        entityExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice") });

        var factExt = Substitute.For<IFactExtractor>();
        var prefExt = Substitute.For<IPreferenceExtractor>();
        var relExt = Substitute.For<IRelationshipExtractor>();

        var pipeline = new MultiExtractorPipeline(
            new[] { entityExt },
            new[] { factExt },
            new[] { prefExt },
            new[] { relExt },
            Options(),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest(ExtractionTypes.Entities));

        result.Entities.Should().ContainSingle();
        result.Facts.Should().BeEmpty();
        result.Preferences.Should().BeEmpty();
        result.Relationships.Should().BeEmpty();

        await factExt.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await prefExt.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await relExt.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_MetadataContainsMergeStrategy()
    {
        var pipeline = new MultiExtractorPipeline(
            Array.Empty<IEntityExtractor>(),
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.Cascade),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Metadata.Should().ContainKey("mergeStrategy")
            .WhoseValue.Should().Be("Cascade");
    }

    [Fact]
    public async Task ExtractAsync_MultiplePreferenceExtractors_MergesResults()
    {
        var ext1 = Substitute.For<IPreferenceExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedPreference { Category = "food", PreferenceText = "likes pizza" }
            });

        var ext2 = Substitute.For<IPreferenceExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedPreference { Category = "food", PreferenceText = "likes pizza" },
                new ExtractedPreference { Category = "drink", PreferenceText = "prefers coffee" }
            });

        var pipeline = new MultiExtractorPipeline(
            Array.Empty<IEntityExtractor>(),
            Array.Empty<IFactExtractor>(),
            new[] { ext1, ext2 },
            Array.Empty<IRelationshipExtractor>(),
            Options(MergeStrategyType.Union),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Preferences.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractAsync_MultipleRelationshipExtractors_MergesResults()
    {
        var ext1 = Substitute.For<IRelationshipExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", RelationshipType = "KNOWS", TargetEntity = "Bob" }
            });

        var ext2 = Substitute.For<IRelationshipExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", RelationshipType = "KNOWS", TargetEntity = "Bob" },
                new ExtractedRelationship { SourceEntity = "Alice", RelationshipType = "WORKS_WITH", TargetEntity = "Charlie" }
            });

        var pipeline = new MultiExtractorPipeline(
            Array.Empty<IEntityExtractor>(),
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            new[] { ext1, ext2 },
            Options(MergeStrategyType.Union),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.Relationships.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractAsync_SourceMessageIds_SetCorrectly()
    {
        var pipeline = new MultiExtractorPipeline(
            Array.Empty<IEntityExtractor>(),
            Array.Empty<IFactExtractor>(),
            Array.Empty<IPreferenceExtractor>(),
            Array.Empty<IRelationshipExtractor>(),
            Options(),
            Logger);

        var result = await pipeline.ExtractAsync(MakeRequest());

        result.SourceMessageIds.Should().ContainSingle()
            .Which.Should().Be("msg-1");
    }

    // --- Helpers ---

    private static ExtractedEntity MakeEntity(string name, double confidence = 1.0) =>
        new() { Name = name, Type = "Person", Confidence = confidence };
}
