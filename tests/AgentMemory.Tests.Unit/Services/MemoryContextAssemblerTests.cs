using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Services;

public sealed class MemoryContextAssemblerTests
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly ILongTermMemoryService _longTerm;
    private readonly IReasoningMemoryService _reasoning;
    private readonly IGraphRagContextSource _graphRag;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly DateTimeOffset _fixedTime = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly float[] _generatedEmbedding = new float[1536];

    public MemoryContextAssemblerTests()
    {
        _shortTerm = Substitute.For<IShortTermMemoryService>();
        _longTerm = Substitute.For<ILongTermMemoryService>();
        _reasoning = Substitute.For<IReasoningMemoryService>();
        _graphRag = Substitute.For<IGraphRagContextSource>();
        _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        _clock = Substitute.For<IClock>();

        _clock.UtcNow.Returns(_fixedTime);

        _embeddingOrchestrator
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_generatedEmbedding));

        // Default: all services return empty results
        SetupEmptyServiceReturns();
    }

    private void SetupEmptyServiceReturns()
    {
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _shortTerm
            .SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _longTerm
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        _longTerm
            .SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTerm
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));
        _reasoning
            .SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(Array.Empty<ReasoningTrace>()));
    }

    private static readonly IMemoryIsolationPolicy SingleTenantPolicy =
        new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private MemoryContextAssembler CreateSut(
        IOptions<MemoryOptions>? options = null,
        IGraphRagContextSource? graphRag = null) =>
        new(_shortTerm, _longTerm, _reasoning, graphRag, _embeddingOrchestrator, _clock,
            options ?? Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance, SingleTenantPolicy);

    private static RecallRequest CreateRequest(float[]? queryEmbedding = null, RetrievalBlendMode? blendMode = null) => new()
    {
        SessionId = "session-1",
        Query = "What do I know about the project?",
        QueryEmbedding = queryEmbedding,
        Options = blendMode is null
            ? RecallOptions.Default
            : new RecallOptions { BlendMode = blendMode.Value }
    };

    private void SetupGraphRagReturns(string text)
        => _graphRag
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GraphRagContextResult
            {
                Items = new[] { new GraphRagContextItem { Text = text, Score = 0.9 } }
            }));

    [Fact]
    public async Task AssembleContextAsync_AppliesQueryIntent_AsAmbientRankingOverride_DuringLongTermSearch()
    {
        // D3: the assembler must publish a per-request MemoryRankingOptions (derived from the intent) for
        // the long-term searches, then reset it so it never leaks past the recall.
        var ctx = new DefaultMemoryRankingContext();
        MemoryRankingOptions? capturedDuringSearch = null;
        _longTerm
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                capturedDuringSearch = ctx.Current;
                return Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>());
            });

        var sut = new MemoryContextAssembler(
            _shortTerm, _longTerm, _reasoning, null, _embeddingOrchestrator, _clock,
            Options.Create(new MemoryOptions()), NullLogger<MemoryContextAssembler>.Instance, SingleTenantPolicy, ctx);

        await sut.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s",
            Query = "q",
            QueryEmbedding = new float[] { 1f },   // non-empty ⇒ the long-term searches run
            Options = new RecallOptions { Intent = RankingIntent.Latest }
        });

        capturedDuringSearch.Should().NotBeNull("the assembler must publish a per-request ranking for the intent");
        capturedDuringSearch!.EffectiveRecencyWeight.Should().BeGreaterThan(0, "Latest raises the recency weight");
        ctx.Current.Should().BeNull("the override must be reset after the long-term searches, never leaking");
    }

    [Fact]
    public async Task AssembleContextAsync_DefaultIntent_DoesNotTouchRankingContext()
    {
        var ctx = new DefaultMemoryRankingContext();
        MemoryRankingOptions? capturedDuringSearch = new MemoryRankingOptions { RecencyWeight = 0.42 };
        _longTerm
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { capturedDuringSearch = ctx.Current; return Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()); });

        var sut = new MemoryContextAssembler(
            _shortTerm, _longTerm, _reasoning, null, _embeddingOrchestrator, _clock,
            Options.Create(new MemoryOptions()), NullLogger<MemoryContextAssembler>.Instance, SingleTenantPolicy, ctx);

        await sut.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s", Query = "q", QueryEmbedding = new float[] { 1f },
            Options = RecallOptions.Default   // Intent = Default
        });

        capturedDuringSearch.Should().BeNull("Default intent must not publish any override");
    }

    [Fact]
    public async Task AssembleContextAsync_MemoryOnly_SkipsGraphRagEvenWhenEnabled()
    {
        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        var result = await sut.AssembleContextAsync(
            CreateRequest(queryEmbedding: new float[1536], blendMode: RetrievalBlendMode.MemoryOnly));

        await _graphRag.DidNotReceive()
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>());
        await _shortTerm.Received(1)
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        result.GraphRagContext.Should().BeNull();
        result.BlendMode.Should().Be(RetrievalBlendMode.MemoryOnly);
    }

    [Fact]
    public async Task AssembleContextAsync_GraphRagOnly_SkipsMemoryLayersAndEmbedding()
    {
        SetupGraphRagReturns("graph-only context");
        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        // No provided embedding — GraphRagOnly must not trigger embedding generation.
        var result = await sut.AssembleContextAsync(
            CreateRequest(queryEmbedding: null, blendMode: RetrievalBlendMode.GraphRagOnly));

        await _embeddingOrchestrator.DidNotReceive()
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _shortTerm.DidNotReceive()
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _longTerm.DidNotReceive()
            .SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _graphRag.Received(1)
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>());

        result.RecentMessages.Items.Should().BeEmpty();
        result.RelevantEntities.Items.Should().BeEmpty();
        result.GraphRagContext.Should().Contain("graph-only context");
        result.BlendMode.Should().Be(RetrievalBlendMode.GraphRagOnly);
    }

    [Fact]
    public async Task AssembleContextAsync_ScopedViaRecallOptionsScope_UsesResolvedOwner_NotRawRequestUserId()
    {
        // #100 Stage 2 regression: a caller scoping purely via RecallOptions.Scope (explicit scope wins
        // over UserId -- a first-class, documented pattern) with RecallRequest.UserId left null must scope
        // GraphRAG to the SAME resolved owner as every other recall source, not silently run it unscoped.
        SetupGraphRagReturns("alice-scoped context");
        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        var request = new RecallRequest
        {
            SessionId = "session-1",
            Query = "anything",
            UserId = null,
            Options = RecallOptions.Default with { Scope = MemoryScope.For("alice") }
        };

        await sut.AssembleContextAsync(request);

        await _graphRag.Received(1).GetContextAsync(
            Arg.Is<GraphRagContextRequest>(r => r.UserId == "alice"), Arg.Any<CancellationToken>());
    }

    // R6-A: FetchGraphRagAsync had a broad catch that re-buried the OCE the GraphRAG source deliberately
    // rethrows on cancellation. In GraphRagOnly mode that swallow was authoritative: a cancelled recall
    // returned a normal (empty-graph) context instead of honoring cancellation.
    [Fact]
    public async Task AssembleContextAsync_GraphRagCancelled_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _graphRag.GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        var act = async () => await sut.AssembleContextAsync(
            CreateRequest(queryEmbedding: null, blendMode: RetrievalBlendMode.GraphRagOnly), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AssembleContextAsync_GraphRagThenMemory_RetrievesBothSources()
    {
        SetupGraphRagReturns("graph context");
        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        var result = await sut.AssembleContextAsync(
            CreateRequest(queryEmbedding: new float[1536], blendMode: RetrievalBlendMode.GraphRagThenMemory));

        await _shortTerm.Received(1)
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _graphRag.Received(1)
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>());
        result.GraphRagContext.Should().Contain("graph context");
        result.BlendMode.Should().Be(RetrievalBlendMode.GraphRagThenMemory);
    }

    [Fact]
    public async Task AssembleContextAsync_GeneratesEmbeddingWhenNotProvided()
    {
        var sut = CreateSut();
        var request = CreateRequest(queryEmbedding: null);

        await sut.AssembleContextAsync(request);

        await _embeddingOrchestrator
            .Received(1)
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsync_UsesProvidedEmbedding()
    {
        var sut = CreateSut();
        var providedEmbedding = new float[1536];
        var request = CreateRequest(queryEmbedding: providedEmbedding);

        await sut.AssembleContextAsync(request);

        await _embeddingOrchestrator
            .DidNotReceive()
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsync_RetrievesFromAllMemoryLayers()
    {
        var sut = CreateSut();

        await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        await _shortTerm.Received(1).GetRecentMessagesAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _shortTerm.Received(1).SearchMessagesAsync(
            Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _longTerm.Received(1).SearchEntitiesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _longTerm.Received(1).SearchPreferencesAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _longTerm.Received(1).SearchFactsAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsync_SkipsGraphRagWhenDisabled()
    {
        var options = Options.Create(new MemoryOptions { EnableGraphRag = false });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        await _graphRag
            .DidNotReceive()
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssembleContextAsync_SkipsGraphRagWhenSourceIsNull()
    {
        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: null); // null source

        var act = async () => await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AssembleContextAsync_IncludesGraphRagWhenEnabled()
    {
        var graphRagResult = new GraphRagContextResult
        {
            Items = new[] { new GraphRagContextItem { Text = "GraphRAG context text", Score = 0.9 } }
        };
        _graphRag
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(graphRagResult));

        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        await _graphRag
            .Received(1)
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>());
        result.GraphRagContext.Should().Contain("GraphRAG context text");
    }

    [Fact]
    public async Task AssembleContextAsync_SetsAssembledTimestamp()
    {
        var sut = CreateSut();

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        result.AssembledAtUtc.Should().Be(_fixedTime);
        result.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task AssembleContextAsync_EnforcesBudgetOldestFirst()
    {
        // Two messages each 10 chars — budget allows only 1
        var oldMsg = CreateMessage("old", "1234567890", _fixedTime.AddHours(-1));
        var newMsg = CreateMessage("new", "ABCDEFGHIJ", _fixedTime);
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(new[] { oldMsg, newMsg }));

        var options = Options.Create(new MemoryOptions
        {
            ContextBudget = new ContextBudget
            {
                MaxCharacters = 11, // fits only 1 of the 10-char messages
                TruncationStrategy = TruncationStrategy.OldestFirst
            }
        });
        var sut = CreateSut(options: options);

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // Budget enforced: fewer messages than provided
        result.RecentMessages.Items.Count.Should().BeLessThan(2);
        // OldestFirst: the newest message should be preserved
        result.RecentMessages.Items.Should().Contain(m => m.MessageId == "new");
    }

    [Fact]
    public async Task AssembleContextAsync_SetsTruncatedFlag_OnlyWhenBudgetDropsItems()
    {
        // The MemoryContext.Truncated flag (surfaced as RecallResult.Truncated, read by MCP clients) must
        // reflect whether budget truncation actually dropped context — it used to be permanently false.
        var oldMsg = CreateMessage("old", "1234567890", _fixedTime.AddHours(-1));
        var newMsg = CreateMessage("new", "ABCDEFGHIJ", _fixedTime);
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(new[] { oldMsg, newMsg }));

        var overBudget = await CreateSut(options: Options.Create(new MemoryOptions
        {
            ContextBudget = new ContextBudget { MaxCharacters = 11, TruncationStrategy = TruncationStrategy.OldestFirst }
        })).AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));
        overBudget.Truncated.Should().BeTrue("the budget forced an item to be dropped");

        var withinBudget = await CreateSut(options: Options.Create(new MemoryOptions
        {
            ContextBudget = new ContextBudget { MaxCharacters = 100_000, TruncationStrategy = TruncationStrategy.OldestFirst }
        })).AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));
        withinBudget.Truncated.Should().BeFalse("everything fit within the budget");
    }

    [Fact]
    public async Task AssembleContextAsync_EnforcesBudgetLowestScoreFirst()
    {
        // Items are assumed ordered by score descending; lowest score (last) is dropped first
        var highScoreMsg = CreateMessage("high-score", "AAAAAAAAAA", _fixedTime);
        var lowScoreMsg = CreateMessage("low-score", "BBBBBBBBBB", _fixedTime.AddMinutes(-1));
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(new[] { highScoreMsg, lowScoreMsg }));

        var options = Options.Create(new MemoryOptions
        {
            ContextBudget = new ContextBudget
            {
                MaxCharacters = 11, // fits only 1 of the 10-char messages
                TruncationStrategy = TruncationStrategy.LowestScoreFirst
            }
        });
        var sut = CreateSut(options: options);

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // Budget enforced: fewer messages than provided
        result.RecentMessages.Items.Count.Should().BeLessThan(2);
        // LowestScoreFirst: highest-score item (first in list) should be preserved
        result.RecentMessages.Items.Should().Contain(m => m.MessageId == "high-score");
    }

    // ---- 3.2: distinct truncation strategies over IDENTICAL cross-section input ----
    //
    // Shared fixture: 3 items of 10 chars each (total 30) across two sections.
    //   recent  = [R0 (newest, best-in-section), R1 (mid age, worst-in-section)]
    //   facts   = [F0 (oldest, best-in-section)]
    // A budget of 21 chars forces removal of exactly ONE item. Each strategy must
    // pick a DIFFERENT victim, proving the strategies are no longer aliases.

    private void SetupCrossSectionInput()
    {
        var r0 = CreateMessage("R0", "AAAAAAAAAA", _fixedTime);                 // newest, best score (index 0)
        var r1 = CreateMessage("R1", "BBBBBBBBBB", _fixedTime.AddMinutes(-1));  // mid age, worst score (index 1)
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(new[] { r0, r1 }));

        var f0 = CreateFact("F0", "S", "P", "OOOO", _fixedTime.AddHours(-1));   // oldest, best score (index 0)
        _longTerm
            .SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(new[] { f0 }));
    }

    private static IOptions<MemoryOptions> BudgetOptions(TruncationStrategy strategy) =>
        Options.Create(new MemoryOptions
        {
            ContextBudget = new ContextBudget { MaxCharacters = 21, TruncationStrategy = strategy }
        });

    [Fact]
    public async Task AssembleContextAsync_OldestFirst_RemovesGloballyOldestAcrossSections()
    {
        SetupCrossSectionInput();
        var sut = CreateSut(options: BudgetOptions(TruncationStrategy.OldestFirst));

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // F0 is the globally oldest item, so it is dropped; both recent messages survive.
        result.RelevantFacts.Items.Should().BeEmpty();
        result.RecentMessages.Items.Should().Contain(m => m.MessageId == "R0");
        result.RecentMessages.Items.Should().Contain(m => m.MessageId == "R1");
    }

    [Fact]
    public async Task AssembleContextAsync_LowestScoreFirst_RemovesGloballyLowestRankedAcrossSections()
    {
        SetupCrossSectionInput();
        var sut = CreateSut(options: BudgetOptions(TruncationStrategy.LowestScoreFirst));

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // R1 is the lowest-ranked item within its section, so it is dropped while F0 (best-in-section) survives.
        // This is a DIFFERENT victim than OldestFirst chose (F0), proving the strategies diverge.
        result.RecentMessages.Items.Should().Contain(m => m.MessageId == "R0");
        result.RecentMessages.Items.Should().NotContain(m => m.MessageId == "R1");
        result.RelevantFacts.Items.Should().Contain(f => f.FactId == "F0");
    }

    [Fact]
    public async Task AssembleContextAsync_Proportional_TrimsEachSectionByRatio()
    {
        SetupCrossSectionInput();
        var sut = CreateSut(options: BudgetOptions(TruncationStrategy.Proportional));

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // ratio = 21/30 = 0.7 → recent target ~14 chars keeps only R0; facts target ~7 chars keeps none.
        // A third distinct outcome from OldestFirst and LowestScoreFirst.
        result.RecentMessages.Items.Should().ContainSingle().Which.MessageId.Should().Be("R0");
        result.RelevantFacts.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AssembleContextAsync_Fail_ThrowsWhenOverBudget()
    {
        SetupCrossSectionInput();
        var sut = CreateSut(options: BudgetOptions(TruncationStrategy.Fail));

        var act = async () => await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        (await act.Should().ThrowAsync<MemoryException>())
            .Which.Code.Should().Be(MemoryErrorCodes.ContextBudgetExceeded);
    }

    [Fact]
    public async Task AssembleContextAsync_ReportsEstimatedTokenCount()
    {
        var messages = new[]
        {
            CreateMessage("msg-1", "Hello world content here", _fixedTime),
            CreateMessage("msg-2", "More content to count chars", _fixedTime.AddMinutes(-1))
        };
        _shortTerm
            .GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(messages));
        var sut = CreateSut();

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        // The assembled context has content — char count should be positive
        var totalChars = result.RecentMessages.Items.Sum(m => m.Content.Length);
        totalChars.Should().BeGreaterThan(0);
        // Token estimate: chars / 4 is the standard approximation
        (totalChars / 4).Should().BeGreaterThan(0);
    }

    // ---- 3.2 review fixes: GraphRAG truncation + temporal budget enforcement ----

    [Fact]
    public async Task AssembleContextAsync_DropsGraphRagWhenOverBudgetAndSectionsEmpty()
    {
        // All sections empty; only a large GraphRAG block, which must be dropped under a tiny budget.
        _graphRag
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GraphRagContextResult
            {
                Items = new[] { new GraphRagContextItem { Text = new string('X', 100), Score = 0.9 } }
            }));
        var options = Options.Create(new MemoryOptions
        {
            EnableGraphRag = true,
            ContextBudget = new ContextBudget { MaxCharacters = 10, TruncationStrategy = TruncationStrategy.OldestFirst }
        });
        var sut = CreateSut(options: options, graphRag: _graphRag);

        var result = await sut.AssembleContextAsync(CreateRequest(queryEmbedding: new float[1536]));

        result.GraphRagContext.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_EnforcesContextBudget()
    {
        var e1 = CreateEntity("e1", "AA", _fixedTime);                 // ~12 chars each (Name 2 + 10)
        var e2 = CreateEntity("e2", "BB", _fixedTime.AddMinutes(-1));
        _shortTerm
            .GetRecentMessagesAsOfAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        _longTerm
            .SearchEntitiesAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(new[] { e1, e2 }));
        _longTerm
            .SearchPreferencesAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        _longTerm
            .SearchFactsAsOfAsync(Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));

        var options = Options.Create(new MemoryOptions
        {
            ContextBudget = new ContextBudget { MaxCharacters = 15, TruncationStrategy = TruncationStrategy.OldestFirst }
        });
        var sut = CreateSut(options: options);

        var result = await sut.AssembleContextAsOfAsync(
            CreateRequest(queryEmbedding: new float[1536]), _fixedTime);

        // Two ~12-char entities exceed the 15-char budget; temporal recall now truncates to one.
        result.RelevantEntities.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task AssembleContextAsOfAsync_EmptyEmbedding_SkipsVectorSearches_InsteadOfThrowing()
    {
        // A blank query (or a transient embed failure) yields an empty vector; like the live path, the
        // as-of path must skip the semantic searches rather than issue a zero-dimension vector query that
        // the index rejects (which would throw instead of degrading to recent messages + empty sections).
        _shortTerm
            .GetRecentMessagesAsOfAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));

        var sut = CreateSut();

        var result = await sut.AssembleContextAsOfAsync(
            new RecallRequest { SessionId = "s", Query = "", QueryEmbedding = Array.Empty<float>() }, _fixedTime);

        await _longTerm.DidNotReceive().SearchEntitiesAsOfAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _longTerm.DidNotReceive().SearchFactsAsOfAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await _reasoning.DidNotReceive().SearchSimilarTracesAsOfAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());

        result.RelevantEntities.Items.Should().BeEmpty();
        result.RelevantFacts.Items.Should().BeEmpty();
    }

    // ---- Helpers ----

    private static Entity CreateEntity(string id, string name, DateTimeOffset createdAt) => new()
    {
        EntityId = id,
        Name = name,
        Type = "Person",
        Confidence = 1.0,
        CreatedAtUtc = createdAt
    };

    private static Message CreateMessage(string id, string content, DateTimeOffset timestamp) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = "session-1",
        Role = "user",
        Content = content,
        TimestampUtc = timestamp
    };

    private static Fact CreateFact(string id, string subject, string predicate, string @object, DateTimeOffset createdAt) => new()
    {
        FactId = id,
        Subject = subject,
        Predicate = predicate,
        Object = @object,
        Confidence = 1.0,
        CreatedAtUtc = createdAt
    };
}
