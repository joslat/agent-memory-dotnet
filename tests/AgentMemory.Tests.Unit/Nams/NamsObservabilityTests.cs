using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Nams;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;
using AgentMemory.Nams.Persistence;
using AgentMemory.Nams.Recall;

namespace AgentMemory.Tests.Unit.Nams;

/// <summary>
/// Verifies NAMS backend operations actually record the Phase 9 metrics. Runs serialized (see
/// <see cref="NamsObservabilityCollection"/>) since every test here attaches a process-wide
/// <see cref="MeterListener"/> filtered by <see cref="NamsMetrics.MeterName"/>.
/// </summary>
[Collection("Nams Observability")]
public sealed class NamsObservabilityTests
{
    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Neo4jNamsClientAdapter CreateAdapter(FakeHttpMessageHandler fake, NamsMetrics metrics, int maxRetryAttempts = 2) =>
        new(new HttpClient(fake) { BaseAddress = new Uri("https://nams.test/v1/") },
            Options.Create(new NamsOptions
            {
                Endpoint = new Uri("https://nams.test/v1/"),
                ApiKey = "nams_key",
                MaxRetryAttempts = maxRetryAttempts,
                InitialRetryDelay = TimeSpan.FromMilliseconds(1)
            }),
            NullLogger<Neo4jNamsClientAdapter>.Instance, metrics);

