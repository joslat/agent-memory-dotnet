using System.Net;
using AgentMemory.Gate;
using AgentMemory.RouterArena.Ask;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class CompoundHttpMeterTests
{
    [Theory]
    [InlineData("{\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}")]
    [InlineData("{\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}")]
    public async Task Complete_reported_usage_is_counted_without_consuming_the_response(string body)
    {
        using var meter = new CompoundHttpMeter(new Handler(_ => Reply(HttpStatusCode.OK, body)));
        using var http = new HttpClient(meter);

        using var response = await http.GetAsync("https://model.test/endpoint");

        (await response.Content.ReadAsStringAsync()).Should().Be(body);
        meter.Snapshot().Should().Be(new CompoundRequestCounts(1, 0, 0, 1, 12, 3));
    }

    [Fact]
    public async Task Missing_usage_stays_unknown_and_http_failures_count_as_attempts()
    {
        using var meter = new CompoundHttpMeter(new Handler(_ => Reply(HttpStatusCode.ServiceUnavailable, "{}")));
        using var http = new HttpClient(meter);

        using var response = await http.GetAsync("https://model.test/endpoint");

        meter.Snapshot().Should().Be(new CompoundRequestCounts(1, 1, 0, 0, null, null));
    }

    [Fact]
    public async Task Transport_exception_is_counted_even_without_a_response()
    {
        using var meter = new CompoundHttpMeter(new Handler(_ => throw new HttpRequestException("unavailable")));
        using var http = new HttpClient(meter);

        await FluentActions.Awaiting(() => http.GetAsync("https://model.test/endpoint")).Should().ThrowAsync<HttpRequestException>();

        meter.Snapshot().Should().Be(new CompoundRequestCounts(1, 1, 0, 0, null, null));
    }

    [Fact]
    public async Task The_real_System_One_retry_counts_as_a_second_physical_request()
    {
        var attempts = 0;
        using var meter = new CompoundHttpMeter(new Handler(_ => ++attempts == 1
            ? Reply(HttpStatusCode.ServiceUnavailable, "{}")
            : Reply(HttpStatusCode.OK, "{\"answers\":{\"compound\":{\"noul\":0.9}}}")));
        using var http = new HttpClient(meter);
        var client = new SystemOneClient(http);

        var result = await client.AskAsync(new SystemOneEndpoint { Endpoint = new Uri("https://model.test/v1/systemone") },
            new { question = "test" }, new Dictionary<string, AgentMemory.Gate.YesNo> { ["compound"] = new("test", "yes", "no") },
            new MemoryGateOptions(), default);

        result["compound"].Should().Be(.9);
        meter.Snapshot().Should().Be(new CompoundRequestCounts(2, 1, 0, 0, null, null));
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }
}
