using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The documented bulk path and its backpressure (rank 27).
/// </summary>
/// <remarks>
/// <para>
/// Loading a backlog was always possible, and both obvious ways are wrong in opposite directions: a
/// serial loop wastes hours, and an unbounded <c>Parallel.ForEachAsync</c> saturates the provider
/// quota and the connection pool — degrading p99 for every other tenant in the process while median
/// latency looks untouched.
/// </para>
/// <para>
/// So the tests are about the two things a bulk API is usually careless with: how many calls it makes
/// at once, and what it tells you when some of them fail.
/// </para>
/// </remarks>
public sealed class BulkIngestionTests
{
    /// <summary>Records concurrency and fails on demand; nothing else about ingestion is exercised.</summary>
    private sealed class RecordingIngestion : IMemoryIngestion
    {
        private int _inFlight;
        private readonly Func<int, bool>? _failOn;
        private readonly TimeSpan _delay;

        public RecordingIngestion(Func<int, bool>? failOn = null, TimeSpan? delay = null)
        {
            _failOn = failOn;
            _delay = delay ?? TimeSpan.FromMilliseconds(20);
        }

        public int PeakConcurrency;
        public readonly List<string> Seen = [];
        private readonly Lock _sync = new();

        public async Task<ExtractionResult> ExtractAndPersistAsync(
            ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref PeakConcurrency, now);
            try
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                lock (_sync) Seen.Add(request.SessionId);

                var index = int.Parse(request.SessionId.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture);
                if (_failOn?.Invoke(index) == true) throw new InvalidOperationException($"boom {index}");
                return new ExtractionResult();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value
                   && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }

        public Task<Message> AddMessageAsync(
            string sessionId, string conversationId, string role, string content,
            IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Message> AddMessageWithIdAsync(
            string messageId, string sessionId, string conversationId, string role, string content,
            IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Message>> AddMessagesAsync(
            IEnumerable<Message> messages, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ExtractFromSessionAsync(
            string sessionId, string? userId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ExtractFromConversationAsync(
            string conversationId, string? userId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static IReadOnlyList<ExtractionRequest> Requests(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ExtractionRequest { Messages = [], SessionId = $"s-{i}" })
            .ToList();

    [Fact]
    public async Task ConcurrencyIsBounded()
    {
        // THE test. Everything else here is about reporting; this is the property the feature exists
        // for, and the one an unbounded Parallel.ForEachAsync gets wrong.
        var ingestion = new RecordingIngestion();

        // Through the interface: a default interface method is not inherited into the implementing type.
        await ((IMemoryIngestion)ingestion).IngestBulkAsync(Requests(20), new BulkIngestionOptions { MaxConcurrency = 3 });

        ingestion.PeakConcurrency.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task EveryRequestIsIngested()
    {
        var ingestion = new RecordingIngestion();

        var result = await ((IMemoryIngestion)ingestion).IngestBulkAsync(Requests(12), new BulkIngestionOptions { MaxConcurrency = 4 });

        result.SucceededCount.Should().Be(12);
        ingestion.Seen.Should().HaveCount(12);
    }

    [Fact]
    public async Task AFailureIsReportedAgainstItsInputRatherThanThrown()
    {
        // A bulk API that returns "8,412 of 10,000 succeeded" tells the caller they have a problem and
        // nothing about which inputs to retry, so the realistic response is to re-run everything --
        // worse than the failure itself.
        var ingestion = new RecordingIngestion(failOn: i => i == 3);

        var result = await ((IMemoryIngestion)ingestion).IngestBulkAsync(Requests(6));

        result.FailedCount.Should().Be(1);
        var failure = result.Outcomes.Single(o => !o.Succeeded);
        failure.Index.Should().Be(3);
        failure.Error.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task OneBadInputDoesNotAbortTheRunByDefault()
    {
        // In a ten-thousand-conversation load, one malformed transcript stopping the other 9,999 is
        // rarely what the caller wanted -- and continuing is not ignoring, because the failure is
        // returned.
        var ingestion = new RecordingIngestion(failOn: i => i == 0);

        var result = await ((IMemoryIngestion)ingestion).IngestBulkAsync(Requests(8), new BulkIngestionOptions { MaxConcurrency = 2 });

        result.SucceededCount.Should().Be(7);
        result.FailedCount.Should().Be(1);
    }

    [Fact]
    public async Task StopOnErrorLeavesTheRestUnattemptedRatherThanFailed()
    {
        // "Tried and failed" and "never tried" call for different actions: re-running a request that
        // was never attempted is always correct, re-running a failed one may not be. Collapsing them
        // makes a stopped run look like a partially broken corpus.
        var ingestion = new RecordingIngestion(failOn: i => i == 0, delay: TimeSpan.FromMilliseconds(50));

        var result = await ((IMemoryIngestion)ingestion).IngestBulkAsync(
            Requests(20), new BulkIngestionOptions { MaxConcurrency = 1, ContinueOnError = false });

        result.FailedCount.Should().Be(1);
        result.NotAttemptedCount.Should().BeGreaterThan(0);
        (result.SucceededCount + result.FailedCount + result.NotAttemptedCount).Should().Be(20);
    }

    [Fact]
    public async Task AnOuterCancellationStillThrows()
    {
        // A stop-on-error run completes normally -- the failure is in the outcomes. A cancellation
        // the CALLER requested is a different thing and must not be swallowed into a tidy report.
        using var cts = new CancellationTokenSource();
        var ingestion = new RecordingIngestion(delay: TimeSpan.FromMilliseconds(200));

        var run = ((IMemoryIngestion)ingestion).IngestBulkAsync(
            Requests(20), new BulkIngestionOptions { MaxConcurrency = 2 }, cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AnEmptyRunIsNotAnError() =>
        (await ((IMemoryIngestion)new RecordingIngestion()).IngestBulkAsync([])).Outcomes.Should().BeEmpty();

    [Fact]
    public async Task ZeroConcurrencyIsRejected()
    {
        // Not silently treated as unbounded, which is precisely the behaviour this exists to prevent.
        var act = async () => await ((IMemoryIngestion)new RecordingIngestion())
            .IngestBulkAsync(Requests(1), new BulkIngestionOptions { MaxConcurrency = 0 });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TheDefaultsAreConservative()
    {
        // A caller who never tunes this must not saturate a shared provider quota by accident.
        var options = new BulkIngestionOptions();

        options.MaxConcurrency.Should().Be(4);
        options.ContinueOnError.Should().BeTrue();
    }
}
