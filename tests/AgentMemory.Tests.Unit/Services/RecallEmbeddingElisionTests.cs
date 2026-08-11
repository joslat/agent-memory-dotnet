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
/// The query embedding must not be generated when no retrieval that needs one will run.
/// </summary>
/// <remarks>
/// <para>
/// A task-aware recall policy narrows a turn by zeroing the per-category <c>MaxX</c> limits, and every
/// vector search is already gated on its own limit. The <b>embedding was not</b>: it was generated
/// unconditionally whenever <c>includeMemory</c> held, so narrowing a turn down to recent messages
/// alone still paid for a provider round trip whose result nothing then read.
/// </para>
/// <para>
/// That cost is the largest single stage of a remote-shaped recall (~120 ms), so a policy that skips
/// categories to save work was <b>relocating</b> the call rather than eliminating it. Gating it in the
/// assembler — rather than in one caller — also covers the Semantic Kernel adapter, the CLI and the
/// facade, which reach this same method.
/// </para>
/// <para>
/// <b>Byte-identical at defaults.</b> Every shipped <c>RecallOptions</c> default leaves at least one
/// vector limit nonzero, so the gate is true and the call happens exactly as before. The tests below
/// assert that direction too: eliding an embedding a search then needed would turn a cost saving into
/// silent recall loss.
/// </para>
/// </remarks>
public sealed class RecallEmbeddingElisionTests
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

    public RecallEmbeddingElisionTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));

        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));

        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _shortTerm.GetRecentMessagesAsOfAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

    /// <summary>Recent messages are session-scoped and time-ordered — they need no vector at all.</summary>
    private static RecallOptions RecentMessagesOnly() => new()
    {
        MaxRecentMessages = 10,
        MaxRelevantMessages = 0,
        MaxEntities = 0,
        MaxPreferences = 0,
        MaxFacts = 0,
        MaxTraces = 0,
        MaxGraphRagItems = 0,
    };

    [Fact]
    public async Task NoEmbeddingIsGeneratedWhenEveryVectorCategoryIsExcluded()
    {
        var sut = CreateSut();

        await sut.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s",
            Query = "hi",
            Options = RecentMessagesOnly(),
        });

        await _embeddings.DidNotReceive()
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoEmbeddingIsGeneratedOnTheAsOfPathEither()
    {
        // Two recall paths have already diverged on one option before (SuccessfulTracesOnly is passed
        // live and hardcoded null as-of), so the as-of path gets its own assertion rather than an
        // assumption that it mirrors the live one.
        var sut = CreateSut();

        await sut.AssembleContextAsOfAsync(new RecallRequest
        {
            SessionId = "s",
            Query = "hi",
            Options = RecentMessagesOnly(),
        }, _clock.UtcNow, _clock.UtcNow);

        await _embeddings.DidNotReceive()
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(nameof(RecallOptions.MaxRelevantMessages))]
    [InlineData(nameof(RecallOptions.MaxEntities))]
    [InlineData(nameof(RecallOptions.MaxPreferences))]
    [InlineData(nameof(RecallOptions.MaxFacts))]
    [InlineData(nameof(RecallOptions.MaxTraces))]
    public async Task AnySurvivingVectorCategoryStillGeneratesTheEmbedding(string category)
    {
        // The direction that matters most: eliding an embedding a search then needs would convert a
        // cost saving into silent recall loss. One case per category, so a future edit cannot drop
        // one of them from the gate without a red test.
        var options = category switch
        {
            nameof(RecallOptions.MaxRelevantMessages) => RecentMessagesOnly() with { MaxRelevantMessages = 5 },
            nameof(RecallOptions.MaxEntities) => RecentMessagesOnly() with { MaxEntities = 5 },
            nameof(RecallOptions.MaxPreferences) => RecentMessagesOnly() with { MaxPreferences = 5 },
            nameof(RecallOptions.MaxFacts) => RecentMessagesOnly() with { MaxFacts = 5 },
            nameof(RecallOptions.MaxTraces) => RecentMessagesOnly() with { MaxTraces = 5 },
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };

        var sut = CreateSut();

        await sut.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s",
            Query = "what did we decide",
            Options = options,
        });

        await _embeddings.Received(1)
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShippedDefaultsAreUnchangedAndStillEmbedExactlyOnce()
    {
        // The byte-identical guarantee for every existing host: no shipped default excludes all five
        // vector categories, so the gate is true and the call happens exactly as it did before.
        var sut = CreateSut();

        await sut.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s",
            Query = "what did we decide",
            Options = RecallOptions.Default,
        });

        await _embeddings.Received(1)
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACallerSuppliedEmbeddingIsNeverRegenerated()
    {
        var sut = CreateSut();

        await sut.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s",
            Query = "what did we decide",
            QueryEmbedding = new float[] { 1f },
            Options = RecallOptions.Default,
        });

        await _embeddings.DidNotReceive()
            .EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
