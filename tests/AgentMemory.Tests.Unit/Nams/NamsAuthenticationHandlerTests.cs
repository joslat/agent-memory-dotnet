using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using AgentMemory.Nams.Authentication;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsAuthenticationHandlerTests
{
    private sealed class FakeTokenProvider : INamsAccessTokenProvider
    {
        private int _generation;
        public List<string> InvalidatedFingerprints { get; } = [];

        public ValueTask<NamsAccessToken> GetTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new NamsAccessToken($"token-{_generation}"));

        public ValueTask InvalidateAsync(string rejectedTokenFingerprint, CancellationToken cancellationToken = default)
        {
            InvalidatedFingerprints.Add(rejectedTokenFingerprint);
            _generation++;
            return ValueTask.CompletedTask;
        }
    }

    private static HttpClient CreateClient(FakeHttpMessageHandler fake, FakeTokenProvider tokenProvider)
    {
        var authHandler = new NamsAuthenticationHandler(tokenProvider) { InnerHandler = fake };
        return new HttpClient(authHandler) { BaseAddress = new Uri("https://nams.test/") };
    }

    [Fact]
    public async Task AttachesBearerTokenFromProvider()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        var tokenProvider = new FakeTokenProvider();
        using var client = CreateClient(fake, tokenProvider);

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "x"));

        fake.Requests.Single().Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "token-0"));
    }

    [Fact]
    public async Task On401_InvalidatesAndRetriesOnceWithFreshToken()
    {
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        var tokenProvider = new FakeTokenProvider();
        using var client = CreateClient(fake, tokenProvider);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "x"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Should().HaveCount(2);
        fake.Requests[0].Headers.Authorization!.Parameter.Should().Be("token-0");
        fake.Requests[1].Headers.Authorization!.Parameter.Should().Be("token-1");
        tokenProvider.InvalidatedFingerprints.Should().HaveCount(1);
    }

    [Fact]
    public async Task On401Twice_StopsAfterOneRetry_ReturnsSecondFailure()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var tokenProvider = new FakeTokenProvider();
        using var client = CreateClient(fake, tokenProvider);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "x"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fake.Requests.Should().HaveCount(2); // one attempt + exactly one bounded retry, never loops
    }

    [Fact]
    public async Task On403_DoesNotInvalidateOrRetry()
    {
        var fake = new FakeHttpMessageHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var tokenProvider = new FakeTokenProvider();
        using var client = CreateClient(fake, tokenProvider);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "x"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fake.Requests.Should().HaveCount(1);
        tokenProvider.InvalidatedFingerprints.Should().BeEmpty();
    }

    [Fact]
    public async Task On401ForWriteWithBody_RetriesWithSameBody()
    {
        // Safe even for a write: a 401 means the server never authenticated the caller, so it never executed
        // the operation the first time.
        var fake = new FakeHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => new HttpResponseMessage(HttpStatusCode.Created));
        var tokenProvider = new FakeTokenProvider();
        using var client = CreateClient(fake, tokenProvider);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "x")
        {
            Content = new StringContent("{\"content\":\"hello\"}")
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        fake.Requests.Should().HaveCount(2);
        var secondBody = await fake.Requests[1].Content!.ReadAsStringAsync();
        secondBody.Should().Be("{\"content\":\"hello\"}");
    }
}
