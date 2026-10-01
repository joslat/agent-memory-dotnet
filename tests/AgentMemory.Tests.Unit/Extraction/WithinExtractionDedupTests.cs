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

    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();

    private PersistenceStage Sut(bool dedup, bool supersede = false)
    {
        var facts = _facts;
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
            Options.Create(new ExtractionOptions
            {
                DeduplicateWithinExtraction = dedup, EnableBatchMemoryUpserts = false, SupersedeReplacedFacts = supersede,
            }));
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
    // 38.6 (F15): the same predicate, objects that each say something the other does not.
    [InlineData("user", "plans to run", "full marathon in May 2027", "user", "plans to run", "half marathon in April 2027")]
    [InlineData("user", "lives in", "Porto", "user", "lives in", "Lyon")]
    public void Facts_that_differ_in_what_they_say_are_never_one_statement(
        string s1, string p1, string o1, string s2, string p2, string o2)
    {
        PersistenceStage.MayBeOneStatement(F(s1, p1, o1), F(s2, p2, o2)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Tomás Silva", "moved to", "analytics", "Tomás Silva", "moved to", "analytics team")]
    [InlineData("user", "requested help with", "the dashboard", "user", "needs help with", "the dashboard")]
    // 38.6: one object is the other's fuller phrasing.
    [InlineData("user", "is training for", "half marathon", "user", "is training for", "the half marathon in April")]
    public void Rephrasings_of_one_slot_may_be_one_statement(string s1, string p1, string o1, string s2, string p2, string o2)
    {
        PersistenceStage.MayBeOneStatement(F(s1, p1, o1), F(s2, p2, o2)).Should().BeTrue();
    }

    /// <summary>
    /// 38.6 (F15). Show 04, run 11, the extraction as recorded: the new plan, and the old plan written as ended today.
    /// Their vectors are near-identical (here: identical), so they were merged, and the full marathon took the half
    /// marathon's end date: stored as valid until the day it was said, it stopped being live a day later.
    /// </summary>
    [Fact]
    public async Task Two_different_plans_said_together_stay_two_and_keep_their_own_dates()
    {
        var said = DateTimeOffset.Parse("2026-09-28T00:00:00Z");
        var extraction = new ExtractionStageResult
        {
            SourceMessageIds = ["message-1"],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
            FilteredFacts =
            [
                new ExtractedFact { Subject = "user", Predicate = "plans to run", Object = "full marathon in May 2027", Confidence = 0.9,
                    ValidFrom = said, ValidFromPrecision = DatePrecision.Day },
                new ExtractedFact { Subject = "user", Predicate = "plans to run", Object = "half marathon in April 2027", Confidence = 0.9,
                    ValidFrom = said, ValidFromPrecision = DatePrecision.Day, ValidUntil = said, ValidUntilPrecision = DatePrecision.Day,
                    Replaces = "half marathon in April" },
            ],
        };

        await Sut(dedup: true).PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        _upserted.Select(f => f.Object).Should().BeEquivalentTo(["full marathon in May 2027", "half marathon in April 2027"]);
        _upserted.Single(f => f.Object.StartsWith("full", StringComparison.Ordinal)).ValidUntil
            .Should().BeNull("the new plan has no end; only the old one ended today");
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
                    SourceRole = "user", ValidFrom = DateTimeOffset.Parse("2026-09-01T00:00:00Z"), ValidFromPrecision = DatePrecision.Month },
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics team", Confidence = 0.95,
                    SourceRole = "assistant" },
            ],
        };

        var result = await Sut(dedup: true).PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        var stored = _upserted.Should().ContainSingle().Subject;
        stored.Object.Should().Be("analytics", "the user's own words beat the assistant's paraphrase");
        stored.ValidFrom.Should().Be(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        stored.ValidFromPrecision.Should().Be(DatePrecision.Month, "36.1: the stored fact keeps how precisely its date was stated");
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
                    ValidFrom = DateTimeOffset.Parse("2026-09-01T00:00:00Z"), ValidFromPrecision = DatePrecision.Month },
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics team", Confidence = 0.9 },
            ],
        };

        await Sut(dedup: true).PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        var stored = _upserted.Should().ContainSingle().Subject;
        stored.Object.Should().Be("analytics team");
        stored.ValidFrom.Should().Be(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        stored.ValidFromPrecision.Should().Be(DatePrecision.Month, "a date and its precision are carried together");
    }

    [Fact]
    public async Task The_day_an_event_happened_is_carried_like_its_other_dates()
    {
        var extraction = new ExtractionStageResult
        {
            FilteredFacts =
            [
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics", Confidence = 0.8,
                    OccurredOn = DateTimeOffset.Parse("2026-09-01T00:00:00Z"), OccurredOnPrecision = DatePrecision.Month },
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics team", Confidence = 0.9 },
            ],
        };

        await Sut(dedup: true).PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        var stored = _upserted.Should().ContainSingle().Subject;
        stored.OccurredOn.Should().Be(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        stored.OccurredOnPrecision.Should().Be(DatePrecision.Month);
    }

    [Fact]
    public async Task The_dropped_phrasings_correction_is_carried_like_its_date()
    {
        // 36.4: a correction the merged-away phrasing carried still closes what it replaces.
        _facts.GetBySubjectAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(
            [
                new Fact { FactId = "finance", Subject = "Tomás Silva", Predicate = "moved to", Object = "finance", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UnixEpoch },
            ]));
        _facts.FindSupersededCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        var extraction = new ExtractionStageResult
        {
            SourceMessageIds = ["message-1"],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
            FilteredFacts =
            [
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics", Confidence = 0.8, Replaces = "finance" },
                new ExtractedFact { Subject = "Tomás Silva", Predicate = "moved to", Object = "analytics team", Confidence = 0.9 },
            ],
        };

        await Sut(dedup: true, supersede: true).PersistAsync(extraction, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await _facts.Received(1).SupersedeAsync("finance", Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>36.6 (D-6, review): the speaker is stored as an entity only until their name is known.</summary>
    [Fact]
    public async Task The_speaker_is_not_stored_as_an_entity_once_their_name_is_known()
    {
        var entities = Substitute.For<IEntityRepository>();
        var written = new List<string>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => { written.Add(ci.Arg<Entity>().Name); return Task.FromResult(ci.Arg<Entity>()); });
        _facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var stage = new PersistenceStage(Substitute.For<IEmbeddingOrchestrator>(), entities, _facts, Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(), clock, ids, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { EnableBatchMemoryUpserts = false }));
        Entity E(string name) => new() { EntityId = name, Name = name, Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };
        ExtractionStageResult With(params ExtractedFact[] facts) => new()
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["user"] = E("user"), ["Carmen"] = E("Carmen") },
            FilteredFacts = facts,
        };

        await stage.PersistAsync(With(
            new ExtractedFact { Subject = "user", Predicate = "is named", Object = "Rosa", Confidence = 1 },
            new ExtractedFact { Subject = "user", Predicate = "works as", Object = "architect", Confidence = 1 }), ownerId: "owner-1");
        written.Should().BeEquivalentTo(["Rosa", "Carmen"], "the speaker is written as the named person, never as \"user\"");

        written.Clear();
        var alreadyThere = With(
            new ExtractedFact { Subject = "user", Predicate = "is named", Object = "Rosa", Confidence = 1 },
            new ExtractedFact { Subject = "user", Predicate = "works as", Object = "architect", Confidence = 1 }) with
        {
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase)
            {
                ["user"] = E("user"), ["Rosa"] = E("Rosa"), ["Carmen"] = E("Carmen"),
            },
        };
        await stage.PersistAsync(alreadyThere, ownerId: "owner-1");
        written.Should().BeEquivalentTo(["Rosa", "Carmen"], "the named person is an entity already: the speaker is not written twice");

        written.Clear();
        await stage.PersistAsync(With(new ExtractedFact { Subject = "Carmen", Predicate = "teaches", Object = "maths", Confidence = 1 }), ownerId: "owner-1");
        written.Should().BeEquivalentTo(["user", "Carmen"], "no name known: stored as before, so nothing hanging from it is lost");
    }
}
