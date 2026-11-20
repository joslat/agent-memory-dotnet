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
/// A live recall starts its section searches together. When one fails, the recall must not return (or throw) while a
/// sibling search is still running: that work would go on unobserved, its own failure never seen, and a host counting
/// capacity by the call would think the database is idle. The as-of path already waits for every section first.
/// </summary>
public sealed class SectionSettlingTests
{
    [Fact]
    public async Task A_failed_section_is_reported_only_after_its_siblings_have_settled()
    {
        var sibling = new TaskCompletionSource<IReadOnlyList<Message>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shortTerm = Substitute.For<IShortTermMemoryService>();
        shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<Message>>(new InvalidOperationException("recent failed")));
        shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(sibling.Task);

        var recall = Create(shortTerm).AssembleContextAsync(Request(), CancellationToken.None);
        var settledEarly = await Task.WhenAny(recall, Task.Delay(200)) == recall;

        sibling.SetResult([]);
        await FluentActions.Awaiting(() => recall).Should().ThrowAsync<InvalidOperationException>().WithMessage("recent failed");
        settledEarly.Should().BeFalse("the relevant-message search was still running when the recent-message search failed");
    }

    [Fact]
    public async Task With_every_section_succeeding_the_recall_is_unchanged()
    {
        var shortTerm = Substitute.For<IShortTermMemoryService>();
        var recent = new Message
        {
            MessageId = "m1", ConversationId = "c1", SessionId = "s1", Role = "user", Content = "hello",
            TimestampUtc = DateTimeOffset.UnixEpoch,
        };
        shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { recent });
        shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        var context = await Create(shortTerm).AssembleContextAsync(Request(), CancellationToken.None);

        context.RecentMessages.Items.Select(m => m.MessageId).Should().Equal("m1");
    }

    private static RecallRequest Request() => new()
    {
        SessionId = "s1",
        UserId = "u1",
        Query = "what did we say?",
        QueryEmbedding = [0.1f, 0.2f, 0.3f],
        Options = new RecallOptions
        {
            MaxRecentMessages = 5, MaxRelevantMessages = 5, MaxFacts = 0, MaxEntities = 0, MaxPreferences = 0, MaxTraces = 0,
        },
    };

    private static MemoryContextAssembler Create(IShortTermMemoryService shortTerm)
    {
        var options = new MemoryOptions();
        return new MemoryContextAssembler(
            shortTerm,
            Substitute.For<ILongTermMemoryService>(),
            Substitute.For<IReasoningMemoryService>(),
            graphRag: null,
            Substitute.For<IEmbeddingOrchestrator>(),
            Substitute.For<TimeProvider>(),
            Options.Create(options),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(options.Isolation), NullLogger<DefaultMemoryIsolationPolicy>.Instance));
    }
}
