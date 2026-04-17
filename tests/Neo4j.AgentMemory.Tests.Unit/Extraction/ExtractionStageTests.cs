using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Unit tests for <see cref="ExtractionStage"/>:
/// extractor execution, multi-extractor merging, confidence filtering,
/// entity validation, and entity resolution.
/// Migrated from the former MultiExtractorPipelineTests + relevant subset of MemoryExtractionPipelineTests.
/// </summary>
public sealed class ExtractionStageTests
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

    private readonly IEntityResolver _resolver = Substitute.For<IEntityResolver>();

    private ExtractionStage CreateSut(
        IEnumerable<IEntityExtractor>? entityExtractors = null,
        IEnumerable<IFactExtractor>? factExtractors = null,
        IEnumerable<IPreferenceExtractor>? prefExtractors = null,
        IEnumerable<IRelationshipExtractor>? relExtractors = null,
        ExtractionOptions? options = null)
    {
        // Resolver echoes back a basic Entity by default
        _resolver
            .ResolveEntityAsync(Arg.Any<ExtractedEntity>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var e = ci.Arg<ExtractedEntity>();
                return Task.FromResult(new Entity
                {
                    EntityId = $"id-{e.Name.ToLowerInvariant()}",
                    Name = e.Name,
                    Type = e.Type,
                    Confidence = e.Confidence,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
            });

        return new ExtractionStage(
            entityExtractors ?? Array.Empty<IEntityExtractor>(),
            factExtractors ?? Array.Empty<IFactExtractor>(),
            prefExtractors ?? Array.Empty<IPreferenceExtractor>(),
            relExtractors ?? Array.Empty<IRelationshipExtractor>(),
            _resolver,
            Options.Create(options ?? new ExtractionOptions()),
            NullLogger<ExtractionStage>.Instance);
    }

    private static ExtractionRequest MakeRequest(ExtractionTypes types = ExtractionTypes.All) =>
        new() { Messages = TestMessages, SessionId = "session-1", TypesToExtract = types };

    private static ExtractedEntity MakeEntity(string name, double confidence = 1.0) =>
        new() { Name = name, Type = "Person", Confidence = confidence };

    // ── Single-extractor baseline ──

    [Fact]
    public async Task ExtractAsync_NoExtractors_ReturnsEmptyResult()
    {
        var sut = CreateSut();
        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().BeEmpty();
        result.RawFacts.Should().BeEmpty();
        result.RawPreferences.Should().BeEmpty();
        result.RawRelationships.Should().BeEmpty();
        result.ResolvedEntityMap.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_EmptyMessages_ReturnsEmptyResult()
    {
        var ext = Substitute.For<IEntityExtractor>();
        ext.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice") });

        var sut = CreateSut(entityExtractors: new[] { ext });
        var result = await sut.ExtractAsync(Array.Empty<Message>(), ExtractionTypes.All);

        // Even with extractors returning data, message IDs list is empty
        result.SourceMessageIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_SingleExtractor_ResolvesEntities()
    {
        var ext = Substitute.For<IEntityExtractor>();
        ext.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Bob") });

        var sut = CreateSut(entityExtractors: new[] { ext });
        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().HaveCount(2);
        result.ResolvedEntityMap.Should().ContainKey("Alice");
        result.ResolvedEntityMap.Should().ContainKey("Bob");
    }

    // ── Confidence filtering ──

    [Fact]
    public async Task ExtractAsync_FiltersLowConfidenceEntities()
    {
        var ext = Substitute.For<IEntityExtractor>();
        ext.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                MakeEntity("Alice", 0.3), // below 0.5 threshold
                MakeEntity("Bob", 0.9)
            });

        var sut = CreateSut(
            entityExtractors: new[] { ext },
            options: new ExtractionOptions { MinConfidenceThreshold = 0.5 });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        // Raw includes all, but only Bob is resolved
        result.RawEntities.Should().HaveCount(2);
        result.ResolvedEntityMap.Should().ContainKey("Bob");
        result.ResolvedEntityMap.Should().NotContainKey("Alice");
    }

    [Fact]
    public async Task ExtractAsync_FiltersLowConfidenceFacts()
    {
        var ext = Substitute.For<IFactExtractor>();
        ext.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedFact { Subject = "A", Predicate = "b", Object = "c", Confidence = 0.3 },
                new ExtractedFact { Subject = "X", Predicate = "y", Object = "z", Confidence = 0.9 }
            });

        var sut = CreateSut(
            factExtractors: new[] { ext },
            options: new ExtractionOptions { MinConfidenceThreshold = 0.5 });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawFacts.Should().HaveCount(2);
        result.FilteredFacts.Should().ContainSingle()
            .Which.Subject.Should().Be("X");
    }

    // ── Entity validation ──

    [Fact]
    public async Task ExtractAsync_FiltersStopwordEntities()
    {
        var ext = Substitute.For<IEntityExtractor>();
        ext.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                MakeEntity("the", 0.9),  // stopword — should be rejected
                MakeEntity("Alice", 0.9) // valid
            });

        var sut = CreateSut(entityExtractors: new[] { ext });
        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.ResolvedEntityMap.Should().ContainKey("Alice");
        result.ResolvedEntityMap.Should().NotContainKey("the");

        await _resolver.DidNotReceive()
            .ResolveEntityAsync(
                Arg.Is<ExtractedEntity>(e => e.Name == "the"),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>());
    }

    // ── Relationship validation ──

    [Fact]
    public async Task ExtractAsync_FiltersRelationshipsWithMissingEndpoints()
    {
        // No entity extractor — so no resolved entities
        var relExt = Substitute.For<IRelationshipExtractor>();
        relExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedRelationship { SourceEntity = "Ghost", TargetEntity = "Nobody", RelationshipType = "KNOWS", Confidence = 0.9 }
            });

        var sut = CreateSut(relExtractors: new[] { relExt });
        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.FilteredRelationships.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_IncludesRelationshipsWithBothEndpointsResolved()
    {
        var entityExt = Substitute.For<IEntityExtractor>();
        entityExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Bob") });

        var relExt = Substitute.For<IRelationshipExtractor>();
        relExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", TargetEntity = "Bob", RelationshipType = "KNOWS", Confidence = 0.9 }
            });

        var sut = CreateSut(entityExtractors: new[] { entityExt }, relExtractors: new[] { relExt });
        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.FilteredRelationships.Should().ContainSingle()
            .Which.RelationshipType.Should().Be("KNOWS");
    }

    // ── ExtractionTypes flag ──

    [Fact]
    public async Task ExtractAsync_OnlyEntitiesRequested_DoesNotRunOtherExtractors()
    {
        var entityExt = Substitute.For<IEntityExtractor>();
        entityExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice") });

        var factExt = Substitute.For<IFactExtractor>();
        var prefExt = Substitute.For<IPreferenceExtractor>();
        var relExt = Substitute.For<IRelationshipExtractor>();

        var sut = CreateSut(
            entityExtractors: new[] { entityExt },
            factExtractors: new[] { factExt },
            prefExtractors: new[] { prefExt },
            relExtractors: new[] { relExt });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.Entities);

        result.RawEntities.Should().ContainSingle();
        result.RawFacts.Should().BeEmpty();
        result.RawPreferences.Should().BeEmpty();
        result.RawRelationships.Should().BeEmpty();

        await factExt.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await prefExt.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
        await relExt.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    // ── Extractor fault tolerance ──

    [Fact]
    public async Task ExtractAsync_ExtractorThrows_ContinuesWithEmptyList()
    {
        var failing = Substitute.For<IEntityExtractor>();
        failing.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var sut = CreateSut(entityExtractors: new[] { failing });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().BeEmpty();
    }

    // ── Multi-extractor merge strategies ──

    [Fact]
    public async Task ExtractAsync_MultipleEntityExtractors_UnionMerge()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Bob") });

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Charlie") });

        var sut = CreateSut(
            entityExtractors: new[] { ext1, ext2 },
            options: new ExtractionOptions { MergeStrategy = MergeStrategyType.Union });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        // Union: Alice + Bob + Charlie = 3
        result.RawEntities.Should().HaveCount(3);
        result.ResolvedEntityMap.Keys.Should().BeEquivalentTo(new[] { "Alice", "Bob", "Charlie" });
    }

    [Fact]
    public async Task ExtractAsync_MultipleEntityExtractors_IntersectionMerge()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Bob") });

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Charlie") });

        var sut = CreateSut(
            entityExtractors: new[] { ext1, ext2 },
            options: new ExtractionOptions { MergeStrategy = MergeStrategyType.Intersection });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        // Only Alice is common to both
        result.RawEntities.Should().ContainSingle()
            .Which.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task ExtractAsync_MultipleEntityExtractors_CascadeMerge()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedEntity>());

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("FromSecond") });

        var sut = CreateSut(
            entityExtractors: new[] { ext1, ext2 },
            options: new ExtractionOptions { MergeStrategy = MergeStrategyType.Cascade });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().ContainSingle()
            .Which.Name.Should().Be("FromSecond");
    }

    [Fact]
    public async Task ExtractAsync_MultipleEntityExtractors_ConfidenceMerge_KeepsBest()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice", 0.6) });

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice", 0.95) });

        var sut = CreateSut(
            entityExtractors: new[] { ext1, ext2 },
            options: new ExtractionOptions { MergeStrategy = MergeStrategyType.Confidence });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().ContainSingle()
            .Which.Confidence.Should().Be(0.95);
    }

    [Fact]
    public async Task ExtractAsync_FirstSuccessStrategy_SkipsFailedExtractors()
    {
        var failing = Substitute.For<IEntityExtractor>();
        failing.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var succeeding = Substitute.For<IEntityExtractor>();
        succeeding.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Success") });

        var sut = CreateSut(
            entityExtractors: new[] { failing, succeeding },
            options: new ExtractionOptions { MergeStrategy = MergeStrategyType.FirstSuccess });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().ContainSingle()
            .Which.Name.Should().Be("Success");
    }

    [Fact]
    public async Task ExtractAsync_MultipleFactExtractors_UnionMerge()
    {
        var ext1 = Substitute.For<IFactExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "cats", Confidence = 1.0 } });

        var ext2 = Substitute.For<IFactExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedFact { Subject = "Alice", Predicate = "likes", Object = "cats", Confidence = 1.0 },
                new ExtractedFact { Subject = "Bob", Predicate = "works_at", Object = "Acme", Confidence = 1.0 }
            });

        var sut = CreateSut(
            factExtractors: new[] { ext1, ext2 },
            options: new ExtractionOptions { MergeStrategy = MergeStrategyType.Union });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        // Union dedup by SPO: 2 unique facts
        result.RawFacts.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractAsync_MultiplePreferenceExtractors_UnionMerge()
    {
        var ext1 = Substitute.For<IPreferenceExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ExtractedPreference { Category = "food", PreferenceText = "likes pizza", Confidence = 1.0 } });

        var ext2 = Substitute.For<IPreferenceExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedPreference { Category = "food", PreferenceText = "likes pizza", Confidence = 1.0 },
                new ExtractedPreference { Category = "drink", PreferenceText = "prefers coffee", Confidence = 1.0 }
            });

        var sut = CreateSut(
            prefExtractors: new[] { ext1, ext2 },
            options: new ExtractionOptions { MergeStrategy = MergeStrategyType.Union });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawPreferences.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractAsync_MultipleRelationshipExtractors_UnionMerge()
    {
        var ext1 = Substitute.For<IRelationshipExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", RelationshipType = "KNOWS", TargetEntity = "Bob", Confidence = 1.0 }
            });

        var ext2 = Substitute.For<IRelationshipExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", RelationshipType = "KNOWS", TargetEntity = "Bob", Confidence = 1.0 },
                new ExtractedRelationship { SourceEntity = "Alice", RelationshipType = "WORKS_WITH", TargetEntity = "Charlie", Confidence = 1.0 }
            });

        var sut = CreateSut(
            relExtractors: new[] { ext1, ext2 },
            options: new ExtractionOptions { MergeStrategy = MergeStrategyType.Union });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawRelationships.Should().HaveCount(2);
    }

    // ── Metadata ──

    [Fact]
    public async Task ExtractAsync_ReturnsCorrectMergeStrategyInResult()
    {
        var sut = CreateSut(options: new ExtractionOptions { MergeStrategy = MergeStrategyType.Cascade });
        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.MergeStrategy.Should().Be(MergeStrategyType.Cascade);
    }

    [Fact]
    public async Task ExtractAsync_SourceMessageIds_SetCorrectly()
    {
        var sut = CreateSut();
        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.SourceMessageIds.Should().ContainSingle()
            .Which.Should().Be("msg-1");
    }

    // ── All extractors return empty ──

    [Fact]
    public async Task ExtractAsync_AllExtractorsReturnEmpty_ResultIsEmpty()
    {
        var entityExt = Substitute.For<IEntityExtractor>();
        entityExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedEntity>());

        var factExt = Substitute.For<IFactExtractor>();
        factExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedFact>());

        var prefExt = Substitute.For<IPreferenceExtractor>();
        prefExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedPreference>());

        var relExt = Substitute.For<IRelationshipExtractor>();
        relExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedRelationship>());

        var sut = CreateSut(
            entityExtractors: new[] { entityExt },
            factExtractors: new[] { factExt },
            prefExtractors: new[] { prefExt },
            relExtractors: new[] { relExt });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().BeEmpty();
        result.RawFacts.Should().BeEmpty();
        result.RawPreferences.Should().BeEmpty();
        result.RawRelationships.Should().BeEmpty();
        result.ResolvedEntityMap.Should().BeEmpty();
    }

    // ── Extractor counts in metadata ──

    [Fact]
    public async Task ExtractAsync_ExtractorCounts_ReflectRegisteredExtractors()
    {
        var ext1 = Substitute.For<IEntityExtractor>();
        ext1.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedEntity>());

        var ext2 = Substitute.For<IEntityExtractor>();
        ext2.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExtractedEntity>());

        var sut = CreateSut(entityExtractors: new[] { ext1, ext2 });
        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.EntityExtractorCount.Should().Be(2);
        result.FactExtractorCount.Should().Be(0);
        result.PreferenceExtractorCount.Should().Be(0);
        result.RelationshipExtractorCount.Should().Be(0);
    }

    // ── Multiple extractor fault tolerance ──

    [Fact]
    public async Task ExtractAsync_AllExtractorsThrow_ReturnsEmptyResult()
    {
        var failingEntity = Substitute.For<IEntityExtractor>();
        failingEntity.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var failingFact = Substitute.For<IFactExtractor>();
        failingFact.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("timeout"));

        var sut = CreateSut(
            entityExtractors: new[] { failingEntity },
            factExtractors: new[] { failingFact });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().BeEmpty();
        result.RawFacts.Should().BeEmpty();
    }

    // ── Entity resolution failure is handled per-entity ──

    [Fact]
    public async Task ExtractAsync_ResolutionThrowsForOneEntity_OtherEntitiesStillResolved()
    {
        var ext = Substitute.For<IEntityExtractor>();
        ext.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("BadEntity"), MakeEntity("Bob") });

        var sut = CreateSut(entityExtractors: new[] { ext });

        // Override resolver AFTER CreateSut so the specific matcher takes precedence
        _resolver
            .ResolveEntityAsync(
                Arg.Is<ExtractedEntity>(e => e.Name == "BadEntity"),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("resolution failed"));

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawEntities.Should().HaveCount(3);
        result.ResolvedEntityMap.Should().ContainKey("Alice");
        result.ResolvedEntityMap.Should().ContainKey("Bob");
        result.ResolvedEntityMap.Should().NotContainKey("BadEntity");
    }

    // ── Confidence filtering on relationships ──

    [Fact]
    public async Task ExtractAsync_LowConfidenceRelationship_FilteredOut()
    {
        var entityExt = Substitute.For<IEntityExtractor>();
        entityExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeEntity("Alice"), MakeEntity("Bob") });

        var relExt = Substitute.For<IRelationshipExtractor>();
        relExt.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedRelationship { SourceEntity = "Alice", TargetEntity = "Bob", RelationshipType = "KNOWS", Confidence = 0.1 }
            });

        var sut = CreateSut(
            entityExtractors: new[] { entityExt },
            relExtractors: new[] { relExt },
            options: new ExtractionOptions { MinConfidenceThreshold = 0.5 });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawRelationships.Should().ContainSingle();
        result.FilteredRelationships.Should().BeEmpty();
    }

    // ── Low-confidence preferences filtered ──

    [Fact]
    public async Task ExtractAsync_FiltersLowConfidencePreferences()
    {
        var ext = Substitute.For<IPreferenceExtractor>();
        ext.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ExtractedPreference { Category = "food", PreferenceText = "cheap pizza", Confidence = 0.2 },
                new ExtractedPreference { Category = "drink", PreferenceText = "espresso", Confidence = 0.8 }
            });

        var sut = CreateSut(
            prefExtractors: new[] { ext },
            options: new ExtractionOptions { MinConfidenceThreshold = 0.5 });

        var result = await sut.ExtractAsync(TestMessages, ExtractionTypes.All);

        result.RawPreferences.Should().HaveCount(2);
        result.FilteredPreferences.Should().ContainSingle()
            .Which.PreferenceText.Should().Be("espresso");
    }
}
