using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The repeated multi-step task the procedural-benefit harness measures against (7.6).
/// </summary>
/// <remarks>
/// <para>
/// A benchmark for procedural memory has one hard requirement: <b>the shortest correct path must be
/// discoverable but not guessable.</b> If an agent can succeed on its first attempt by calling the
/// obvious tool, there is no procedure to learn and both arms score identically — the measurement
/// returns "no benefit" for a reason that has nothing to do with memory.
/// </para>
/// <para>
/// So the ordering here is enforced rather than suggested. Booking requires a hold, a hold requires
/// the traveller's tier, and the tier lives behind a lookup the task prompt never mentions. An agent
/// meeting this cold must discover that chain by being refused; an agent with the procedure stored
/// walks it. That gap is the entire signal, and it is why <c>book</c> refuses politely with the
/// reason rather than erroring — a hard failure would end the run instead of teaching it.
/// </para>
/// <para>
/// <b>Deterministic tools, on purpose.</b> The agent is the only nondeterministic part; the world it
/// acts on answers identically every time. A benchmark whose environment varied would report the
/// variance as a memory effect, and with three attempts per arm there is nowhere near the sample size
/// to tell those apart.
/// </para>
/// </remarks>
internal sealed class ProceduralBenchmarkTask
{
    /// <summary>Marker the agent can only emit by completing the real chain.</summary>
    /// <remarks>
    /// Completion is checked against this rather than against the agent saying it finished. An agent
    /// that learned a <i>wrong</i> procedure reports success fluently, and that failure is invisible
    /// to every efficiency number the harness collects.
    /// </remarks>
    internal const string ConfirmationMarker = "BOOKING-CONFIRMED";

    private const string Traveller = "ruaidhri";
    private const string RequiredTier = "gold";
    private const string HoldReference = "HOLD-4417";

    /// <summary>Records what was called, so a test can assert the chain without a model.</summary>
    internal List<string> Calls { get; } = [];

    internal string Prompt =>
        $"Book the 14:05 rail connection for traveller '{Traveller}'. "
        + $"Reply with the confirmation reference exactly as the booking tool returns it.";

    internal bool IsComplete(string response) =>
        response.Contains(ConfirmationMarker, StringComparison.Ordinal);

    /// <summary>
    /// Words that would give the chain away if they appeared in a tool description.
    /// </summary>
    /// <remarks>
    /// The first run failed on exactly this: the bodies enforced the ordering and the descriptions
    /// announced it, so the model ordered the calls correctly cold and the enforcement never fired.
    /// A benchmark whose difficulty lives only in code the model never reads is not a benchmark.
    /// </remarks>
    internal static readonly string[] ChainRevealingWords =
        ["require", "first", "before", "returned by", "from the"];

    /// <summary>The tools, in the order a correct procedure uses them.</summary>
    internal IReadOnlyList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(LookUpTraveller),
        AIFunctionFactory.Create(PlaceHold),
        AIFunctionFactory.Create(Book),
    ];

    // Descriptions state PURPOSE only. Naming a prerequisite here hands the model the chain, and
    // the first run proved it: zero refusals, minimal tool count, both arms identical. The ordering
    // has to be learned from the refusals, or there is nothing for a procedure to remember.
    [Description("Looks up a traveller.")]
    private string LookUpTraveller(
        [Description("The traveller's name.")] string traveller)
    {
        Calls.Add(nameof(LookUpTraveller));
        return string.Equals(traveller, Traveller, StringComparison.OrdinalIgnoreCase)
            ? $"traveller={Traveller}; tier={RequiredTier}"
            : $"no traveller named '{traveller}'";
    }

    [Description("Places a hold on a connection.")]
    private string PlaceHold(
        [Description("The connection time, e.g. 14:05.")] string connection,
        [Description("The traveller's loyalty tier.")] string tier)
    {
        Calls.Add(nameof(PlaceHold));
        // Refused, not thrown. A hard failure ends the run; a refusal that names what is missing is
        // what an agent without the procedure can actually learn from -- and learning it cold, once,
        // is precisely the cost the stored procedure is supposed to remove.
        return string.Equals(tier, RequiredTier, StringComparison.OrdinalIgnoreCase)
            ? $"hold placed on {connection}; reference={HoldReference}"
            : $"refused: a hold needs the traveller's loyalty tier; look up the traveller first";
    }

    [Description("Confirms a booking.")]
    private string Book(
        [Description("The hold reference.")] string holdReference)
    {
        Calls.Add(nameof(Book));
        return string.Equals(holdReference, HoldReference, StringComparison.OrdinalIgnoreCase)
            ? string.Create(CultureInfo.InvariantCulture, $"{ConfirmationMarker} ref {HoldReference}")
            : "refused: booking needs a valid hold reference; place a hold first";
    }
}
