using System.Text.RegularExpressions;

namespace AgentMemory.Core;

/// <summary>
/// Runs a parse whose regular expressions carry a match timeout, and gives the parse's safe answer when one runs out of
/// time.
/// </summary>
/// <remarks>
/// The temporal parsers, the fact date reader, the calendar reader and the extraction gate bound every pattern (100 ms)
/// because they run on each turn's recall or write path. A bound only helps if running out of time is an answer:
/// uncaught, <see cref="RegexMatchTimeoutException"/> turned a long turn on a busy host into a failed recall or write.
/// Each caller names what "nothing recognised" means for it: no temporal reference, no window, no date, extract now.
/// </remarks>
internal static class BoundedParse
{
    /// <summary>The parse's result, or <paramref name="whenOutOfTime"/> when one of its patterns timed out.</summary>
    internal static T OrElse<T>(Func<T> parse, T whenOutOfTime)
    {
        try
        {
            return parse();
        }
        catch (RegexMatchTimeoutException)
        {
            return whenOutOfTime;
        }
    }
}
