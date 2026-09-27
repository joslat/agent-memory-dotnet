using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 36.3: shared knowledge (a taught book, a catalogue) is recalled under its own budget, labelled as shared,
/// and cannot absorb a person's entity by a near match. Measured before the fix: the book took 7–8 of 10
/// fact slots on questions about the person, rendered its characters' tastes as "User preferences", and a
/// person's "Bill Evans" was merged into the book's "Bill".
/// </summary>
public sealed class SharedKnowledgeBoundaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static Fact F(string id, string? owner) => new()
    {
        FactId = id, Subject = id, Predicate = "is", Object = "x", Confidence = 1, CreatedAtUtc = T0, OwnerId = owner,
    };

    private static Entity E(string id, string name, string? owner) => new()
    {
        EntityId = id, Name = name, Type = "PERSON", Confidence = 1, CreatedAtUtc = T0, OwnerId = owner,
    };

    private static Preference P(string id, string? owner) => new()
    {
        PreferenceId = id, Category = "c", PreferenceText = id, Confidence = 1, CreatedAtUtc = T0, OwnerId = owner,
    };

    // ── The service: two searches, own first, only for an owner reading shared ──────────────────────

    private static (LongTermMemoryService Service, IFactRepository Facts, IEntityRepository Entities, IPreferenceRepository Preferences)
        Service(int? sharedBudget)
    {
        var facts = Substitute.For<IFactRepository>();
        facts.SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<(Fact, double)>>(
                ci.Arg<MemoryScope?>() is { OwnerId: SharedScopes.SentinelOwner }
                    ? [(F("shared", null), 0.99)]
                    : [(F("own", "u1"), 0.6)]));
        facts.SearchByVectorAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Fact, double)>>([]));
        var entities = Substitute.For<IEntityRepository>();
        entities.SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Entity, double)>>([]));
        var preferences = Substitute.For<IPreferenceRepository>();
        preferences.SearchByVectorAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Preference, double)>>([]));

        var memoryOptions = new MemoryOptions { SharedRecallBudget = sharedBudget };
        var service = new LongTermMemoryService(
            entities, facts, preferences, Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(memoryOptions.Isolation), NullLogger<DefaultMemoryIsolationPolicy>.Instance),
            memoryOptions: Options.Create(memoryOptions));
        return (service, facts, entities, preferences);
    }

    [Fact]
    public async Task With_a_shared_budget_an_owner_gets_its_own_top_k_and_the_shared_top_n_own_first()
    {
        var (service, facts, _, _) = Service(sharedBudget: 3);

        var found = await service.SearchFactsAsync(new float[4], 10, 0.7, MemoryScope.For("u1"));

        found.Select(f => f.FactId).Should().Equal(["own", "shared"], "a higher-scoring shared fact still comes after the person's own");
        await facts.Received(1).SearchByVectorAsync(Arg.Any<float[]>(), 10, 0.7,
            Arg.Is<MemoryScope?>(s => s!.OwnerId == "u1" && !s.IncludeShared), Arg.Any<CancellationToken>());
        await facts.Received(1).SearchByVectorAsync(Arg.Any<float[]>(), 3, 0.7,
            Arg.Is<MemoryScope?>(s => s!.OwnerId == SharedScopes.SentinelOwner), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_shared_budget_the_search_is_the_single_one_it_always_was()
    {
        var (service, facts, _, _) = Service(sharedBudget: null);

        await service.SearchFactsAsync(new float[4], 10, 0.7, MemoryScope.For("u1"));

        await facts.Received(1).SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await facts.Received(1).SearchByVectorAsync(Arg.Any<float[]>(), 10, 0.7,
            Arg.Is<MemoryScope?>(s => s!.OwnerId == "u1" && s.IncludeShared), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_read_without_an_owner_has_nothing_to_separate()
    {
        var (service, facts, _, _) = Service(sharedBudget: 3);

        await service.SearchFactsAsync(new float[4], 10, 0.7, MemoryScope.Global);

        await facts.Received(1).SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_zero_budget_recalls_no_shared_items_and_makes_no_shared_search()
    {
        var (service, facts, _, _) = Service(sharedBudget: 0);

        var found = await service.SearchFactsAsync(new float[4], 10, 0.7, MemoryScope.For("u1"));

        found.Select(f => f.FactId).Should().Equal("own");
        await facts.DidNotReceive().SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Is<MemoryScope?>(s => s!.OwnerId == SharedScopes.SentinelOwner), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Entities_preferences_and_the_point_in_time_searches_are_split_too()
    {
        // The twins: every vector search in the service, or a path keeps the single budget by being missed.
        var (service, facts, entities, preferences) = Service(sharedBudget: 2);
        var owner = MemoryScope.For("u1");

        await service.SearchEntitiesAsync(new float[4], 5, 0.7, owner);
        await service.SearchPreferencesAsOfAsync(new float[4], T0, 5, 0.7, owner);
        await service.SearchFactsAsOfAsync(new float[4], T0, 5, 0.7, owner);

        await entities.Received(1).SearchByVectorAsync(Arg.Any<float[]>(), 2, Arg.Any<double>(),
            Arg.Is<MemoryScope?>(s => s!.OwnerId == SharedScopes.SentinelOwner), Arg.Any<CancellationToken>());
        await preferences.Received(1).SearchByVectorAsOfAsync(Arg.Any<float[]>(), T0, 2, Arg.Any<double>(),
            Arg.Is<MemoryScope?>(s => s!.OwnerId == SharedScopes.SentinelOwner), Arg.Any<CancellationToken>());
        await facts.Received(1).SearchByVectorAsOfAsync(Arg.Any<float[]>(), T0, 2, Arg.Any<double>(),
            Arg.Is<MemoryScope?>(s => s!.OwnerId == SharedScopes.SentinelOwner), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    // ── The context says so, on both recall paths ─────────────────────────────────────────────────

    private static MemoryContextAssembler Assembler(int? sharedBudget)
    {
        var longTerm = Substitute.For<ILongTermMemoryService>();
        var shortTerm = Substitute.For<IShortTermMemoryService>();
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        var options = new MemoryOptions { SharedRecallBudget = sharedBudget };
        return new MemoryContextAssembler(
            shortTerm, longTerm, Substitute.For<IReasoningMemoryService>(), graphRag: null, embeddings,
            Substitute.For<IClock>(), Options.Create(options), NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(options.Isolation), NullLogger<DefaultMemoryIsolationPolicy>.Instance));
    }

    private static RecallRequest Request(MemoryScope? scope) => new()
    {
        SessionId = "s1", Query = "what do I do", QueryEmbedding = new float[4],
        Options = new RecallOptions { Scope = scope },
    };

    [Theory]
    [InlineData(3, "u1", true)]
    [InlineData(null, "u1", false)]
    [InlineData(3, null, false)]
    public async Task The_context_is_marked_exactly_when_the_searches_were_split(int? budget, string? owner, bool expected)
    {
        var scope = owner is null ? null : MemoryScope.For(owner);

        var live = await Assembler(budget).AssembleContextAsync(Request(scope));
        var asOf = await Assembler(budget).AssembleContextAsOfAsync(Request(scope), T0);

        live.SeparatesSharedKnowledge.Should().Be(expected);
        asOf.SeparatesSharedKnowledge.Should().Be(expected, "the point-in-time path is the live path's twin");
    }

    // ── The renderers label shared items, and only when told ──────────────────────────────────────

    private static MemoryContext Context(bool separated) => new()
    {
        SessionId = "s1",
        AssembledAtUtc = T0,
        SeparatesSharedKnowledge = separated,
        RelevantFacts = new MemoryContextSection<Fact> { Items = [F("Rosa works as architect", "u1"), F("Alice followed the White Rabbit", null)] },
        RelevantPreferences = new MemoryContextSection<Preference> { Items = [P("Rosa likes jazz", "u1"), P("Alice does not like raw eggs", null)] },
    };

    [Fact]
    public void The_agent_framework_renderer_never_shows_shared_items_as_the_persons()
    {
        var text = string.Join("\n", MafTypeMapper.ToContextMessages(Context(separated: true), new ContextFormatOptions()).Select(m => m.Text));

        var known = text.Split('\n').Single(line => line.Contains("Known facts", StringComparison.Ordinal));
        known.Should().Contain("Rosa works as architect").And.NotContain("White Rabbit");
        text.Split('\n').Single(line => line.Contains("User preferences", StringComparison.Ordinal))
            .Should().NotContain("raw eggs");
        text.Should().Contain($"Facts ({SharedKnowledge.Label})").And.Contain("White Rabbit")
            .And.Contain($"Preferences ({SharedKnowledge.Label})").And.Contain("raw eggs");
    }

    [Fact]
    public void The_core_formatter_never_shows_shared_items_as_the_persons()
    {
        var text = MemoryContextFormatter.FormatRecallResult(new RecallResult { Context = Context(separated: true), TotalItemsRetrieved = 4 });

        var known = text[text.IndexOf("### Known Facts", StringComparison.Ordinal)..text.IndexOf("### User Preferences", StringComparison.Ordinal)];
        known.Should().Contain("Rosa works as architect").And.NotContain("White Rabbit");
        text.Should().Contain($"### Facts ({SharedKnowledge.Label})").And.Contain($"### Preferences ({SharedKnowledge.Label})");
        text.IndexOf("raw eggs", StringComparison.Ordinal).Should().BeGreaterThan(
            text.IndexOf($"### Preferences ({SharedKnowledge.Label})", StringComparison.Ordinal));
    }

    [Fact]
    public void Unmarked_both_renderers_are_unchanged()
    {
        var maf = string.Join("\n", MafTypeMapper.ToContextMessages(Context(separated: false), new ContextFormatOptions()).Select(m => m.Text));
        var core = MemoryContextFormatter.FormatRecallResult(new RecallResult { Context = Context(separated: false), TotalItemsRetrieved = 4 });

        maf.Should().NotContain(SharedKnowledge.Label);
        maf.Split('\n').Single(line => line.Contains("Known facts", StringComparison.Ordinal)).Should().Contain("White Rabbit");
        core.Should().NotContain(SharedKnowledge.Label);
    }

    // ── A sub-query cannot hand the shared corpus the slots back ──────────────────────────────────

    [Fact]
    public void The_fan_out_merge_keeps_the_two_budgets()
    {
        IReadOnlyList<(Fact Item, double Score)> monolithic = [(F("own1", "u1"), 0.6), (F("own2", "u1"), 0.55), (F("sh1", null), 0.9)];
        IReadOnlyList<(Fact Item, double Score)> leg = [(F("sh2", null), 0.95), (F("sh3", null), 0.94)];

        var merged = RecallFanOutMerge.MergeScored(monolithic, leg, f => f.FactId, limit: 2, f => f.OwnerId, sharedLimit: 1);

        merged.Merged.Select(r => r.Item.FactId).Should().Equal("own1", "own2", "sh2");
    }

    // ── Resolution: exact only across the boundary ────────────────────────────────────────────────

    private static CompositeEntityResolver Resolver(IEntityRepository entities)
    {
        var options = new ExtractionOptions();
        options.EntityResolution.EnablePartialNameMatch = true;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new[] { 1f, 0f }));
        return new CompositeEntityResolver(entities, embeddings, Options.Create(options), clock, ids,
            NullLogger<CompositeEntityResolver>.Instance);
    }

    private static IEntityRepository Candidates(params Entity[] candidates)
    {
        var entities = Substitute.For<IEntityRepository>();
        entities.GetByTypeAsync("PERSON", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(candidates));
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(ci.Arg<Entity>()));
        return entities;
    }

    [Fact]
    public async Task A_persons_mention_does_not_join_a_shared_entity_by_a_partial_name()
    {
        var bookBill = E("book-bill", "Bill", owner: null);

        var resolved = await Resolver(Candidates(bookBill)).ResolveEntityAsync(
            new ExtractedEntity { Name = "Bill Evans", Type = "PERSON" }, ["m1"], MemoryScope.For("u1"));

        resolved.EntityId.Should().NotBe("book-bill");
    }

    [Fact]
    public async Task The_same_partial_name_still_joins_the_persons_own_entity()
    {
        var ownBill = E("own-bill", "Bill", owner: "u1");

        var resolved = await Resolver(Candidates(ownBill)).ResolveEntityAsync(
            new ExtractedEntity { Name = "Bill Evans", Type = "PERSON" }, ["m1"], MemoryScope.For("u1"));

        resolved.EntityId.Should().Be("own-bill");
    }

    [Fact]
    public async Task An_exact_name_still_joins_the_shared_entity()
    {
        var rabbit = E("book-rabbit", "White Rabbit", owner: null);

        var resolved = await Resolver(Candidates(rabbit)).ResolveEntityAsync(
            new ExtractedEntity { Name = "white rabbit", Type = "PERSON" }, ["m1"], MemoryScope.For("u1"));

        resolved.EntityId.Should().Be("book-rabbit");
    }

    // ── Review round 1 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Off_the_core_formatter_adds_no_shared_section_even_with_a_projection_preamble()
    {
        var projection = new AgentMemory.Abstractions.Domain.ProjectedContext
        {
            Blocks = [new AgentMemory.Abstractions.Domain.ProjectedBlock(
                AgentMemory.Abstractions.Domain.ProjectedBlockKind.NoDirectMatch, "facts", "No stored item directly matches.")],
        };
        var context = Context(separated: false) with { Projection = projection };

        var text = MemoryContextFormatter.FormatRecallResult(new RecallResult { Context = context, TotalItemsRetrieved = 4 });

        text.Should().NotContain(SharedKnowledge.Label);
        System.Text.RegularExpressions.Regex.Matches(text, "No stored item directly matches").Count.Should().Be(1);
    }

    [Fact]
    public async Task The_batch_path_drops_shared_preferences_too()
    {
        var staged = new AgentMemory.Core.Extraction.ExtractionStageResult
        {
            FilteredPreferences = [new ExtractedPreference { Category = "food", PreferenceText = "Alice does not like raw eggs" }],
        };

        MemoryExtractionPipeline.WithoutSharedPreferences(new ExtractionRequest { SessionId = "s", Messages = [], ShareWithEveryone = true }, staged)
            .FilteredPreferences.Should().BeEmpty();
        MemoryExtractionPipeline.WithoutSharedPreferences(new ExtractionRequest { SessionId = "s", Messages = [] }, staged)
            .FilteredPreferences.Should().ContainSingle();
        var batch = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "AgentMemory.Core", "Services", "MemoryExtractionPipeline.Batch.cs"));
        batch.Should().Contain("WithoutSharedPreferences(", "both extraction paths drop them");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentMemory.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public async Task With_a_shared_budget_expansion_reads_the_owners_own_rows_only()
    {
        var (service, facts, _, _) = Service(sharedBudget: 3);
        facts.SearchByCanonicalPredicatesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<MemoryScope>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<bool>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));

        await service.SearchFactsAsync(new float[4], 10, 0.7, MemoryScope.For("u1"), expandByPredicate: true, expansionLimit: 60,
            questionRelations: ["lives in"], CancellationToken.None);

        await facts.Received(1).SearchByCanonicalPredicatesAsync(Arg.Any<IReadOnlyList<string>>(), 60,
            Arg.Is<MemoryScope>(s => s.OwnerId == "u1" && !s.IncludeShared), Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task An_empty_owner_is_no_owner_and_splits_nothing()
    {
        var (service, facts, _, _) = Service(sharedBudget: 3);

        await service.SearchFactsAsync(new float[4], 10, 0.7, new MemoryScope { OwnerId = "", IncludeShared = true });

        await facts.Received(1).SearchByVectorAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_shared_item_keeps_its_own_annotation_in_the_core_formatter()
    {
        var projection = new AgentMemory.Abstractions.Domain.ProjectedContext
        {
            Annotations = new Dictionary<string, AgentMemory.Abstractions.Domain.ProjectedItemAnnotation>
            {
                ["Alice followed the White Rabbit"] = new() { IsNearMiss = true, Score = 0.42 },
            },
        };

        var text = MemoryContextFormatter.FormatRecallResult(new RecallResult { Context = Context(separated: true) with { Projection = projection }, TotalItemsRetrieved = 4 });

        text.Should().Contain("[closest match, 0.42] Alice followed the White Rabbit", "a shared near-miss must not read as a confident match");
    }
}
