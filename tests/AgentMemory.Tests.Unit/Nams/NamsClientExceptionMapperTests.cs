using System.Net;
using System.Text;
using FluentAssertions;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsClientExceptionMapperTests
{
    private static Task<int> DeserializeInt(Stream stream, CancellationToken cancellationToken) =>
        System.Text.Json.JsonSerializer.DeserializeAsync<int>(stream, cancellationToken: cancellationToken).AsTask();

    [Fact]
    public async Task MapResponseAsync_Success_ReturnsDeserializedValue()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("42") };

        var result = await NamsClientExceptionMapper.MapResponseAsync(response, DeserializeInt, secretToRedact: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    // InlineData values must be public/constant-friendly types -- NamsFailureKind is internal, so the expected
    // kind is passed by name and parsed inside the (necessarily public) theory method.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, nameof(NamsFailureKind.Validation))]
    [InlineData(HttpStatusCode.UnprocessableEntity, nameof(NamsFailureKind.Validation))]
    [InlineData(HttpStatusCode.Unauthorized, nameof(NamsFailureKind.Authentication))]
    [InlineData(HttpStatusCode.Forbidden, nameof(NamsFailureKind.Authorization))]
    [InlineData(HttpStatusCode.NotFound, nameof(NamsFailureKind.NotFound))]
    [InlineData(HttpStatusCode.TooManyRequests, nameof(NamsFailureKind.RateLimited))]
    [InlineData(HttpStatusCode.InternalServerError, nameof(NamsFailureKind.ServerError))]
    [InlineData(HttpStatusCode.BadGateway, nameof(NamsFailureKind.ServerError))]
    public async Task MapResponseAsync_ErrorStatusCode_ClassifiesFailureKind(HttpStatusCode statusCode, string expectedKindName)
    {
        var expectedKind = Enum.Parse<NamsFailureKind>(expectedKindName);
        using var response = new HttpResponseMessage(statusCode) { Content = new StringContent("{\"error\":\"nope\"}") };

        var result = await NamsClientExceptionMapper.MapResponseAsync(response, DeserializeInt, secretToRedact: null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(expectedKind);
        result.StatusCode.Should().Be((int)statusCode);
    }

    [Fact]
    public async Task MapResponseAsync_MalformedJson_ReturnsValidationFailure_DoesNotThrow()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not valid json") };

        var result = await NamsClientExceptionMapper.MapResponseAsync(response, DeserializeInt, secretToRedact: null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureKind.Should().Be(NamsFailureKind.Validation);
    }

    [Fact]
    public async Task MapResponseAsync_ErrorBodyContainsApiKey_RedactsIt()
    {
        const string secretApiKey = "nams_super_secret_12345";
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent($"{{\"error\":\"failed for key {secretApiKey}\"}}")
        };

        var result = await NamsClientExceptionMapper.MapResponseAsync(response, DeserializeInt, secretApiKey, CancellationToken.None);

        result.FailureMessage.Should().NotContain(secretApiKey);
        result.FailureMessage.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void FromTransportException_HttpRequestException_MapsToNetwork()
    {
        var exception = NamsClientExceptionMapper.FromTransportException(new HttpRequestException("boom"), secretToRedact: null);

        exception.FailureKind.Should().Be(NamsFailureKind.Network);
    }

    [Fact]
    public void FromTransportException_TaskCanceledException_MapsToTimeout()
    {
        var exception = NamsClientExceptionMapper.FromTransportException(new TaskCanceledException(), secretToRedact: null);

        exception.FailureKind.Should().Be(NamsFailureKind.Timeout);
    }

    [Fact]
    public void FromTransportException_RedactsApiKeyFromMessage()
    {
        const string secretApiKey = "nams_super_secret_12345";
        var exception = NamsClientExceptionMapper.FromTransportException(
            new HttpRequestException($"connection to host with key {secretApiKey} failed"), secretApiKey);

        exception.Message.Should().NotContain(secretApiKey);
    }

    [Fact]
    public async Task ToException_BuildsExceptionWithFailureKindAndStatusCode()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        var result = await NamsClientExceptionMapper.MapResponseAsync(response, DeserializeInt, secretToRedact: null, CancellationToken.None);

        var exception = NamsClientExceptionMapper.ToException(result);

        exception.FailureKind.Should().Be(NamsFailureKind.NotFound);
        exception.StatusCode.Should().Be(404);
    }
}
