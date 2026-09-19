using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The PRODUCER side of the provider-build field, which nothing tested.
/// </summary>
/// <remarks>
/// The scoreboard tests hand-write <c>providerBuilds</c> and the meter tests stop at
/// <c>Snapshot()</c>, so a regression between them could leave both suites green while real
/// artifacts omitted or mis-scoped the field entirely. That is the same producer/consumer split
/// this codebase has been bitten by repeatedly: two halves tested apart, and the seam between them
/// tested by nobody.
/// </remarks>
public sealed class ProviderBuildScopeTests
{
    /// <summary>Roles are kept apart, because a flattened set cannot tell two systems apart.</summary>
    [Fact]
    public async Task EachRoleReportsItsOwnBuild()
    {
        var answer = Meter("fp_answer");
        var judge = Meter("fp_judge");

        var scope = TypedMemEvalProviderBuilds.StartVertical(answer, judge, extraction: null);
        await CallAsync(answer, judge);

        var builds = scope.Snapshot();

        builds.Should().NotBeNull();
        builds!["answer"].Should().BeEquivalentTo(["fp_answer"]);
        builds["judge"].Should().BeEquivalentTo(["fp_judge"]);
        builds.Should().NotContainKey("extraction", "that role made no calls in this vertical");
    }

    /// <summary>
    /// One call without a build makes the whole vertical UNKNOWN.
    /// </summary>
    /// <remarks>
    /// Nine responses carrying <c>fp_a</c> and a tenth carrying nothing is not a run on
    /// <c>fp_a</c>: part of it is unaccounted for. Recording <c>fp_a</c> anyway would let it band
    /// with a fully accounted run — the gate enforcing unknown-is-not-agreement on a field that was
    /// built by breaking that very rule.
    /// </remarks>
    [Fact]
    public async Task ACallWithoutABuildMakesTheVerticalUnknown()
    {
        var answer = Meter("fp_answer", "fp_answer", null);

        var scope = TypedMemEvalProviderBuilds.StartVertical(answer, judge: null, extraction: null);
        await CallAsync(answer);
        await CallAsync(answer);
        await CallAsync(answer);

        scope.Snapshot().Should().BeNull(
            "a run that cannot account for all of its calls has no build identity");
    }

    /// <summary>
    /// A vertical reports only what IT saw, not what earlier verticals did.
    /// </summary>
    /// <remarks>
    /// The meters are created once and reused across an <c>all</c> invocation. Reading their totals
    /// at the end of the third vertical would report the first two's builds, so after any backend
    /// rollover a clean run looks mixed — and clean runs stop banding, which is the opposite of what
    /// this field is for.
    /// </remarks>
    [Fact]
    public async Task AVerticalReportsOnlyItsOwnBuildsAndNotEarlierOnes()
    {
        var answer = Meter("fp_first", "fp_second");

        // First vertical burns the first build.
        var first = TypedMemEvalProviderBuilds.StartVertical(answer, null, null);
        await CallAsync(answer);
        first.Snapshot()!["answer"].Should().BeEquivalentTo(["fp_first"]);

        // Second vertical, same meters, different backend.
        var second = TypedMemEvalProviderBuilds.StartVertical(answer, null, null);
        await CallAsync(answer);

        second.Snapshot()!["answer"].Should().BeEquivalentTo(["fp_second"],
            "the first vertical's build belongs to the first vertical");
    }

    /// <summary>A vertical that made no calls at all reports unknown, not an empty identity.</summary>
    [Fact]
    public void AVerticalThatCalledNothingIsUnknown() =>
        TypedMemEvalProviderBuilds.StartVertical(Meter("fp_a"), null, null)
            .Snapshot().Should().BeNull();

    private static LongMemEvalChatCallMeter Meter(params string?[] buildsPerCall)
    {
        var index = 0;
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var build = buildsPerCall[Math.Min(index++, buildsPerCall.Length - 1)];
                var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
                if (build is not null)
                {
                    response.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["system_fingerprint"] = build,
                    };
                }

                return Task.FromResult(response);
            });

        return new LongMemEvalChatCallMeter(inner);
    }

    private static async Task CallAsync(params LongMemEvalChatCallMeter[] meters)
    {
        foreach (var meter in meters)
        {
            await meter.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        }
    }
}
