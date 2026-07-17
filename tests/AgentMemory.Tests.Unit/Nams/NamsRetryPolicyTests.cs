using System.Net;
using FluentAssertions;
using AgentMemory.Nams.Client;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsRetryPolicyTests
{
    private static readonly Uri BaseAddress = new("https://nams.test/");

    private static NamsRetryPolicy CreatePolicy(int maxAttempts = 3) =>
        new(maxAttempts, TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task Idempotent_TransientServerErrorThenSuccess_Retries()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, isIdempotent: true, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Idempotent_RetryExhaustion_ReturnsFinalFailureResponse()
    {
        var fake = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy(maxAttempts: 2).ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, isIdempotent: true, CancellationToken.None);

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
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, isIdempotent: true, CancellationToken.None);

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
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, isIdempotent: true, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Idempotent_PermanentFailure_DoesNotRetry()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, isIdempotent: true, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task NonIdempotent_TransientServerError_NeverRetries()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        using var response = await CreatePolicy().ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "x"), client, isIdempotent: false, CancellationToken.None);

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
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, isIdempotent: true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Idempotent_NetworkExceptionExhaustsRetries_Throws()
    {
        var fake = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("boom"));
        using var client = new HttpClient(fake) { BaseAddress = BaseAddress };

        var act = () => CreatePolicy(maxAttempts: 2).ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "x"), client, isIdempotent: true, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        fake.Requests.Should().HaveCount(3); // initial attempt + 2 retries
    }
}
