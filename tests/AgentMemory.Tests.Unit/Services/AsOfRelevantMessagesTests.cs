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
/// B-6 (the bitemporal recheck, 2026-10-03): a date in the question routes recall to the as-of path, which ran no message
/// search, so "what did we talk about last weekend?" lost its messages. The session's relevant messages are now searched
/// there and kept to those said by the transaction instant, and not withdrawn by then.
/// </summary>
public sealed class AsOfRelevantMessagesTests
{
    private static readonly DateTimeOffset SystemAsOf = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_as_of_path_recalls_the_sessions_messages_said_by_its_instant()
    {
        var before = Message("m1", "We planned a weekend in Lisbon.", SystemAsOf.AddDays(-3));
        var after = Message("m2", "Actually we booked Porto instead.", SystemAsOf.AddHours(1));
        var withdrawn = Message("m3", "My card number is…", SystemAsOf.AddDays(-2)) with { InvalidatedAtUtc = SystemAsOf.AddDays(-1) };
        var assembler = Create(before, after, withdrawn);

        var context = await assembler.AssembleContextAsOfAsync(Request(), SystemAsOf, SystemAsOf, CancellationToken.None);

        context.RelevantMessages.Items.Select(m => m.MessageId).Should().Equal(["m1"],
            "said by the instant and not withdrawn by then; the later message did not exist yet");
    }

    [Fact]
    public async Task With_relevant_messages_capped_at_zero_nothing_is_searched()
    {
        var shortTerm = Substitute.For<IShortTermMemoryService>();
        var assembler = Create(shortTerm);

        await assembler.AssembleContextAsOfAsync(Request() with { Options = Request().Options with { MaxRelevantMessages = 0 } },
            SystemAsOf, SystemAsOf, CancellationToken.None);

        await shortTerm.DidNotReceiveWithAnyArgs().SearchMessagesAsync(default, default!, default, default, default);
    }

    private static Message Message(string id, string text, DateTimeOffset at) => new()
    {
        MessageId = id, ConversationId = "c1", SessionId = "s1", Role = "user", Content = text, TimestampUtc = at,
    };

    private static RecallRequest Request() => new()
    {
        SessionId = "s1",
        UserId = "u1",
        Query = "what did we plan last weekend?",
        QueryEmbedding = new float[8],
        Options = new RecallOptions { MaxRelevantMessages = 5, MaxRecentMessages = 0, MaxFacts = 0, MaxEntities = 0, MaxPreferences = 0, MaxTraces = 0 },
    };

    private static MemoryContextAssembler Create(params Message[] found)
    {
        var shortTerm = Substitute.For<IShortTermMemoryService>();
        shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(found);
        return Create(shortTerm);
    }

    private static MemoryContextAssembler Create(IShortTermMemoryService shortTerm)
    {
        shortTerm.GetRecentMessagesAsOfAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var options = new MemoryOptions();
        return new MemoryContextAssembler(
            shortTerm,
            Substitute.For<ILongTermMemoryService>(),
            Substitute.For<IReasoningMemoryService>(),
            graphRag: null,
            Substitute.For<IEmbeddingOrchestrator>(),
            Substitute.For<IClock>(),
            Options.Create(options),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(options.Isolation), NullLogger<DefaultMemoryIsolationPolicy>.Instance));
    }
}
