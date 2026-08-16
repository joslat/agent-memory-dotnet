using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 steps 9-10. Source quotes and date grounding, and the single fetch they share.
/// </summary>
public sealed class QuoteAndDateProjectionTests
{
    private static readonly DateTimeOffset Stamp = new(2023, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static Fact MakeFact(
        string id, string @object = "Acme", params string[] sourceIds) => new()
    {
        FactId = id, Subject = "Bob", Predicate = "works_at", Object = @object,
        Confidence = 0.9, CreatedAtUtc = Stamp, SourceMessageIds = sourceIds,
    };

    private static Message MakeMessage(
        string id, string content, DateTimeOffset? timestamp = null,
        IReadOnlyDictionary<string, object>? metadata = null) => new()
    {
        MessageId = id, SessionId = "s1", ConversationId = "c1", Role = "user",
        Content = content, TimestampUtc = timestamp ?? Stamp,
        Metadata = metadata ?? new Dictionary<string, object>(),
    };

    private static ProjectionState State(MemoryProjectionOptions options, params Fact[] facts) => new()
    {
        Options = options,
        Scope = null,
        Entities = [],
        Facts = facts,
        Preferences = [],
        Traces = [],
        RecentMessages = [],
        RelevantMessages = [],
        EntityScores = [],
        FactScores = [],
        PreferenceScores = [],
        TraceScores = [],
    };

    private static IMessageRepository RepoWith(params Message[] messages)
    {
        var repo = Substitute.For<IMessageRepository>();
        repo.GetByIdsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(messages));
        return repo;
    }

    private static MemoryProjectionOptions QuotesOn =>
        MemoryProjectionOptions.Default with { AttachSourceQuotes = true };

    private static MemoryProjectionOptions DatesOn =>
        MemoryProjectionOptions.Default with { GroundDates = true };

    // ── quotes ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheShortestSentenceContainingTheObjectIsAttached()
    {
        var state = State(QuotesOn, MakeFact("f1", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1",
            "I used to work elsewhere. I joined Acme last spring. Acme is a large logistics firm headquartered in Basel."));

        await new SourceQuoteProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SourceQuote.Should().Be("I joined Acme last spring");
    }

