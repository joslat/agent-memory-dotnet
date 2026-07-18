using FluentAssertions;
using Microsoft.Extensions.Options;
using AgentMemory.Nams;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsHealthCheckTests
{
    private sealed class FakeNamsClient : INamsClient
    {
        public Func<int, CancellationToken, Task<IReadOnlyList<NamsEntity>>>? OnListEntities { get; set; }

        public Task<NamsConversation> CreateConversationAsync(
            string? userId, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
            string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
            string query, string? type, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (OnListEntities ?? ((_, _) => Task.FromResult<IReadOnlyList<NamsEntity>>([])))(limit, cancellationToken);
        }

        public Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
            string conversationId, string query, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsConversationSummary>> ListConversationsAsync(int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsObservation>> GetObservationsAsync(
            string conversationId, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsEntityFeedbackResult> SetEntityFeedbackAsync(
            string entityId, double? userScore, bool? confirmed, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsEntityGraph> GetEntityGraphAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsGraphExpansion> ExpandGraphAsync(
            string nodeId, IReadOnlyList<string> loadedIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsReasoningStep> RecordReasoningStepAsync(
            string conversationId, string reasoning, string actionTaken, string? result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NamsReasoningStep>> ListReasoningStepsAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsToolCall> RecordToolCallAsync(
            string? stepId, string toolName, string input, string? output, string? status, int? durationMs,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsReasoningTrace> GetReasoningTraceAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<NamsEntityProvenance> GetEntityProvenanceAsync(string entityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static NamsOptions ValidOptions() => new() { Endpoint = new Uri("https://nams.test/v1/"), ApiKey = "nams_key" };

    private static NamsHealthCheck CreateCheck(FakeNamsClient client, NamsOptions? options = null) =>
        new(client, Options.Create(options ?? ValidOptions()));

    [Fact]
    public async Task CheckAsync_MissingApiKey_ReturnsUnhealthyWithoutCallingClient()
    {
        var client = new FakeNamsClient { OnListEntities = (_, _) => throw new InvalidOperationException("should not be called") };
        var check = CreateCheck(client, new NamsOptions { Endpoint = new Uri("https://nams.test/v1/"), ApiKey = null });

        var result = await check.CheckAsync(CancellationToken.None);

        result.Status.Should().Be(NamsHealthStatus.Unhealthy);
        result.Latency.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_Success_ReturnsHealthy()
    {
        var client = new FakeNamsClient { OnListEntities = (_, _) => Task.FromResult<IReadOnlyList<NamsEntity>>([]) };
        var check = CreateCheck(client);

        var result = await check.CheckAsync(CancellationToken.None);

        result.Status.Should().Be(NamsHealthStatus.Healthy);
        result.Latency.Should().NotBeNull();
    }

    [Theory]
    [InlineData(nameof(NamsFailureKind.Authentication), NamsHealthStatus.Unhealthy)]
    [InlineData(nameof(NamsFailureKind.Authorization), NamsHealthStatus.Unhealthy)]
    [InlineData(nameof(NamsFailureKind.Network), NamsHealthStatus.Unhealthy)]
    [InlineData(nameof(NamsFailureKind.Timeout), NamsHealthStatus.Unhealthy)]
    [InlineData(nameof(NamsFailureKind.RateLimited), NamsHealthStatus.Degraded)]
    public async Task CheckAsync_FailureKind_MapsToExpectedStatus(string failureKindName, NamsHealthStatus expected)
    {
        var failureKind = Enum.Parse<NamsFailureKind>(failureKindName);
        var client = new FakeNamsClient
        {
            OnListEntities = (_, _) => throw new NamsOperationException(failureKind, "probe failed")
        };
        var check = CreateCheck(client);

        var result = await check.CheckAsync(CancellationToken.None);

        result.Status.Should().Be(expected);
        result.Latency.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckAsync_CallerCancellation_PropagatesOperationCanceledException()
    {
        var client = new FakeNamsClient();
        var check = CreateCheck(client);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => check.CheckAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
