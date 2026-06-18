using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using AgentMemory.Enrichment;
using AgentMemory.Enrichment.Http;

namespace AgentMemory.Tests.Unit.Enrichment;

public sealed class RetryHttpMessageHandlerTests
{
    private static readonly TimeSpan FastDelay = TimeSpan.FromMilliseconds(1);

    /// <summary>Inner handler that replays a fixed sequence of responses/throws and counts invocations.</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps;
        public int Calls { get; private set; }

        public SequenceHandler(params Func<HttpResponseMessage>[] steps) => _steps = new Queue<Func<HttpResponseMessage>>(steps);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (_steps.Count == 0) throw new InvalidOperationException("SequenceHandler ran out of steps.");
            return _steps.Dequeue()(); // the step may itself throw
        }
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode code) => () => new HttpResponseMessage(code);
    private static Func<HttpResponseMessage> Throws(Exception ex) => () => throw ex;

    private static HttpClient ClientWith(int maxRetries, SequenceHandler inner) =>
        new(new RetryHttpMessageHandler(maxRetries, logger: null, baseDelay: FastDelay) { InnerHandler = inner });

    [Fact]
    public async Task RetriesTransientStatus_ThenSucceeds()
    {
        var inner = new SequenceHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.OK));
        var client = ClientWith(maxRetries: 2, inner);

        var response = await client.GetAsync("https://example.test/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(3); // initial + 2 retries
    }

    [Fact]
    public async Task StopsAfterMaxRetries_ReturnsLastTransientResponse()
    {
        var inner = new SequenceHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable));
        var client = ClientWith(maxRetries: 2, inner);

        var response = await client.GetAsync("https://example.test/");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        inner.Calls.Should().Be(3); // initial + 2 retries, then gives up
    }

    [Fact]
    public async Task RetriesHttpRequestException_ThenSucceeds()
    {
        var inner = new SequenceHandler(
            Throws(new HttpRequestException("connection reset")),
            Status(HttpStatusCode.OK));
        var client = ClientWith(maxRetries: 2, inner);

        var response = await client.GetAsync("https://example.test/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task DoesNotRetry_OnSuccess()
    {
        var inner = new SequenceHandler(Status(HttpStatusCode.OK));
        var client = ClientWith(maxRetries: 3, inner);

        var response = await client.GetAsync("https://example.test/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DoesNotRetry_OnNonTransientStatus()
    {
        var inner = new SequenceHandler(Status(HttpStatusCode.NotFound));
        var client = ClientWith(maxRetries: 3, inner);

        var response = await client.GetAsync("https://example.test/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task MaxRetriesZero_NeverRetries()
    {
        var inner = new SequenceHandler(Status(HttpStatusCode.ServiceUnavailable));
        var client = ClientWith(maxRetries: 0, inner);

        var response = await client.GetAsync("https://example.test/");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CallerCancellation_IsNotRetried_AndPropagates()
    {
        var inner = new SequenceHandler(Status(HttpStatusCode.OK)); // would succeed if reached
        var client = ClientWith(maxRetries: 3, inner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => client.GetAsync("https://example.test/", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1); // the single attempt observed the canceled token; no retry loop
    }

    [Fact]
    public async Task TooManyRequests_IsTreatedAsTransient()
    {
        var inner = new SequenceHandler(
            Status(HttpStatusCode.TooManyRequests),
            Status(HttpStatusCode.OK));
        var client = ClientWith(maxRetries: 1, inner);

        var response = await client.GetAsync("https://example.test/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AddEnrichmentServices_WiresRetryHandlerOnNominatimClient_HonoringMaxRetries()
    {
        // Proves the fix end-to-end: the named Nominatim client's pipeline includes the retry handler
        // reading MaxRetries from GeocodingOptions. Without the handler the first 503 would be returned
        // and the stub called once; the single retry (→ 2 calls) is what demonstrates the wiring.
        var stub = new SequenceHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.OK));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEnrichmentServices(configureGeocoding: o =>
        {
            o.MaxRetries = 2;
            o.UserAgent = "test/1.0";
            o.BaseUrl = "https://nominatim.test";
        });
        // Swap the innermost handler so no real network call is made; the retry handler still wraps it.
        services.AddHttpClient(NominatimGeocodingService.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(NominatimGeocodingService.ClientName);

        var response = await client.GetAsync("https://nominatim.test/search");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Calls.Should().Be(2); // initial 503 + one retry → the retry handler is wired into the pipeline
    }
}
