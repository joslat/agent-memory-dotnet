using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// IBP-1b (<see cref="RecallOptions.FillFoldedEntityPlaces"/>; the owner, 22 Nov 2026: behind a switch, measured, then on for
/// everyone if answers do not get worse). Since IBP-1 the prompt shows each entity line once, so one thing stored as two
/// entities takes one line and its second place stays empty. With the switch on, that place goes to the next entity.
/// </summary>
public sealed class FilledEntityPlacesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly TimeProvider _clock = Substitute.For<TimeProvider>();
    private readonly List<int> _asked = [];

    private static readonly IMemoryIsolationPolicy Policy =
        new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private static Entity E(string id, string name, string type, string? description = null) => new()
    {
        EntityId = id, Name = name, Type = type, Description = description, Confidence = 0.9, CreatedAtUtc = Now,
    };

    // In relevance order: the dog Luna stored twice alike, then three different things.
    private static readonly Entity[] Pool =
    [
        E("luna-1", "Luna", "ANIMAL", "the owner's dog"),
        E("luna-2", "Luna", "ANIMAL", "the owner's dog"),
        E("bruno", "Bruno", "PERSON"),
        E("tortoni", "Café Tortoni", "PLACE"),
        E("atlas", "Atlas", "PROJECT"),
    ];

    public FilledEntityPlacesTests()
    {
        _clock.GetUtcNow().Returns(Now);
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new float[8]));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var limit = call.ArgAt<int>(1);
                _asked.Add(limit);
                return Task.FromResult<IReadOnlyList<Entity>>(Pool.Take(limit).ToList());
            });
        _longTerm.SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([]));
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<ValidTimeMode>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
        _reasoning.SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));
    }

    private Task<MemoryContext> AskAsync(RecallOptions options) =>
        new MemoryContextAssembler(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
                Options.Create(new MemoryOptions()), NullLogger<MemoryContextAssembler>.Instance, Policy,
                rankingContext: null, truncationStrategies: null, rerankers: null, projectionFeatures: null)
            .AssembleContextAsync(new RecallRequest
            {
                SessionId = "s1", Query = "how is Luna?", Question = "how is Luna?", QueryEmbedding = new float[8], Options = options,
            }, CancellationToken.None);

    [Fact]
    public async Task Off_by_default_recall_returns_what_it_returns_today()
    {
        var context = await AskAsync(RecallOptions.Default with { MaxEntities = 3 });

        _asked.Should().Equal(3);
        context.RelevantEntities.Items.Select(e => e.EntityId).Should().Equal("luna-1", "luna-2", "bruno");
        MemoryContextFormatter.OncePerLine(context.RelevantEntities.Items).Should().HaveCount(2, "the prompt folds the two Lunas into one line");
    }

    [Fact]
    public async Task On_the_place_the_fold_frees_goes_to_the_next_entity()
    {
        var context = await AskAsync(RecallOptions.Default with { MaxEntities = 3, FillFoldedEntityPlaces = true });

        _asked.Should().Equal(6);
        context.RelevantEntities.Items.Select(e => e.EntityId).Should().Equal("luna-1", "bruno", "tortoni");
    }

    [Fact]
    public void The_search_asks_for_twice_the_limit_and_at_most_16_more()
    {
        MemoryContextAssembler.EntitySearchLimit(RecallOptions.Default with { MaxEntities = 5 }).Should().Be(5);
        MemoryContextAssembler.EntitySearchLimit(RecallOptions.Default with { MaxEntities = 5, FillFoldedEntityPlaces = true }).Should().Be(10);
        MemoryContextAssembler.EntitySearchLimit(RecallOptions.Default with { MaxEntities = 40, FillFoldedEntityPlaces = true }).Should().Be(56);
        MemoryContextAssembler.EntitySearchLimit(RecallOptions.Default with { MaxEntities = 0, FillFoldedEntityPlaces = true }).Should().Be(0);
    }

    [Fact]
    public void Namesakes_that_differ_keep_their_own_places_and_scores_follow_the_kept_entities()
    {
        Entity[] found = [E("luna-dog", "Luna", "ANIMAL", "the owner's dog"), E("luna-cat", "Luna", "ANIMAL", "the sister's cat"),
            E("luna-dup", "Luna", "ANIMAL", "the owner's dog"), E("bruno", "Bruno", "PERSON")];
        var scores = found.Select((e, i) => (e, 0.9 - i * 0.1)).ToArray();

        var (kept, keptScores) = MemoryContextAssembler.FillFoldedEntityPlaces(
            RecallOptions.Default with { MaxEntities = 3, FillFoldedEntityPlaces = true }, found, scores);

        kept.Select(e => e.EntityId).Should().Equal("luna-dog", "luna-cat", "bruno");
        keptScores.Select(s => s.Entity.EntityId).Should().Equal("luna-dog", "luna-cat", "bruno");
    }
}