    [Fact]
    public async Task ASentenceWithoutTheObjectIsNotChosen()
    {
        var state = State(QuotesOn, MakeFact("f1", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1", "It rained. Acme it is."));

        await new SourceQuoteProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SourceQuote.Should().Be("Acme it is");
    }

    [Fact]
    public async Task TheQuoteIsTruncatedAtTheConfiguredLength()
    {
        var longSentence = "Acme " + new string('x', 400);
        var state = State(QuotesOn with { MaxQuoteLength = 20 }, MakeFact("f1", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1", longSentence));

        await new SourceQuoteProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        var quote = state.Build().Annotations["f1"].SourceQuote!;
        quote.Should().EndWith("…");
        quote.Length.Should().BeLessThanOrEqualTo(21);
    }

    [Fact]
    public async Task NoMoreThanTheConfiguredNumberOfQuotesIsAttached()
    {
        // The token cost of this feature is bounded by construction, not by hope.
        var facts = Enumerable.Range(1, 5)
            .Select(i => MakeFact($"f{i}", "Acme", "m1")).ToArray();
        var state = State(QuotesOn with { MaxQuotesPerRecall = 2 }, facts);
        var repo = RepoWith(MakeMessage("m1", "Bob started at Acme in June."));

        await new SourceQuoteProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations.Values.Count(a => a.SourceQuote is not null).Should().Be(2);
    }

    [Fact]
    public async Task AQuoteAlreadyContainedInTheTripleIsSkipped()
    {
        // Repeating the item's own text spends tokens to say the same thing twice -- exactly the cost
        // this feature is priced against.
        var state = State(QuotesOn, MakeFact("f1", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1", "Acme"));

        await new SourceQuoteProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task AFactWithNoSourceMessagesGetsNoQuote()
    {
        var state = State(QuotesOn, MakeFact("f1", "Acme"));
        var repo = RepoWith();

        await new SourceQuoteProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.IsEmpty.Should().BeTrue();
    }

    // ── dates ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheStorageTimestampGroundsAFactWhenThereIsNoMetadata()
    {
        var state = State(DatesOn, MakeFact("f1", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1", "Bob joined Acme.", new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.Zero)));

        await new DateGroundingProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SourceDate.Should().Be("2024-03-04");
    }

    [Fact]
    public async Task TheSourceTimestampMetadataWinsOverTheStorageTimestamp()
    {
        // A corpus ingested in one afternoon has storage timestamps that say nothing and source
        // timestamps that say everything.
        var state = State(DatesOn, MakeFact("f1", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1", "Bob joined Acme.",
            timestamp: new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
            metadata: new Dictionary<string, object> { ["sourceTimestamp"] = "2021-09-30T00:00:00Z" }));

        await new DateGroundingProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SourceDate.Should().Be("2021-09-30");
    }

    [Fact]
    public async Task AnUnparseableSourceTimestampFallsBackRatherThanThrowing()
    {
        // Adapter-written metadata is a data condition, not a contract -- and throwing on a rendering
        // path would turn a bad string into a failed recall.
        var state = State(DatesOn, MakeFact("f1", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1", "Bob joined Acme.",
            timestamp: new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.Zero),
            metadata: new Dictionary<string, object> { ["sourceTimestamp"] = "not-a-date" }));

        await new DateGroundingProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().Annotations["f1"].SourceDate.Should().Be("2024-03-04");
    }

    [Fact]
    public async Task ChronologicalOrderingSortsTheFactSection()
    {
        var options = MemoryProjectionOptions.Default with { ChronologicalOrdering = true };
        var state = State(options, MakeFact("f1", "Acme", "m1"), MakeFact("f2", "Globex", "m2"));
        var repo = RepoWith(
            MakeMessage("m1", "later", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeMessage("m2", "earlier", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        await new DateGroundingProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().SectionOrder[ProjectionSectionKeys.Facts].Should().Equal("f2", "f1");
    }

    [Fact]
    public async Task ASingleDatedItemImposesNoOrdering()
    {
        // One date is not a chronology, and reordering on it would rearrange the retrieval ranking --
        // a real signal -- to express an order the section does not have.
        var options = MemoryProjectionOptions.Default with { ChronologicalOrdering = true };
        var state = State(options, MakeFact("f1", "Acme", "m1"), MakeFact("f2", "Globex"));
        var repo = RepoWith(MakeMessage("m1", "only one"));

        await new DateGroundingProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        state.Build().SectionOrder.Should().NotContainKey(ProjectionSectionKeys.Facts);
    }

    [Fact]
    public void DateGroundingIsEnabledByEitherOfItsTwoFlags()
    {
        var feature = new DateGroundingProjectionFeature(Substitute.For<IMessageRepository>());

        feature.IsEnabled(MemoryProjectionOptions.Default).Should().BeFalse();
        feature.IsEnabled(DatesOn).Should().BeTrue();
        feature.IsEnabled(MemoryProjectionOptions.Default with { ChronologicalOrdering = true })
            .Should().BeTrue("ordering needs the dates it orders by");
    }

    // ── the shared fetch ──────────────────────────────────────────────

    [Fact]
    public async Task BothFeaturesTogetherIssueExactlyOneRepositoryCall()
    {
        // THE budget claim: one extra read per recall, not one per feature. Memoised on the state, so
        // the second feature awaits the first one's task.
        var options = MemoryProjectionOptions.Default with
        {
            AttachSourceQuotes = true,
            GroundDates = true,
        };
        var state = State(options, MakeFact("f1", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1", "Bob joined Acme in June."));

        await new SourceQuoteProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);
        await new DateGroundingProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        await repo.Received(1).GetByIdsAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        var annotation = state.Build().Annotations["f1"];
        annotation.SourceQuote.Should().NotBeNull();
        annotation.SourceDate.Should().NotBeNull();
    }

    [Fact]
    public async Task TheSameSourceMessageIsFetchedOnceForManyFacts()
    {
        // One utterance commonly produced several facts; fetching it per fact would multiply the read
        // by the section size.
        var state = State(QuotesOn,
            MakeFact("f1", "Acme", "m1"), MakeFact("f2", "Acme", "m1"), MakeFact("f3", "Acme", "m1"));
        var repo = RepoWith(MakeMessage("m1", "Bob joined Acme in June."));

        await new SourceQuoteProjectionFeature(repo).ApplyAsync(state, CancellationToken.None);

        await repo.Received(1).GetByIdsAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoSourceIdsAnywhereMeansNoReadAtAll()
    {
        var repo = Substitute.For<IMessageRepository>();

        await new SourceQuoteProjectionFeature(repo)
            .ApplyAsync(State(QuotesOn, MakeFact("f1", "Acme")), CancellationToken.None);

        await repo.DidNotReceive().GetByIdsAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
