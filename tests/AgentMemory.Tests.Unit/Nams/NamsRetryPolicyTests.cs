using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using AgentMemory.Nams.Client;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsRetryPolicyTests
{
    private static readonly Uri BaseAddress = new("https://nams.test/");

    private static NamsRetryPolicy CreatePolicy(int maxAttempts = 3) =>
        new(maxAttempts, TimeSpan.FromMilliseconds(1));

    /// <summary>Captures every exception object passed to Log(...), so a test can inspect what a real
    /// logger would have rendered via Exception.ToString() -- redaction bugs only show up here, not in the
    /// message template string itself.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<Exception> LoggedExceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                LoggedExceptions.Add(exception);
        }
    }

    [Fact]
    public async Task Idempotent_TransientServerErrorThenSuccess_Retries()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Idempotent_RetryExhaustion_ReturnsFinalFailureResponse()
    {
        var fake = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy(maxAttempts: 2).ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.Requests.Should().HaveCount(3); // initial attempt + 2 retries
    }

    [Fact]
    public async Task Idempotent_RateLimitedWithRetryAfterHeader_RetriesThenSucceeds()
    {
        var fake = new FakeHttpMessageHandler(
            () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            },
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Idempotent_RateLimitedWithRetryAfterDateForm_RetriesThenSucceeds()
    {
        // Retry-After has two legal forms (RFC 9110 §10.2.3): delta-seconds or an absolute HTTP-date.
        // RetryConditionHeaderValue exposes these as mutually-exclusive Delta/Date properties -- this covers
        // the Date form, which reading only .Delta would silently ignore in favor of exponential backoff.
        var fake = new FakeHttpMessageHandler(
            () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow);
                return response;
            },
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Idempotent_PermanentFailure_DoesNotRetry()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task NonIdempotent_TransientServerError_NeverRetries()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "x"), client, retryEligibility: NamsRetryEligibility.NonIdempotent, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesWithoutRetry()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Idempotent_TransientServerErrorThenSuccess_InvokesOnRetryOnceForTheOneRetry()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };
        var retryCount = 0;

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None,
            onRetry: () => retryCount++);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        retryCount.Should().Be(1);
    }

    [Fact]
    public async Task PermanentFailure_NeverInvokesOnRetry()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };
        var retryCount = 0;

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None,
            onRetry: () => retryCount++);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        retryCount.Should().Be(0);
    }

    [Fact]
    public async Task Idempotent_NetworkExceptionExhaustsRetries_Throws()
    {
        var fake = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("boom"));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        var act = () => CreatePolicy(maxAttempts: 2).ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        fake.Requests.Should().HaveCount(3); // initial attempt + 2 retries
    }

    [Fact]
    public async Task Idempotent_TransientNetworkExceptionLogged_RedactsApiKeyFromLoggedException()
    {
        // Regression test: the transient-failure LogDebug call passed the raw caught exception straight to
        // the logger -- a real ILogger provider renders it via Exception.ToString(), which would leak the API
        // key if it ever appeared in the exception's own message (the same threat model
        // NamsClientExceptionMapper's redaction already guards against; this call site bypassed it entirely).
        const string apiKey = "nams_super_secret_12345";
        var logger = new CapturingLogger();
        var fake = new FakeHttpMessageHandler(
            (_, _) => throw new HttpRequestException($"connection refused for key {apiKey}"));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };
        var policy = new NamsRetryPolicy(maxAttempts: 1, TimeSpan.FromMilliseconds(1), logger, secretToRedact: apiKey);

        var act = () => policy.ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, retryEligibility: NamsRetryEligibility.Idempotent, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        logger.LoggedExceptions.Should().NotBeEmpty();
        logger.LoggedExceptions.Should().OnlyContain(ex => !ex.ToString().Contains(apiKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1000)]
    public void BackoffFor_NeverExceedsMaxBackoff_EvenForALargeAttemptCount(int attempt)
    {
        // Regression test: BackoffFor's exponential formula had no ceiling -- NamsOptionValidator only
        // enforces MaxRetryAttempts >= 0, no upper bound, so a misconfigured large attempt count would grow
        // past Task.Delay's ~49.7-day argument limit (ArgumentOutOfRangeException) or overflow
        // TimeSpan.FromMilliseconds (OverflowException), both escaping this class's own NamsOperationException
        // contract. attempt=1000 alone would previously throw when constructing the TimeSpan; now it must
        // just return the cap.
        var delay = CreatePolicy().BackoffFor(attempt);

        delay.Should().BeLessThanOrEqualTo(NamsRetryPolicy.MaxBackoff);
    }

    [Fact]
    public void BackoffFor_ZeroBaseDelayWithExtremeAttempt_DoesNotProduceNaN()
    {
        // Regression test: baseDelay has no validation floor (unlike maxAttempts) -- a zero baseDelay
        // combined with an attempt large enough that Math.Pow(2, attempt) itself overflows to +Infinity
        // produces 0 * Infinity = NaN, which an uncapped ">=" comparison alone does not catch (every
        // comparison against NaN is false). TimeSpan.FromMilliseconds(NaN) would previously throw
        // ArgumentException -- the same class of bug this whole fix exists to prevent.
        var policy = new NamsRetryPolicy(maxAttempts: 3, TimeSpan.Zero);

        var delay = policy.BackoffFor(2000);

        delay.Should().Be(NamsRetryPolicy.MaxBackoff);
    }

    [Fact]
    public void RetryDelayFor_AbsurdRetryAfterDeltaHeader_IsCapped()
    {
        // Regression test: the Retry-After header path returned the server-supplied delay directly, never
        // routing through BackoffFor/MaxBackoff -- a misconfigured or hostile server sending an absurd
        // Retry-After (e.g. many days out) would bypass the cap entirely, even though the raw value itself
        // doesn't overflow anything.
        var policy = CreatePolicy();
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(1));

        var delay = policy.RetryDelayFor(response, attempt: 0);

        delay.Should().Be(NamsRetryPolicy.MaxBackoff);
    }

    [Fact]
    public void RetryDelayFor_NegativeRetryAfterDeltaHeader_IsFlooredToZero_NotPassedNegativeToTaskDelay()
    {
        // Regression test: RFC 9110 forbids a negative delta-seconds value, but RetryConditionHeaderValue's
        // own constructor doesn't enforce that -- a non-conformant/hostile server sending one would otherwise
        // reach Task.Delay with a negative TimeSpan, which throws ArgumentOutOfRangeException.
        var policy = CreatePolicy();
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(-5));

        var delay = policy.RetryDelayFor(response, attempt: 0);

        delay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void RetryDelayFor_AbsurdRetryAfterDateHeader_IsCapped()
    {
        var policy = CreatePolicy();
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddDays(1));

        var delay = policy.RetryDelayFor(response, attempt: 0);

        delay.Should().Be(NamsRetryPolicy.MaxBackoff);
    }
}
