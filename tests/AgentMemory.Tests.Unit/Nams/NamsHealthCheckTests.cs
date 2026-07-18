using FluentAssertions;
using Microsoft.Extensions.Options;
using AgentMemory.Nams;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsHealthCheckTests
{
    private sealed class FakeNamsClient : ThrowingNamsClientStub
    {
        public Func<int, CancellationToken, Task<IReadOnlyList<NamsEntity>>>? OnListEntities { get; set; }

        public override Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (OnListEntities ?? ((_, _) => Task.FromResult<IReadOnlyList<NamsEntity>>([])))(limit, cancellationToken);
        }
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
