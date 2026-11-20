using System.Text.RegularExpressions;
using AgentMemory.Core;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit;

/// <summary>
/// The bound on the parsers' patterns is an answer, not a crash: a parse that runs out of time gives its safe answer.
/// Before, the temporal parsers, the fact date reader, the calendar reader and the extraction gate threw
/// <see cref="RegexMatchTimeoutException"/> to the recall or write that called them; a long query on a busy machine
/// failed <c>TemporalQueryParserTests.ALongQueryDoesNotHangTheParser</c> that way (full run, 20 November 2026).
/// </summary>
public sealed class BoundedParseTests
{
    /// <summary>Nested quantifiers on a string that cannot match: exponential backtracking, certain to time out.</summary>
    private static readonly Regex Catastrophic = new(@"^(a+)+$", RegexOptions.None, TimeSpan.FromTicks(1));

    private static readonly string NoMatch = new string('a', 64) + "!";

    [Fact]
    public void A_parse_that_runs_out_of_time_gives_its_safe_answer()
    {
        // The premise: the pattern really times out.
        var raw = () => Catastrophic.IsMatch(NoMatch);
        raw.Should().Throw<RegexMatchTimeoutException>();

        BoundedParse.OrElse(() => Catastrophic.IsMatch(NoMatch), whenOutOfTime: false).Should().BeFalse();
        BoundedParse.OrElse<DateTimeOffset?>(() => Catastrophic.IsMatch(NoMatch) ? DateTimeOffset.UnixEpoch : null, null)
            .Should().BeNull();
    }

    [Fact]
    public void A_parse_in_time_gives_its_own_answer()
    {
        BoundedParse.OrElse(() => Regex.IsMatch("in March", "March", RegexOptions.None, TimeSpan.FromSeconds(1)), false)
            .Should().BeTrue();
    }

    [Fact]
    public void Only_running_out_of_time_is_turned_into_the_safe_answer()
    {
        var act = () => BoundedParse.OrElse<bool>(() => throw new InvalidOperationException("a real fault"), false);

        act.Should().Throw<InvalidOperationException>("a fault that is not the time bound must still surface");
    }
}
