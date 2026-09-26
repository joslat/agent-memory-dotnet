using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Resolution;
using AgentMemory.Extraction.Llm;
using AgentMemory.Extraction.Llm.Internal;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Findings of the review round on the extraction-reliability branch, each pinned by a test that
/// failed before its fix.
/// </summary>
public sealed class Sprint1ReviewRegressionTests
{
    // ---- M2: the date converter must never throw (non-ASCII digits) ----

    [Theory]
    [InlineData("２０２６-08")]   // fullwidth
    [InlineData("٢٠٢٦")]         // Arabic-Indic
    public void Non_ascii_digits_drop_the_date_instead_of_throwing(string raw)
    {
        var act = () => PeriodDateConverter.Parse(raw, PeriodEdge.Start);

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void A_reply_with_a_non_ascii_date_keeps_its_items()
    {
        const string reply = """{"facts":[{"subject":"A","predicate":"p","object":"B","valid_from":"２０２６-08","confidence":0.9}]}""";

        var parsed = LlmExtractionRunner.Parse(reply);

        parsed.IsUsable.Should().BeTrue();
        parsed.Response!.Facts.Should().ContainSingle().Which.ValidFrom.Should().BeNull();
    }

    // ---- M3: free text never borrows today's date or year ----

    [Theory]
    [InlineData("17:00", true)]
    [InlineData("10:30 PM", true)]
    [InlineData("August 15", false)]
    [InlineData("March 3", false)]
    public void Free_text_without_an_explicit_year_is_dropped(string raw, bool end)
    {
        PeriodDateConverter.Parse(raw, end ? PeriodEdge.End : PeriodEdge.Start).Should().BeNull();
    }

    [Fact]
    public void Free_text_with_a_year_still_reads()
    {
        PeriodDateConverter.Parse("15 August 2026 18:00", PeriodEdge.End)
            .Should().Be(new DateTimeOffset(2026, 8, 15, 18, 0, 0, TimeSpan.Zero));
    }

    // ---- L1: truncation with no usage and no cap sets no limit ----

    [Fact]
    public async Task A_truncation_without_usage_or_cap_does_not_invent_a_smaller_limit()
    {
        var requests = new List<ChatOptions?>();
        var chat = Substitute.For<IChatClient>();
        var replies = new Queue<ChatResponse>(
        [
            new(new ChatMessage(ChatRole.Assistant, """{"entities":[""")) { FinishReason = ChatFinishReason.Length },
            new(new ChatMessage(ChatRole.Assistant, """{"entities":[]}""")) { FinishReason = ChatFinishReason.Stop },
        ]);
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => { requests.Add(ci.Arg<ChatOptions>()?.Clone()); return Task.FromResult(replies.Dequeue()); });
        var sut = new LlmUnifiedMemoryExtractor(chat,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        await sut.ExtractAsync([new Message
        {
            MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user", Content = "x", TimestampUtc = DateTimeOffset.UtcNow,
        }]);

        requests.Should().HaveCount(2);
        requests[1]!.MaxOutputTokens.Should().BeNull();
    }

    // ---- L2: a content-filter stop is recognised as a content rejection ----

    [Fact]
    public async Task A_content_filter_stop_is_a_content_rejection_for_the_batch_splitter()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.ContentFilter });
        var sut = new LlmUnifiedMemoryExtractor(chat,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        var thrown = await FluentActions.Awaiting(() => sut.ExtractAsync([new Message
        {
            MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user", Content = "x", TimestampUtc = DateTimeOffset.UtcNow,
        }])).Should().ThrowAsync<FormatException>();

        LlmMultiSessionUnifiedMemoryExtractor.IsContentRejection(thrown.Which).Should().BeTrue();
    }

    // ---- Round 2: null lists, zero usage, ambiguous numeric dates ----

    [Fact]
    public async Task A_clean_reply_with_a_null_list_is_used_even_while_traced()
    {
        // KeptItems read Response.Entities.Count; "entities": null threw, but only when a listener made
        // the attempt span non-null (any OpenTelemetry host).
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == AgentMemory.Abstractions.Diagnostics.AgentMemoryDiagnostics.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"entities":null,"facts":[{"subject":"A","predicate":"p","object":"B","confidence":0.9}],"relations":null}""")));
        var sut = new LlmUnifiedMemoryExtractor(chat,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        var result = await sut.ExtractAsync([new Message
        {
            MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user", Content = "x", TimestampUtc = DateTimeOffset.UtcNow,
        }]);

        result.Facts.Should().ContainSingle();
        LlmExtractionRunner.Parse("""{"entities":null,"facts":[]}""").KeptItems.Should().Be(0);
    }

    [Fact]
    public async Task A_truncation_reporting_zero_output_tokens_sets_no_limit()
    {
        var requests = new List<ChatOptions?>();
        var chat = Substitute.For<IChatClient>();
        var replies = new Queue<ChatResponse>(
        [
            new(new ChatMessage(ChatRole.Assistant, """{"entities":[""")) { FinishReason = ChatFinishReason.Length, Usage = new UsageDetails { OutputTokenCount = 0 } },
            new(new ChatMessage(ChatRole.Assistant, """{"entities":[]}""")) { FinishReason = ChatFinishReason.Stop },
        ]);
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => { requests.Add(ci.Arg<ChatOptions>()?.Clone()); return Task.FromResult(replies.Dequeue()); });
        var sut = new LlmUnifiedMemoryExtractor(chat,
            Options.Create(new LlmExtractionOptions { UseUnifiedExtraction = true }),
            NullLogger<LlmUnifiedMemoryExtractor>.Instance);

        await sut.ExtractAsync([new Message
        {
            MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user", Content = "x", TimestampUtc = DateTimeOffset.UtcNow,
        }]);

        requests[1]!.MaxOutputTokens.Should().BeNull();
    }

    [Theory]
    [InlineData("03/04/2026")]
    [InlineData("3.4.2026")]
    public void Day_and_month_written_as_numbers_are_not_guessed(string raw)
    {
        PeriodDateConverter.Parse(raw, PeriodEdge.Start).Should().BeNull();
    }

    // ---- L3: an unreadable control field is repaired, not half-accepted ----

    [Fact]
    public void An_unreadable_processed_source_sessions_makes_the_reply_unusable()
    {
        var parsed = LlmExtractionRunner.Parse(
            """{"entities":[{"name":"A","type":"PERSON","confidence":0.9}],"processed_source_sessions":"s1"}""");

        parsed.Status.Should().Be(LlmExtractionRunner.ParseStatus.Unusable);
        parsed.Error.Should().Contain("processed_source_sessions");
    }
}

/// <summary>M1 and M4: reusing a vector must not change who matches whom within one turn.</summary>
public sealed class ResolutionReviewRegressionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly List<Entity> _upserted = [];

    public ResolutionReviewRegressionTests()
    {
        _entities.GetByTypeAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _upserted.Add(ci.Arg<Entity>()); return Task.FromResult(ci.Arg<Entity>()); });
        // Every name embeds to the SAME vector: any semantic comparison would match (cosine 1).
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[] { 1f, 0f }));
    }

    private CompositeEntityResolver Sut(Action<ExtractionOptions>? configure = null)
    {
        var options = new ExtractionOptions();
        configure?.Invoke(options);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        return new CompositeEntityResolver(_entities, _embeddings, Options.Create(options), clock, ids,
            NullLogger<CompositeEntityResolver>.Instance);
    }

    [Fact]
    public async Task A_new_entity_does_not_start_absorbing_later_mentions_in_the_same_turn()
    {
        // Before vector reuse, a new entity entered the turn's snapshot WITHOUT a vector, so the semantic
        // matcher could not match later mentions against it. That must still hold.
        var sut = Sut();
        using var batch = sut.BeginBatch();
        await sut.PrepareCandidatesAsync(["LOCATION"]);

        var first = await sut.ResolveForPersistenceAsync(new ExtractedEntity { Name = "NYC", Type = "LOCATION" }, []);
        var second = await sut.ResolveForPersistenceAsync(new ExtractedEntity { Name = "New York City", Type = "LOCATION" }, []);

        second.EntityId.Should().NotBe(first.EntityId);
        first.Embedding.Should().NotBeNull("the vector is still handed to persistence");
    }

    [Fact]
    public async Task The_direct_path_stores_a_new_entity_without_the_vector_resolution_computed()
    {
        var entity = await Sut().ResolveEntityAsync(new ExtractedEntity { Name = "Contoso", Type = "ORGANIZATION" }, []);

        _upserted.Should().ContainSingle().Which.Embedding.Should().BeNull();
        entity.Embedding.Should().Equal(1f, 0f);
    }

    [Fact]
    public async Task The_name_probe_reads_only_the_same_type_snapshot()
    {
        var sut = Sut(o => o.EntityResolution.TypeStrictFiltering = false);
        using var batch = sut.BeginBatch();
        await sut.PrepareCandidatesAsync(["PERSON"]);

        await sut.PrepareNameEmbeddingsAsync(
        [
            new ExtractedEntity { Name = "Ana", Type = "PERSON" },
            new ExtractedEntity { Name = "Bea", Type = "PERSON" },
        ]);

        await _entities.DidNotReceiveWithAnyArgs().GetByNameAsync(default!, default, default, default);
    }

    [Fact]
    public async Task A_failing_name_probe_never_fails_the_stage()
    {
        _embeddings.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("transient"));
        var sut = Sut();
        using var batch = sut.BeginBatch();
        await sut.PrepareCandidatesAsync(["PERSON"]);

        var act = () => sut.PrepareNameEmbeddingsAsync(
        [
            new ExtractedEntity { Name = "Ana", Type = "PERSON" },
            new ExtractedEntity { Name = "Bea", Type = "PERSON" },
        ]);

        await act.Should().NotThrowAsync();
    }

    // ---- L5 / L6: configurations that would make partial matching worse than off ----

    [Fact]
    public void Partial_confidence_below_SameAs_fails_validation_when_enabled()
    {
        var act = () => Validate(o =>
        {
            o.Extraction.EntityResolution.EnablePartialNameMatch = true;
            o.Extraction.SameAsThreshold = 0.94;
            o.Extraction.EntityResolution.PartialNameMatchConfidence = 0.9;
        });

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Null_partial_types_fail_validation()
    {
        var act = () => Validate(o => o.Extraction.EntityResolution.PartialNameMatchTypes = null!);

        act.Should().Throw<OptionsValidationException>();
    }

    private static void Validate(Action<MemoryOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(configure);
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;
    }
}
