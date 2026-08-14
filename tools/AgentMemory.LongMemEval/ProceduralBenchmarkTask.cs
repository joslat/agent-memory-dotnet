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
internal sealed class ProceduralBenchmarkTask : IProceduralTask
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

    /// <summary>
    /// The arbitrary dependency, and the reason this task can be measured at all.
    /// </summary>
    /// <remarks>
    /// Booking needs a clearance code, and the only tool that yields one is a <i>service bulletin</i>
    /// lookup — a name that does not suggest it. Two earlier runs failed to discriminate because
    /// every dependency was inferable from tool and parameter names, so a competent model ordered
    /// the calls cold and no procedure could save anything. A semantic cue the model can follow is
    /// not a procedure worth remembering; an arbitrary one is.
    /// </remarks>
    private const string ClearanceCode = "CLR-7731";

    /// <summary>
    /// An undocumented environment convention: the session must be refreshed before a hold.
    /// </summary>
    /// <remarks>
    /// <b>The fourth run's finding, implemented.</b> Attempts 1-4 all failed to discriminate for one
    /// reason: a competent model reads tool descriptions and selects correctly first time, so there
    /// is no exploration to save. Hiding a dependency did not help, because the dependency was still
    /// <i>inferable</i> from the interface.
    /// <para>
    /// This one is not inferable from anything. Nothing in <c>refresh_session</c>'s name or
    /// description connects it to holds; the requirement exists only in the environment's behaviour
    /// and is discoverable only by being refused. That is what a human writes a runbook for, and a
    /// runbook is what a procedure is.
    /// </para>
    /// </remarks>
    private bool _sessionRefreshed;

    /// <summary>Records what was called, so a test can assert the chain without a model.</summary>
    public List<string> Calls { get; } = [];

    public string Prompt =>
        $"Book the 14:05 rail connection for traveller '{Traveller}'. "
        + $"Reply with the confirmation reference exactly as the booking tool returns it.";

    public bool IsComplete(string response) =>
        response.Contains(ConfirmationMarker, StringComparison.Ordinal);

    /// <summary>Marker every refusal in this environment starts with.</summary>
    /// <remarks>
    /// Every tool that declines does so with this prefix, so "was this call refused" is an exact string
    /// test rather than a guess about the shape of a sentence. The agent gains nothing from it — the
    /// word appears in the refusal text either way — and the harness gains the ability to tell a call
    /// that worked from a call that was turned away.
    /// </remarks>
    internal const string RefusalPrefix = "refused:";

    /// <summary>
    /// Whether a tool result is a refusal, i.e. whether that call did any work.
    /// </summary>
    /// <remarks>
    /// Supplied to the runner so a promoted procedure records the calls that <b>worked</b>. Without it,
    /// promotion stores the transcript of how the agent stumbled into success — including the call it was
    /// refused on — and replaying that reproduces the mistake. The seventh run measured exactly that: the
    /// promoted chain read "PlaceHold then RefreshSession then PlaceHold", so the arm holding the
    /// procedure still paid for the wasted call, and the two arms tied at six tool calls.
    /// </remarks>
    internal static bool IsRefusal(string result) =>
        result.StartsWith(RefusalPrefix, StringComparison.OrdinalIgnoreCase);

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
    public IReadOnlyList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(LookUpTraveller),
        AIFunctionFactory.Create(PlaceHold),
        AIFunctionFactory.Create(CheckServiceBulletin),
        AIFunctionFactory.Create(RefreshSession),
        AIFunctionFactory.Create(Book),
        .. Decoys(),
    ];

    /// <summary>
    /// Plausible tools that are never needed, so calling everything stops being free.
    /// </summary>
    /// <remarks>
    /// <b>The third run's finding, implemented.</b> With four tools, an agent skips discovery entirely
    /// by invoking all of them — the unguided policy is already near-optimal, so a stored procedure
    /// cannot pay for itself and both arms tie. Exhaustive calling has to cost more than the task is
    /// worth before knowing the route is worth anything.
    /// <para>
    /// The decoys are deliberately relevant-sounding. Obvious filler would be skipped on sight, which
    /// would restore the small effective action space this exists to remove.
    /// </para>
    /// </remarks>
    private IEnumerable<AITool> Decoys() =>
        new (string Name, string Description)[]
        {
            ("check_seat_map", "Returns the seat map for a connection."),
            ("list_fare_classes", "Lists fare classes available on a connection."),
            ("get_station_facilities", "Returns facilities at a station."),
            ("check_loyalty_balance", "Returns a traveller's loyalty point balance."),
            ("list_connections", "Lists connections between two stations."),
            ("get_refund_policy", "Returns the refund policy for a fare class."),
            ("check_platform", "Returns the departure platform for a connection."),
            ("get_catering_menu", "Returns the catering menu for a connection."),
            ("list_baggage_rules", "Returns baggage allowance rules."),
            ("check_delay_history", "Returns recent punctuality for a connection."),
            ("get_carriage_layout", "Returns the carriage layout for a connection."),
            ("list_partner_operators", "Lists partner operators for a route."),
        }
        .Select(decoy => AIFunctionFactory.Create(
            (string query) =>
            {
                Calls.Add(decoy.Name);
                return $"{decoy.Name}: no action required for this booking.";
            },
            decoy.Name,
            decoy.Description));

    [Description("Returns the service bulletin for a connection.")]
    private string CheckServiceBulletin(
        [Description("The connection time.")] string connection)
    {
        Calls.Add(nameof(CheckServiceBulletin));
        // The clearance code is buried in an otherwise unremarkable bulletin. Nothing in the tool's
        // name or signature says it is the source, so it has to be found rather than deduced.
        return $"bulletin for {connection}: on time; catering normal; clearance={ClearanceCode}";
    }

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

    [Description("Refreshes the booking session.")]
    private string RefreshSession()
    {
        Calls.Add(nameof(RefreshSession));
        _sessionRefreshed = true;
        return "session refreshed";
    }

    [Description("Places a hold on a connection.")]
    private string PlaceHold(
        [Description("The connection time, e.g. 14:05.")] string connection,
        [Description("The traveller's loyalty tier.")] string tier)
    {
        Calls.Add(nameof(PlaceHold));

        // The convention that can only be learned by failing. The refusal deliberately does NOT name
        // refresh_session -- naming it would make this inferable again, which is what defeated the
        // previous four attempts.
        if (!_sessionRefreshed) return "refused: booking session is stale";
        // Refused, not thrown. A hard failure ends the run; a refusal that names what is missing is
        // what an agent without the procedure can actually learn from -- and learning it cold, once,
        // is precisely the cost the stored procedure is supposed to remove.
        return string.Equals(tier, RequiredTier, StringComparison.OrdinalIgnoreCase)
            ? $"hold placed on {connection}; reference={HoldReference}"
            : $"refused: a hold needs the traveller's loyalty tier; look up the traveller first";
    }

    [Description("Confirms a booking.")]
    private string Book(
        [Description("The hold reference.")] string holdReference,
        [Description("The clearance code.")] string clearanceCode)
    {
        Calls.Add(nameof(Book));
        if (!string.Equals(holdReference, HoldReference, StringComparison.OrdinalIgnoreCase))
            return "refused: booking needs a valid hold reference";

        // Names what is missing, never where to get it. Saying "check the service bulletin" would
        // hand over the arbitrary step and put us back where the first two runs were.
        return string.Equals(clearanceCode, ClearanceCode, StringComparison.OrdinalIgnoreCase)
            ? string.Create(CultureInfo.InvariantCulture, $"{ConfirmationMarker} ref {HoldReference}")
            : "refused: booking needs a valid clearance code";
    }
}
