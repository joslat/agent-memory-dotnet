using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// An empty recall section must say <b>why</b> it is empty.
/// </summary>
/// <remarks>
/// <para>
/// Three causes need opposite responses and looked identical from the outside: the section was never
/// searched, the store genuinely holds nothing, or candidates existed and were filtered away — by the
/// similarity floor, or by an owner post-filter applied after the vector index chose a global top-K.
/// </para>
/// <para>
/// This is not hypothetical. Analysing recorded benchmark runs turned up a question that retrieved
/// <b>2 facts from a 710-fact graph</b> with the answer demonstrably present in memory, and no
/// artifact could say which of the three had happened — the investigation stopped there for want of
/// exactly this signal.
/// </para>
/// </remarks>
public sealed class RecallSectionDiagnosticsTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly IMemoryIsolationPolicy SingleTenant =
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions()),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    public RecallSectionDiagnosticsTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _shortTerm.SearchMessagesAsync(
                Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _longTerm.SearchEntitiesAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        _longTerm.SearchPreferencesAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        _reasoning.SearchSimilarTracesAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(Array.Empty<ReasoningTrace>()));
    }

    private MemoryContextAssembler CreateSut() =>
        new(_shortTerm, _longTerm, _reasoning, null, _embeddings, _clock,
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance, SingleTenant);

    private static RecallRequest Request(RecallOptions options) => new()
    {
        SessionId = "s",
        Query = "what did we decide",
        Options = options,
    };

    [Fact]
    public async Task DiagnosticsAreAbsentByDefault()
    {
        // Off unless asked for: every section keeps its null default and none of this work runs.
        var context = await CreateSut().AssembleContextAsync(Request(RecallOptions.Default));

        context.RelevantFacts.Diagnostics.Should().BeNull();
        context.RelevantEntities.Diagnostics.Should().BeNull();
        context.RecentMessages.Diagnostics.Should().BeNull();
    }

    [Fact]
    public async Task ASearchedButEmptySectionIsDistinguishableFromAnUnsearchedOne()
    {
        // The load-bearing case. Facts were searched and came back empty; traces were excluded by a
        // zero limit. Both sections hold zero items, and before this they were indistinguishable.
        var context = await CreateSut().AssembleContextAsync(Request(
            RecallOptions.Default with { IncludeDiagnostics = true, MaxTraces = 0 }));

        context.RelevantFacts.Diagnostics!.Searched.Should().BeTrue();
        context.RelevantFacts.Diagnostics!.SearchedAndEmpty.Should().BeTrue();

        context.SimilarTraces.Diagnostics!.Searched.Should().BeFalse();
        context.SimilarTraces.Diagnostics!.SearchedAndEmpty.Should().BeFalse(
            "a section that never ran says nothing about the store, and must not read as 'found nothing'");
    }

    [Fact]
    public async Task AShortResultIsVisibleAsShort()
    {
        // The owner-starvation shape: the index picks a global top-K and the owner filter is applied
        // afterwards, so a SHORT result can mean crowded out rather than genuinely sparse. Escalation
        // fires only on a total zero, so this case is otherwise invisible.
        _longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(new[]
            {
                new Fact
                {
                    FactId = "f1", Subject = "a", Predicate = "b", Object = "c",
                    Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UnixEpoch,
                },
            }));

        var context = await CreateSut().AssembleContextAsync(Request(
            RecallOptions.Default with { IncludeDiagnostics = true, MaxFacts = 10 }));

        var diagnostics = context.RelevantFacts.Diagnostics!;
        diagnostics.Returned.Should().Be(1);
        diagnostics.RequestedLimit.Should().Be(10);
        diagnostics.SearchedAndShort.Should().BeTrue();
    }

    [Fact]
    public async Task TheSimilarityFloorIsRecordedSoBelowThresholdIsCheckable()
    {
        var context = await CreateSut().AssembleContextAsync(Request(
            RecallOptions.Default with { IncludeDiagnostics = true, MinSimilarityScore = 0.83 }));

        context.RelevantFacts.Diagnostics!.MinimumScore.Should().Be(0.83);
    }

    [Fact]
    public async Task AnUnscoreableSectionReportsNullScoresRatherThanZero()
    {
        // Zero is a real score. A provider that cannot supply scores must produce null, or a reader
        // cannot tell "scored badly" from "not scored".
        var context = await CreateSut().AssembleContextAsync(Request(
            RecallOptions.Default with { IncludeDiagnostics = true }));

        context.RelevantFacts.Diagnostics!.TopScore.Should().BeNull();
        context.RelevantFacts.Diagnostics!.LowestScore.Should().BeNull();
    }

    [Fact]
    public async Task WhenNoEmbeddingIsAvailableEveryVectorSectionReportsNotSearched()
    {
        // Degraded embedding: the vector searches genuinely did not run, and the sections must say so
        // rather than implying the store was queried and found wanting.
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<float>()));

        var context = await CreateSut().AssembleContextAsync(Request(
            RecallOptions.Default with { IncludeDiagnostics = true }));

        context.RelevantFacts.Diagnostics!.Searched.Should().BeFalse();
        context.RelevantEntities.Diagnostics!.Searched.Should().BeFalse();
        context.RelevantPreferences.Diagnostics!.Searched.Should().BeFalse();
        context.SimilarTraces.Diagnostics!.Searched.Should().BeFalse();
        context.RecentMessages.Diagnostics!.Searched.Should().BeTrue(
            "recent messages are session-scoped and time-ordered, so they need no vector");
    }

    [Fact]
    public async Task TheAsOfPathReportsDiagnosticsToo()
    {
        // Asserted separately rather than assumed: these two paths have already drifted apart once on
        // a single option (SuccessfulTracesOnly is passed live and hardcoded null as-of).
        _shortTerm.GetRecentMessagesAsOfAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _longTerm.SearchFactsAsOfAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));

        var context = await CreateSut().AssembleContextAsOfAsync(
            Request(RecallOptions.Default with { IncludeDiagnostics = true, MaxTraces = 0 }),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        context.RelevantFacts.Diagnostics.Should().NotBeNull();
        context.RelevantFacts.Diagnostics!.SearchedAndEmpty.Should().BeTrue();
        context.SimilarTraces.Diagnostics!.Searched.Should().BeFalse();
    }
}
