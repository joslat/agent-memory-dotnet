using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// I-6: one message produced "moved to | analytics team" and "moved to | analytics" (two phrasings, one
/// fact). Near-identical facts of one extraction are stored once.
/// </summary>
public sealed class WithinExtractionDedupTests
{
    private readonly List<Fact> _upserted = [];

    // Vectors by topic: the two "analytics" phrasings are the same statement; "dashboard" is another.
    private static float[] VectorFor(string text) =>
        text.Contains("analytics", StringComparison.Ordinal) ? [1f, 0.02f, 0f, 0f]
        : text.Contains("dashboard", StringComparison.Ordinal) ? [0f, 1f, 0f, 0f]
        : [0f, 0f, 1f, 0f];

    private PersistenceStage Sut(bool dedup)
    {
        var facts = Substitute.For<IFactRepository>();
        facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => { _upserted.Add(call.Arg<Fact>()); return call.Arg<Fact>(); });
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => VectorFor(call.Arg<string>()));
        embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<float[]>)call.Arg<IReadOnlyList<string>>().Select(VectorFor).ToList());
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));

        return new PersistenceStage(embeddings, Substitute.For<IEntityRepository>(), facts, Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(), clock, ids, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { DeduplicateWithinExtraction = dedup, EnableBatchMemoryUpserts = false }));
    }

    private static ExtractionStageResult Extraction() => new()
    {
        SourceMessageIds = ["message-1"],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
        FilteredFacts =
        [
            new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics", Confidence = 0.7 },
            new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics team", Confidence = 0.9 },
            new ExtractedFact { Subject = "Tomás Silva", Predicate = "needs help with", Object = "a dashboard", Confidence = 0.8 },
        ],
    };

    [Fact]
    public async Task On_two_phrasings_of_one_fact_are_stored_once_the_more_confident_one()
    {
        await Sut(dedup: true).PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        _upserted.Select(f => f.Object).Should().BeEquivalentTo(["analytics team", "a dashboard"]);
    }

    [Fact]
    public async Task Off_every_phrasing_is_stored()
    {
        await Sut(dedup: false).PersistAsync(Extraction(), ownerId: "owner-1", cancellationToken: CancellationToken.None);

        _upserted.Should().HaveCount(3);
    }
}
