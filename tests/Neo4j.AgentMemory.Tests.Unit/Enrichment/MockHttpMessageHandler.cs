using System.Net;
using System.Net.Http;
using System.Text;

namespace Neo4j.AgentMemory.Tests.Unit.Enrichment;

/// <summary>
/// Minimal HttpMessageHandler stub for unit testing HTTP-dependent services.
/// </summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;

    public HttpRequestMessage? LastRequest { get; private set; }

    public MockHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        : this(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        })
    {
    }

    public MockHttpMessageHandler(HttpResponseMessage response)
    {
        _response = response;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_response);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _response.Dispose();
        base.Dispose(disposing);
    }
}
