using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
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

    // ---- review round 2: similarity alone is not "the same statement" ----

    private static ExtractedFact F(string subject, string predicate, string @object) =>
        new() { Subject = subject, Predicate = predicate, Object = @object, Confidence = 0.9 };

    [Theory]
    [InlineData("user", "has", "2 kids", "user", "has", "3 kids")]
    [InlineData("user", "is", "vegetarian", "user", "is not", "vegetarian")]
    [InlineData("user", "eats", "meat", "user", "doesn't eat", "meat")]
    [InlineData("user", "started on", "2024-03-01", "user", "started on", "2024-04-01")]
    [InlineData("Tomás Silva", "moved to", "analytics", "Tomás Pereira", "moved to", "analytics")]
    [InlineData("user", "works as", "data engineer at Northwind", "user", "works at", "Northwind")]
    public void Facts_that_differ_in_what_they_say_are_never_one_statement(
        string s1, string p1, string o1, string s2, string p2, string o2)
    {
        PersistenceStage.MayBeOneStatement(F(s1, p1, o1), F(s2, p2, o2)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Tomás Silva", "moved to", "analytics", "Tomás Silva", "moved to", "analytics team")]
    [InlineData("user", "requested help with", "the dashboard", "user", "needs help with", "the dashboard")]
    public void Rephrasings_of_one_slot_may_be_one_statement(string s1, string p1, string o1, string s2, string p2, string o2)
    {
        PersistenceStage.MayBeOneStatement(F(s1, p1, o1), F(s2, p2, o2)).Should().BeTrue();
    }

    [Fact]
    public void Different_validity_windows_are_different_statements()
    {
        var march = F("user", "lives in", "Porto") with { ValidFrom = DateTimeOffset.Parse("2026-03-01T00:00:00Z") };
        var april = march with { ValidFrom = DateTimeOffset.Parse("2026-04-01T00:00:00Z") };

        PersistenceStage.MayBeOneStatement(march, april).Should().BeFalse();
    }

    [Fact]
    public async Task A_merged_phrasing_is_reported_and_the_users_words_and_date_survive()
    {
        // The assistant's paraphrase is the more confident one, and only the user's phrasing carried a date.
        var extraction = new ExtractionStageResult
        {
            SourceMessageIds = ["message-1"],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
            FilteredFacts =
            [
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics", Confidence = 0.8,
                    SourceRole = "user", ValidFrom = DateTimeOffset.Parse("2026-09-01T00:00:00Z") },
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics team", Confidence = 0.95,
                    SourceRole = "assistant" },
            ],
        };

        var result = await Sut(dedup: true).PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        var stored = _upserted.Should().ContainSingle().Subject;
        stored.Object.Should().Be("analytics", "the user's own words beat the assistant's paraphrase");
        stored.ValidFrom.Should().Be(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var merged = result.Outcomes.Should().ContainSingle(o => o.Status == IngestionItemStatus.Skipped).Subject;
        merged.SourceKey.Should().Be("Tomás Silva moved to analytics team");
        merged.ErrorCode.Should().Be(MemoryErrorCodes.FactMergedWithinExtraction);
    }

    [Fact]
    public async Task The_dropped_phrasings_date_is_kept_when_the_winner_has_none()
    {
        var extraction = new ExtractionStageResult
        {
            SourceMessageIds = ["message-1"],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
            FilteredFacts =
            [
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics", Confidence = 0.8,
                    ValidFrom = DateTimeOffset.Parse("2026-09-01T00:00:00Z") },
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics team", Confidence = 0.9 },
            ],
        };

        await Sut(dedup: true).PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        var stored = _upserted.Should().ContainSingle().Subject;
        stored.Object.Should().Be("analytics team");
        stored.ValidFrom.Should().Be(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
    }
}
