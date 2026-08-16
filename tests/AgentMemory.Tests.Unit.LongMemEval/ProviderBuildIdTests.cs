using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Recovering the provider's backend build id from a response (S-4).
/// </summary>
/// <remarks>
/// <para>
/// The value exists to answer one question: <i>were these two runs ever comparable?</i> This deployment
/// rejects <c>temperature: 0</c>, so extraction is nondeterministic and the provider's determinism
/// guarantee holds only while its backend build is unchanged. A deployment name pins the model; it does
/// not pin the build.
/// </para>
/// <para>
/// Provider-free by construction: these assert against constructed responses, which is the whole of what
/// can be quietly wrong here.
/// </para>
/// </remarks>
public sealed class ProviderBuildIdTests
{
    /// <summary>A raw provider response shaped like the OpenAI client's <c>ChatCompletion</c>.</summary>
    private sealed class RawWithFingerprint
    {
        public string SystemFingerprint { get; init; } = "fp_raw_9911";
    }

    private sealed class RawThatThrows
    {
        public string SystemFingerprint => throw new NotSupportedException("provider refuses reflection");
    }

    private static ChatResponse Response(
        object? raw = null, params (string Key, object? Value)[] additional)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"))
        {
            RawRepresentation = raw
        };
        if (additional.Length > 0)
        {
            response.AdditionalProperties = new AdditionalPropertiesDictionary();
            foreach (var (key, value) in additional)
                response.AdditionalProperties[key] = value;
        }

        return response;
    }

    [Fact]
    public void ReadsTheFingerprintFromTheAdditionalPropertiesBag()
    {
        ProviderBuildId.FromChatResponse(Response(additional: ("system_fingerprint", "fp_44a1")))
            .Should().Be("fp_44a1");
    }

    [Fact]
    public void FallsBackToTheProvidersRawResponseObject()
    {
        // ChatResponse has no SystemFingerprint member, so for the OpenAI client the value only exists
        // on the provider's own ChatCompletion. Missing this path means reporting "no build id" on every
        // real run.
        ProviderBuildId.FromChatResponse(Response(raw: new RawWithFingerprint()))
            .Should().Be("fp_raw_9911");
    }

    [Fact]
    public void PrefersTheBagOverTheRawObject()
    {
        ProviderBuildId.FromChatResponse(
                Response(raw: new RawWithFingerprint(), additional: ("system_fingerprint", "fp_bag")))
            .Should().Be("fp_bag");
    }

    [Fact]
    public void AbsenceIsNullAndNeverAPlaceholder()
    {
        // THE property. "The provider did not report a build" and "the build was X" are different facts,
        // and a sentinel would let a report deny an incomparability it cannot actually rule out.
        ProviderBuildId.FromChatResponse(Response()).Should().BeNull();
        ProviderBuildId.FromChatResponse(null).Should().BeNull();
        ProviderBuildId.FromChatResponse(Response(additional: ("system_fingerprint", "   ")))
            .Should().BeNull();
    }

    [Fact]
    public void AnImplausiblyLongValueIsRejectedRatherThanCopiedIntoAReport()
    {
        ProviderBuildId.FromChatResponse(
                Response(additional: ("system_fingerprint", new string('x', 500))))
            .Should().BeNull("providers return short opaque tokens; anything else is not a build id");
    }

    [Fact]
    public void AProviderObjectThatRefusesReflectionReportsNoBuildRatherThanThrowing()
    {
        // Telemetry must never abort a measured run. A provider shaped differently leaves this null.
        var act = () => ProviderBuildId.FromChatResponse(Response(raw: new RawThatThrows()));

        act.Should().NotThrow();
        act().Should().BeNull();
    }
}
