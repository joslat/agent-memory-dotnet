using AgentMemory.Inference;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Inference;

/// <summary>
/// A printed endpoint is scheme, host and port — and nothing that could be a credential.
/// </summary>
/// <remarks>
/// A URI has four places a secret turns up in the wild, and a gateway URL carrying a token in its
/// path is the common one: <c>https://gw.example/v1/sk-live-…</c> reads as an ordinary endpoint and
/// is a secret. Each of the four gets its own case, because a sanitizer that drops the query and
/// keeps the path is the kind of half-right that passes a single happy-path test.
/// </remarks>
public sealed class SafeEndpointTests
{
    private const string Token = "sk-live-do-not-print";

    /// <summary>A token in any of the four URI parts does not survive sanitisation.</summary>
    [Theory]
    [InlineData("user-info", "https://user:sk-live-do-not-print@api.example.com/v1")]
    [InlineData("path", "https://api.example.com/v1/sk-live-do-not-print")]
    [InlineData("query", "https://api.example.com/v1?key=sk-live-do-not-print")]
    [InlineData("fragment", "https://api.example.com/v1#sk-live-do-not-print")]
    public void NoUriPartLeaksATokenIntoThePrintedEndpoint(string part, string endpoint)
    {
        var safe = InferenceEndpoints.Sanitize(endpoint);

        safe.Should().NotContain(Token, $"a token in the {part} must not reach a log line");
        safe.Should().Be("https://api.example.com");
    }

    /// <summary>A non-default port is kept, because it identifies the host.</summary>
    [Fact]
    public void ANonDefaultPortSurvives()
    {
        InferenceEndpoints.Sanitize("http://localhost:11434/v1").Should().Be("http://localhost:11434");
    }

    /// <summary>An unparseable endpoint is not echoed back.</summary>
    /// <remarks>
    /// "I could not parse this" is not a reason to print it: the string that failed to parse is still
    /// operator input and may still be a secret.
    /// </remarks>
    [Fact]
    public void AnUnparseableEndpointIsNotEchoed()
    {
        InferenceEndpoints.Sanitize("not a uri " + Token).Should().NotContain(Token);
    }

    /// <summary>An unset endpoint says so rather than printing empty.</summary>
    [Fact]
    public void AnUnsetEndpointIsNamed()
    {
        InferenceEndpoints.Sanitize(null).Should().Be("(unset)");
    }

    /// <summary>The validator's refusal message is itself sanitised.</summary>
    /// <remarks>
    /// The obvious way to write a helpful error quotes the endpoint. That is exactly where a token in
    /// the path would end up in the logs of every host that refused to start.
    /// </remarks>
    [Fact]
    public void TheRefusalMessageDoesNotLeakTheEndpoint()
    {
        InferenceEndpoints
            .TryValidate($"http://remote-host/v1/{Token}", "OPENAI_COMPATIBLE_ENDPOINT", out var error)
            .Should().BeFalse();

        error.Should().NotBeNull().And.NotContain(Token);
        error.Should().Contain("OPENAI_COMPATIBLE_ENDPOINT", "the operator still has to know which one");
    }
}