    private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
            if (tag.Key == key) return tag.Value?.ToString();
        return null;
    }

    private static MeterListener StartListener(
        Action<Instrument, long, ReadOnlySpan<KeyValuePair<string, object?>>>? onLong = null,
        Action<Instrument, double, ReadOnlySpan<KeyValuePair<string, object?>>>? onDouble = null)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == NamsMetrics.MeterName) l.EnableMeasurementEvents(instrument); }
        };
        if (onLong is not null)
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => onLong(instrument, value, tags));
        if (onDouble is not null)
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => onDouble(instrument, value, tags));
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task GetContextAsync_Success_RecordsSucceededOperationAndDuration()
    {
        using var metrics = new NamsMetrics();
        var fake = new FakeHttpMessageHandler(() => Json(HttpStatusCode.OK, """{"reflections":[],"observations":[],"recentMessages":[]}"""));
        var adapter = CreateAdapter(fake, metrics);

        var operations = new List<(string? Status, string? Operation)>();
        double? duration = null;
        using var listener = StartListener(
            onLong: (i, value, tags) => { if (i.Name == "memory.backend.operations") operations.Add((Tag(tags, "status"), Tag(tags, "operation"))); },
            onDouble: (i, value, _) => { if (i.Name == "memory.backend.duration") duration = value; });

        await adapter.GetContextAsync("conv-1", CancellationToken.None);

        operations.Should().ContainSingle(o => o.Status == "succeeded" && o.Operation == "get_context");
        duration.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateConversationAsync_ServerError_RecordsFailedOperationAndFailureKind()
    {
        using var metrics = new NamsMetrics();
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var adapter = CreateAdapter(fake, metrics);

        var failures = new List<(string? Operation, string? FailureKind)>();
        using var listener = StartListener(onLong: (i, _, tags) =>
        {
            if (i.Name == "memory.backend.failures") failures.Add((Tag(tags, "operation"), Tag(tags, "failure_kind")));
        });

        var act = () => adapter.CreateConversationAsync("user-1", null, CancellationToken.None);
        await act.Should().ThrowAsync<NamsOperationException>();

        failures.Should().ContainSingle(f => f.Operation == "resolve_conversation" && f.FailureKind == nameof(NamsFailureKind.ServerError));
    }

    [Fact]
    public async Task SearchEntitiesAsync_RateLimitedExhaustsRetries_RecordsRateLimited()
    {
        using var metrics = new NamsMetrics();
        var fake = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var adapter = CreateAdapter(fake, metrics, maxRetryAttempts: 1);

        var rateLimitedCount = 0L;
        using var listener = StartListener(onLong: (i, value, _) =>
        {
            if (i.Name == "memory.backend.rate_limited") rateLimitedCount += value;
        });

        var act = () => adapter.SearchEntitiesAsync("acme", null, 10, CancellationToken.None);
        await act.Should().ThrowAsync<NamsOperationException>();

        rateLimitedCount.Should().Be(1); // one final classified outcome, not one per retried 429
    }

    [Fact]
    public async Task GetContextAsync_TransientFailureThenSuccess_RecordsOneRetry()
    {
        using var metrics = new NamsMetrics();
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => Json(HttpStatusCode.OK, """{"reflections":[],"observations":[],"recentMessages":[]}"""));
        var adapter = CreateAdapter(fake, metrics);

        var retryCount = 0L;
        using var listener = StartListener(onLong: (i, value, _) =>
        {
            if (i.Name == "memory.backend.retries") retryCount += value;
        });

        await adapter.GetContextAsync("conv-1", CancellationToken.None);

        retryCount.Should().Be(1);
    }

    [Fact]
    public async Task GetContextAsync_CallerCancellation_RecordsCancelledStatus_NotAFailure()
    {
        using var metrics = new NamsMetrics();
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        var adapter = CreateAdapter(fake, metrics);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var operations = new List<string?>();
        var failureCount = 0L;
        using var listener = StartListener(onLong: (i, value, tags) =>
        {
            if (i.Name == "memory.backend.operations") operations.Add(Tag(tags, "status"));
            if (i.Name == "memory.backend.failures") failureCount += value;
        });

        var act = () => adapter.GetContextAsync("conv-1", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        operations.Should().ContainSingle(s => s == "cancelled");
        failureCount.Should().Be(0, "cancellation must never be counted as a service failure");
    }

    [Fact]
    public async Task RecallAsync_NoTruncation_RecordsItemCount_DoesNotRecordTruncated()
    {
        var namsClient = new StubNamsClient
        {
            Context = new NamsContext([], [], [new NamsMessage("m1", "hi", "user", null, "conv-1", 1.0, null)])
        };
        using var metrics = new NamsMetrics();
        var service = new NamsRecallService(namsClient, Options.Create(new NamsRecallOptions { IncludeEntitySearch = false }),
            NullLogger<NamsRecallService>.Instance, metrics);

        long? itemCount = null;
        var truncatedCount = 0L;
        using var listener = StartListener(onLong: (i, value, tags) =>
        {
            if (i.Name == "memory.backend.context_items") itemCount = value;
            if (i.Name == "memory.backend.context_truncated") truncatedCount += value;
        });

        await service.RecallAsync("conv-1", null, CancellationToken.None);

        itemCount.Should().Be(1);
        truncatedCount.Should().Be(0);
    }

    [Fact]
    public async Task RecallAsync_BudgetTruncates_RecordsContextTruncated()
    {
        var namsClient = new StubNamsClient
        {
            Context = new NamsContext([], [],
                [new NamsMessage("m1", "a much longer message than the tiny budget allows", "user", null, "conv-1", 1.0, null)])
        };
        using var metrics = new NamsMetrics();
        var service = new NamsRecallService(
            namsClient, Options.Create(new NamsRecallOptions { IncludeEntitySearch = false, MaxTotalCharacters = 1 }),
            NullLogger<NamsRecallService>.Instance, metrics);

        var truncatedCount = 0L;
        using var listener = StartListener(onLong: (i, value, _) =>
        {
            if (i.Name == "memory.backend.context_truncated") truncatedCount += value;
        });

        await service.RecallAsync("conv-1", null, CancellationToken.None);

        truncatedCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistTurnAsync_NetworkFailure_RecordsUnknownWriteOutcome()
    {
        var namsClient = new StubNamsClient
        {
            OnAddMessages = _ => throw new NamsOperationException(NamsFailureKind.Network, "connection reset")
        };
        using var metrics = new NamsMetrics();
        var service = new NamsPersistenceService(
            namsClient,
            Options.Create(new NamsOptions { Endpoint = new Uri("https://nams.test/v1/"), ApiKey = "nams_key" }),
            NullLogger<NamsPersistenceService>.Instance, metrics);

        var unknownCount = 0L;
        using var listener = StartListener(onLong: (i, value, _) =>
        {
            if (i.Name == "memory.backend.unknown_write_outcomes") unknownCount += value;
        });

        await service.PersistTurnAsync("conv-1", [new NamsMessageToPersist("hi")], [], CancellationToken.None);

        unknownCount.Should().Be(1);
    }

    /// <summary>Minimal <see cref="INamsClient"/> stub shared by the recall/persistence metric tests above --
    /// deliberately separate from the other test files' own fakes to avoid coupling this file's setup to
    /// their unrelated test-specific configuration knobs.</summary>
    private sealed class StubNamsClient : ThrowingNamsClientStub
    {
        public NamsContext Context { get; set; } = new([], [], []);
        public Func<IReadOnlyList<NamsMessageInput>, Task<IReadOnlyList<NamsMessage>>>? OnAddMessages { get; set; }

        public override Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(Context);

        public override Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
            string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken) =>
            (OnAddMessages ?? (msgs => Task.FromResult<IReadOnlyList<NamsMessage>>(
                msgs.Select((m, i) => new NamsMessage($"m{i}", m.Content, m.Role, null, conversationId, null, null)).ToList())))(messages);

        public override Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
            string query, string? type, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NamsEntity>>([]);
    }
}
