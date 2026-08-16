using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The meter records which backend build served each call (S-4).
/// </summary>
/// <remarks>
/// <para>
/// Reading the id off a response is one thing and <b>observing it on every call of a real run</b> is
/// another — the second is the half that would silently no-op, and the half that matters. Wired here
/// rather than asserted at the reader, because a reader nobody calls reports nothing while looking
/// correct.
/// </para>
/// <para>
/// The sharp case is two distinct builds inside a <i>single</i> run: the run straddled a backend change,
/// so even its own arm-to-arm comparison is suspect, and nothing else in the harness could notice.
/// </para>
/// </remarks>
public sealed class LongMemEvalChatCallMeterProviderBuildTests
{
    /// <summary>A chat client that stamps a caller-chosen build id on each successive response.</summary>
    private sealed class ScriptedClient(params string?[] builds) : IChatClient
    {
        private int _call;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var build = builds[Math.Min(_call++, builds.Length - 1)];
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"));
            if (build is not null)
            {
                response.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["system_fingerprint"] = build
                };
            }

            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static async Task<LongMemEvalChatCallSnapshot> RunAsync(int calls, params string?[] builds)
    {
        using var meter = new LongMemEvalChatCallMeter(new ScriptedClient(builds));
        for (var index = 0; index < calls; index++)
            await meter.GetResponseAsync([new ChatMessage(ChatRole.User, "ask")]);

        return meter.Snapshot();
    }

    [Fact]
    public async Task TheBuildServingEachCallIsCounted()
    {
        var snapshot = await RunAsync(3, "fp_stable");

        snapshot.ProviderBuilds.Should().ContainKey("fp_stable").WhoseValue.Should().Be(3);
        snapshot.ProviderBuildChangedDuringRun.Should().BeFalse();
        snapshot.CallsWithoutProviderBuild.Should().Be(0);
    }

    [Fact]
    public async Task ARunThatStraddlesABackendChangeSaysSo()
    {
        // The reason this is worth instrumenting at all: nothing else in the harness can distinguish
        // "the change under test moved the number" from "the two halves of this run ran on different
        // backend builds".
        var snapshot = await RunAsync(3, "fp_before", "fp_after", "fp_after");

        snapshot.ProviderBuilds.Should().HaveCount(2);
        snapshot.ProviderBuilds["fp_after"].Should().Be(2);
        snapshot.ProviderBuildChangedDuringRun.Should().BeTrue();
    }

    [Fact]
    public async Task CallsWithNoReportedBuildAreCountedSeparately_NotBucketedUnderAPlaceholder()
    {
        // "The provider did not report a build" is a distinct fact from "the build was X". Folding the
        // first into the second would let a report assert comparability it cannot support.
        var snapshot = await RunAsync(2, new string?[] { null });

        snapshot.ProviderBuilds.Should().BeEmpty();
        snapshot.CallsWithoutProviderBuild.Should().Be(2);
        snapshot.ProviderBuildChangedDuringRun.Should().BeFalse();
    }

    [Fact]
    public async Task RecordingABuildDoesNotDisturbTheCallAccounting()
    {
        // The meter's existing job is the load-bearing one; a telemetry addition that moved a call count
        // would corrupt every cost figure in the report.
        var snapshot = await RunAsync(4, "fp_stable");

        snapshot.Calls.Should().Be(4);
        snapshot.CompletedCalls.Should().Be(4);
        snapshot.Failures.Should().Be(0);
    }
}
