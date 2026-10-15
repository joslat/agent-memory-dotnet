using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using Microsoft.Agents.AI;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

public sealed class PersistedTurnCallbackTests
{
    [Fact]
    public async Task CallbackReceivesStoredIdentitiesContentAndBothRolesRegardlessOfCoreUserOnlyFiltering()
    {
        var (memory, clock) = Services();
        var provider = new Observer(memory, clock, new() { ExtractFromUserMessagesOnly = true });
        ExtractionRequest? extraction = null;
        memory.ExtractAndPersistAsync(Arg.Do<ExtractionRequest>(x => extraction = x), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult { SourceMessageIds = [] });
        await Store(provider);
        provider.Seen.Should().NotBeNull();
        provider.Seen!.SessionId.Should().Be("s"); provider.Seen.ConversationId.Should().Be("c"); provider.Seen.Owner.Should().Be("owner");
        provider.Seen.AtUtc.Should().Be(DateTimeOffset.UnixEpoch);
        provider.Seen.Messages.Select(x => x.Id).Should().Equal("stored-user", "stored-assistant");
        provider.Seen.Messages.Select(x => x.Text).Should().Equal("stored:user", "stored:assistant");
        provider.Seen.Messages.Select(x => x.Role).Should().Equal("user", "assistant");
        extraction!.Messages.Select(x => x.Role).Should().Equal("user");
        await memory.Received(1).AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallbackStillRunsWhenCoreExtractionIsDisabled()
    {
        var (memory, clock) = Services(); var provider = new Observer(memory, clock, new() { AutoExtractOnPersist = false });
        await Store(provider); provider.Seen.Should().NotBeNull();
        await memory.DidNotReceive().ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledCallbackIsNotCalledAndDoesNotReadExtraClock()
    {
        var (memory, clock) = Services(); var provider = new Observer(memory, clock, new() { AutoExtractOnPersist = false }) { Enabled = false };
        clock.ClearReceivedCalls(); await Store(provider); provider.Seen.Should().BeNull();
        _ = clock.DidNotReceive().UtcNow;
    }

    [Fact]
    public async Task PartialPersistenceFailureDoesNotPublishASuccessfulTurn()
    {
        var (memory, clock) = Services();
        memory.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), "assistant", Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message>>(_ => throw new IOException("storage failure"));
        var provider = new Observer(memory, clock, new()); await Store(provider);
        provider.Seen.Should().BeNull();
        await memory.DidNotReceive().ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallbackFailureDoesNotPreventCoreExtraction()
    {
        var (memory, clock) = Services(); var provider = new Observer(memory, clock, new()) { Failure = new IOException("module failure") };
        await Store(provider);
        await memory.Received(1).ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallbackCancellationPropagates()
    {
        var (memory, clock) = Services(); using var cts = new CancellationTokenSource();
        var provider = new Observer(memory, clock, new()) { BeforeCallback = () => cts.Cancel() };
        var action = () => Store(provider, cts.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        await memory.Received(1).ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyTurnDoesNotPublishOrReadClock()
    {
        var (memory, clock) = Services(); var provider = new Observer(memory, clock, new()); clock.ClearReceivedCalls();
        await provider.PerformStoreAsync([], [], "s", "c", CancellationToken.None, "owner");
        provider.Seen.Should().BeNull(); _ = clock.DidNotReceive().UtcNow;
    }

    [Fact]
    public async Task FailedNativeInvocationDoesNotStoreOrPublish()
    {
        var (memory, clock) = Services(); var provider = new Observer(memory, clock, new());
#pragma warning disable MAAI001
        await provider.InvokedAsync(new AIContextProvider.InvokedContext(Substitute.For<AIAgent>(), Substitute.For<AgentSession>(),
            [new ChatMessage(ChatRole.User, "failed turn")], new IOException("invoke failed")), CancellationToken.None);
#pragma warning restore MAAI001
        provider.Seen.Should().BeNull();
        await memory.DidNotReceive().AddMessageWithIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallbackCapturesApplicationAndReadOnlySourcesBeforeScopeChanges()
    {
        var (memory, clock) = Services(); var store = new TestStore { ApplicationId = "application-a" };
        var provider = new Observer(memory, clock, new() { AutoExtractOnPersist = false }, store);
        await Store(provider); store.ApplicationId = "application-b";
        provider.Seen!.ApplicationId.Should().Be("application-a");
        var mutate = () => ((IList<PersistedTurnMessage>)provider.Seen.Messages).Clear();
        mutate.Should().Throw<NotSupportedException>();
    }

    private static Task Store(Observer provider, CancellationToken token = default) => provider.PerformStoreAsync(
        [new(ChatRole.User, "input-user"), new(ChatRole.System, "never stored")], [new(ChatRole.Assistant, "input-assistant")], "s", "c", token, "owner");

    private static (IMemoryService Memory, IClock Clock) Services()
    {
        var memory = Substitute.For<IMemoryService>(); var clock = Substitute.For<IClock>(); clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        memory.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(call => new Message { MessageId = "stored-" + call.ArgAt<string>(2), SessionId = "s", ConversationId = "c", Role = call.ArgAt<string>(2), Content = "stored:" + call.ArgAt<string>(2), TimestampUtc = DateTimeOffset.UnixEpoch });
        memory.AddMessageWithIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(call => new Message { MessageId = "stored-" + call.ArgAt<string>(2), SessionId = "s", ConversationId = "c", Role = call.ArgAt<string>(2), Content = "stored:" + call.ArgAt<string>(2), TimestampUtc = DateTimeOffset.UnixEpoch });
        return (memory, clock);
    }

    private sealed class TestStore : IWritableMemoryStoreContext
    { public string? ApplicationId { get; set; } }

    private sealed class Observer(IMemoryService memory, IClock clock, AgentFrameworkOptions options, IMemoryStoreContext? store = null) : Neo4jMemoryContextProvider(
        memory, Substitute.For<IEmbeddingOrchestrator>(), clock, Substitute.For<IIdGenerator>(), Options.Create(new MemoryOptions()),
        Options.Create(new ContextFormatOptions()), Options.Create(options), NullLogger<Neo4jMemoryContextProvider>.Instance, storeContext: store)
    {
        public bool Enabled { get; init; } = true;
        public PersistedMemoryTurn? Seen { get; private set; }
        public Exception? Failure { get; init; }
        public Action? BeforeCallback { get; init; }
        protected override bool ObservesPersistedTurns => Enabled;
        protected override Task OnTurnPersistedAsync(PersistedMemoryTurn turn, CancellationToken cancellationToken = default)
        {
            BeforeCallback?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); Seen = turn;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }
}
