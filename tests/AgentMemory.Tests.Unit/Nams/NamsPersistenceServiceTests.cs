using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Nams;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;
using AgentMemory.Nams.Persistence;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsPersistenceServiceTests
{
    private sealed class FakeNamsClient : INamsClient
    {
        public List<IReadOnlyList<NamsMessageInput>> Calls { get; } = [];
        public Func<IReadOnlyList<NamsMessageInput>, Task<IReadOnlyList<NamsMessage>>>? OnAddMessages { get; set; }

        public Task<NamsConversation> CreateConversationAsync(
            string? userId, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
            string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(messages);
            return (OnAddMessages ?? (msgs => Task.FromResult<IReadOnlyList<NamsMessage>>(
                msgs.Select((m, i) => new NamsMessage($"m{i}", m.Content, m.Role, null, conversationId, null, null)).ToList())))(messages);
        }

        public Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
            string query, string? type, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
            string conversationId, string query, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static NamsPersistenceService CreateService(
        FakeNamsClient client, NamsPersistenceFailureMode mode = NamsPersistenceFailureMode.BestEffort) =>
        new(client, Options.Create(new NamsOptions
        {
            Endpoint = new Uri("https://nams.test/v1/"),
            ApiKey = "nams_key",
            PersistenceFailureMode = mode
        }), NullLogger<NamsPersistenceService>.Instance, new NamsMetrics());

    [Fact]
    public async Task PersistTurnAsync_BothListsEmpty_ReturnsPersistedWithNoIds_DoesNotCallClient()
    {
        var client = new FakeNamsClient();
        var service = CreateService(client);

        var result = await service.PersistTurnAsync("conv-1", [], [], CancellationToken.None);

        result.Outcome.Should().Be(NamsPersistenceOutcome.Persisted);
        result.PersistedMessageIds.Should().BeEmpty();
        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PersistTurnAsync_UserAndAssistantMessages_SubmittedInOrder_UserFirst_CorrectRoles()
    {
        var client = new FakeNamsClient();
        var service = CreateService(client);

        await service.PersistTurnAsync(
            "conv-1",
            userMessages: [new NamsMessageToPersist("hi"), new NamsMessageToPersist("how are you")],
            assistantMessages: [new NamsMessageToPersist("hello there")],
            CancellationToken.None);

        var submitted = client.Calls.Single();
        submitted.Select(m => (m.Content, m.Role)).Should().Equal(
            ("hi", "user"), ("how are you", "user"), ("hello there", "assistant"));
    }

    [Fact]
    public async Task PersistTurnAsync_Success_ReturnsPersistedOutcomeWithMessageIds()
    {
        var client = new FakeNamsClient();
        var service = CreateService(client);

        var result = await service.PersistTurnAsync(
            "conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        result.Outcome.Should().Be(NamsPersistenceOutcome.Persisted);
        result.PersistedMessageIds.Should().ContainSingle();
    }

    [Fact]
    public async Task PersistTurnAsync_CalledExactlyOnce_NeverRetries()
    {
        var client = new FakeNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(NamsFailureKind.ServerError, "boom")
        };
        var service = CreateService(client);

        await service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        client.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task PersistTurnAsync_NetworkFailure_ReturnsUnknownWriteOutcome_BestEffort_DoesNotThrow()
    {
        var client = new FakeNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(NamsFailureKind.Network, "connection lost")
        };
        var service = CreateService(client);

        var result = await service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        result.Outcome.Should().Be(NamsPersistenceOutcome.UnknownWriteOutcome);
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PersistTurnAsync_TimeoutFailure_ReturnsUnknownWriteOutcome()
    {
        var client = new FakeNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(NamsFailureKind.Timeout, "timed out")
        };
        var service = CreateService(client);

        var result = await service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        result.Outcome.Should().Be(NamsPersistenceOutcome.UnknownWriteOutcome);
    }

    [Theory]
    [InlineData(nameof(NamsFailureKind.Validation))]
    [InlineData(nameof(NamsFailureKind.NotFound))]
    [InlineData(nameof(NamsFailureKind.RateLimited))]
    [InlineData(nameof(NamsFailureKind.ServerError))]
    public async Task PersistTurnAsync_DefinitiveRejection_ReturnsFailedOutcome_NotUnknown(string failureKindName)
    {
        var failureKind = Enum.Parse<NamsFailureKind>(failureKindName);
        var client = new FakeNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(failureKind, "rejected")
        };
        var service = CreateService(client);

        var result = await service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        result.Outcome.Should().Be(NamsPersistenceOutcome.Failed);
    }

    [Theory]
    [InlineData(nameof(NamsFailureKind.Authentication))]
    [InlineData(nameof(NamsFailureKind.Authorization))]
    public async Task PersistTurnAsync_IdentitySecurityFailure_Propagates_RegardlessOfFailureMode(string failureKindName)
    {
        var failureKind = Enum.Parse<NamsFailureKind>(failureKindName);
        var client = new FakeNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(failureKind, "denied")
        };
        var service = CreateService(client, NamsPersistenceFailureMode.BestEffort);

        var act = () => service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        await act.Should().ThrowAsync<NamsOperationException>();
    }

    [Fact]
    public async Task PersistTurnAsync_Cancellation_Propagates()
    {
        var client = new FakeNamsClient();
        var service = CreateService(client);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PersistTurnAsync_FailInvocationMode_FailedOutcome_ThrowsPersistenceFailedException()
    {
        var client = new FakeNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(NamsFailureKind.ServerError, "boom")
        };
        var service = CreateService(client, NamsPersistenceFailureMode.FailInvocation);

        var act = () => service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsPersistenceFailedException>();
        exception.Which.Result.Outcome.Should().Be(NamsPersistenceOutcome.Failed);
    }

    [Fact]
    public async Task PersistTurnAsync_FailInvocationMode_UnknownWriteOutcome_ThrowsPersistenceFailedException()
    {
        var client = new FakeNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(NamsFailureKind.Network, "lost")
        };
        var service = CreateService(client, NamsPersistenceFailureMode.FailInvocation);

        var act = () => service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NamsPersistenceFailedException>();
        exception.Which.Result.Outcome.Should().Be(NamsPersistenceOutcome.UnknownWriteOutcome);
    }

    [Fact]
    public async Task PersistTurnAsync_FailInvocationMode_Success_DoesNotThrow()
    {
        var client = new FakeNamsClient();
        var service = CreateService(client, NamsPersistenceFailureMode.FailInvocation);

        var act = () => service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PersistTurnAsync_FailureReason_NeverContainsApiKey()
    {
        const string apiKey = "nams_super_secret_12345";
        var client = new FakeNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(NamsFailureKind.ServerError, $"failed for key {apiKey}")
        };
        var service = new NamsPersistenceService(client, Options.Create(new NamsOptions
        {
            Endpoint = new Uri("https://nams.test/v1/"),
            ApiKey = apiKey
        }), NullLogger<NamsPersistenceService>.Instance, new NamsMetrics());

        var result = await service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        result.FailureReason.Should().NotBeNullOrEmpty();
        result.FailureReason.Should().NotContain(apiKey);
    }
}
