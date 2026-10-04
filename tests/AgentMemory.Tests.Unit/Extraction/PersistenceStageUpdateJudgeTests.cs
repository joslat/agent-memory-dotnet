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
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// 41.06: the update judge on the write path. A change said in other words ("I'm not doing the 10k anymore") closes the
/// stored memory it replaces when a registered judge says so at the threshold; below it, when the judge fails, or with no
/// judge, the write path is what it was.
/// </summary>
public sealed class PersistenceStageUpdateJudgeTests
{
    private readonly IEmbeddingOrchestrator _orchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IEntityRepository _entityRepo = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _factRepo = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _prefRepo = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();
    private readonly IMemoryUpdateJudge _judge = Substitute.For<IMemoryUpdateJudge>();

    private static readonly Fact Stored = new()
    {
        FactId = "fact-old", Subject = "Marta", Predicate = "is running", Object = "the Lyon 10k", Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    public PersistenceStageUpdateJudgeTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 10, 3, 11, 0, 0, TimeSpan.Zero));
        _orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);
        _factRepo.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Fact>()));
        _prefRepo.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Preference>()));
        _factRepo.SearchByVectorAsync(Arg.Any<float[]>(), ValidTimeMode.Current, Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Fact Fact, double Score)> { (Stored, 0.81) });
        _factRepo.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);
        _idGen.GenerateId().Returns("fact-new");
        _judge.IsEnabled.Returns(true);
    }

    private PersistenceStage CreateSut(bool withJudge = true) =>
        new(_orchestrator, _entityRepo, _factRepo, _prefRepo, _relRepo, _clock, _idGen, NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(), Options.Create(new ExtractionOptions()), updateJudge: withJudge ? _judge : null);

    private static ExtractionStageResult Dropped10k() => new()
    {
        SourceMessageIds = ["msg-1"],
        SourceText = "I'm not doing the 10k anymore, the knee won't cope",
        FilteredFacts = [new ExtractedFact { Subject = "Marta", Predicate = "is not doing", Object = "the 10k", Confidence = 0.9 }],
    };

    private void JudgeSays(double p) =>
        _judge.JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyDictionary<string, double>)ci.Arg<MemoryUpdateRequest>().Pairs.ToDictionary(x => x.Key, _ => p));

    [Fact]
    public async Task A_change_said_in_other_words_closes_what_it_replaces_when_the_judge_is_sure()
    {
        JudgeSays(0.93);

        await CreateSut().PersistAsync(Dropped10k());

        await _factRepo.Received(1).SupersedeAsync("fact-old", "fact-new", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _judge.Received(1).JudgeAsync(Arg.Is<MemoryUpdateRequest>(r =>
            r.Said == "I'm not doing the 10k anymore, the knee won't cope"
            && r.Pairs.Single().NewMemory == "Marta | is not doing | the 10k"
            && r.Pairs.Single().StoredMemory == "Marta | is running | the Lyon 10k"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0.64)]
    [InlineData(0.2)]
    public async Task Below_the_threshold_nothing_is_closed(double p)
    {
        JudgeSays(p);

        await CreateSut().PersistAsync(Dropped10k());

        await _factRepo.DidNotReceive().SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_judge_closes_nothing_and_the_write_stands()
    {
        _judge.JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("down"));

        var result = await CreateSut().PersistAsync(Dropped10k());

        result.FactCount.Should().Be(1);
        await _factRepo.DidNotReceive().SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_judge_or_with_it_off_the_write_path_is_today_s()
    {
        await CreateSut(withJudge: false).PersistAsync(Dropped10k());
        _judge.IsEnabled.Returns(false);
        await CreateSut().PersistAsync(Dropped10k());

        await _judge.DidNotReceive().JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>());
        await _factRepo.DidNotReceive().SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_preference_closes_the_preference_it_replaces()
    {
        JudgeSays(0.9);
        _idGen.GenerateId().Returns("pref-new");
        _prefRepo.SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Preference Preference, double Score)>
            {
                (new Preference { PreferenceId = "pref-old", Category = "style", PreferenceText = "Wants short answers in plain sentences, no bullet points", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UnixEpoch }, 0.84),
            });
        _prefRepo.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut().PersistAsync(new ExtractionStageResult
        {
            SourceMessageIds = ["msg-1"],
            SourceText = "From now on bullet points are fine, actually.",
            FilteredPreferences = [new ExtractedPreference { Category = "style", PreferenceText = "Bullet points are fine in answers", Confidence = 0.9 }],
        });

        await _prefRepo.Received(1).SupersedeAsync("pref-old", "pref-new", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }
}
