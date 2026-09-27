using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.TestSupport;

/// <summary>Stubbing helpers for <see cref="IMemoryService"/> substitutes.</summary>
internal static class MemoryServiceStubs
{
    /// <summary>
    /// Answers the id-keyed store (how the Agent Framework providers store request messages since the
    /// provenance fix) the way a real store does: the message back, under the caller's id. Without it an
    /// unconfigured substitute returns null for the message.
    /// </summary>
    internal static IMemoryService RouteIdKeyedAdds(this IMemoryService memory)
    {
        memory.AddMessageWithIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new Message
            {
                MessageId = call.ArgAt<string>(4), SessionId = call.ArgAt<string>(0), ConversationId = call.ArgAt<string>(1),
                Role = call.ArgAt<string>(2), Content = call.ArgAt<string>(3), TimestampUtc = DateTimeOffset.UnixEpoch,
                Metadata = call.ArgAt<IReadOnlyDictionary<string, object>?>(5) ?? new Dictionary<string, object>(),
            }));
        return memory;
    }
}
