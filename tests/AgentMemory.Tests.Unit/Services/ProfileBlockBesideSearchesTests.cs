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
/// DRT-3 (C-02): recall starts the profile block's point-read beside its searches instead of after them. The block
/// read has started while a search is still pending, and a recall whose search fails leaves no unobserved task.
/// </summary>
public sealed class ProfileBlockBesideSearchesTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IWorkingMemoryService _block = Substitute.For<IWorkingMemoryService>();
    private readonly TaskCompletionSource<IReadOnlyList<Fact>> _factSearch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _blockRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly IMemoryIsolationPolicy Policy =
        new DefaultMemoryIsolationPolicy(Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    public ProfileBlockBesideSearchesTests()
    {
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new float[8]));
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));
        _longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>([]));
        _longTerm.SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([]));
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ => _factSearch.Task);
        _longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<ValidTimeMode>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ => _factSearch.Task);
        _reasoning.SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));
        _block.GetAsync("owner-1", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _blockRead.TrySetResult();
            return Task.FromResult<WorkingMemoryBlock?>(new WorkingMemoryBlock
            {
                OwnerId = "owner-1", Text = "Known about the user: works at Acme", BuiltAtUtc = DateTimeOffset.UtcNow, ContentHash = "h",
            });
        });
    }

    private Task<MemoryContext> RecallAsync() =>
        new MemoryContextAssembler(_shortTerm, _longTerm, _reasoning, null, _embeddings, TimeProvider.System,
                Options.Create(new MemoryOptions()), NullLogger<MemoryContextAssembler>.Instance, Policy,
                rankingContext: null, truncationStrategies: null, rerankers: null, projectionFeatures: null, workingMemory: _block)
            .AssembleContextAsync(new RecallRequest
            {
                SessionId = "s1", UserId = "owner-1", Query = "Where do I work?", QueryEmbedding = new float[8], Options = RecallOptions.Default,
            }, CancellationToken.None);

    [Fact]
    public async Task The_block_read_starts_while_a_search_is_still_pending()
    {
        var recall = RecallAsync();

        (await Task.WhenAny(_blockRead.Task, Task.Delay(TimeSpan.FromSeconds(10)))).Should().Be(_blockRead.Task);
        recall.IsCompleted.Should().BeFalse("the fact search has not answered yet");
        _factSearch.SetResult([]);
        var context = await recall;

        context.WorkingMemoryBlock.Should().Be("Known about the user: works at Acme");
        await _block.Received(1).GetAsync("owner-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_search_with_a_failed_block_read_leaves_no_unobserved_task()
    {
        _block.GetAsync("owner-1", Arg.Any<CancellationToken>()).Returns(Task.FromException<WorkingMemoryBlock?>(new InvalidOperationException("block")));
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> count = (_, _) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += count;
        try
        {
            _factSearch.SetException(new InvalidOperationException("search"));
            var failing = () => RecallAsync();
            await failing.Should().ThrowAsync<Exception>();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= count;
        }

        unobserved.Should().Be(0);
    }
}
