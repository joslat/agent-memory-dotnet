namespace AgentMemory.LongMemEval;

/// <summary>
/// The Phase-30 Wave-C capabilities a benchmark run may switch on, and the schema extensions they
/// need installed to persist.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> Every Wave-C capability ships off by default — deliberately, so a
/// sealed measurement keeps taking the path it was taken under. But they were also <i>unreachable
/// from the harness</i>: <c>LongMemEvalMemoryProfile</c> set exactly three fields on
/// <c>MemoryOptions</c> and none of them was a Phase-30 flag, and no CLI verb exposed one. So the
/// features built to move these numbers were the one thing no run could exercise, and their
/// measurement gates could never close — 30.6 sat "built, not measured" because it was
/// <i>unmeasurable</i>.
/// </para>
/// <para>
/// That is the same defect the <c>RescueShortOwnerResults</c> comment in the profile records one
/// wave earlier: <i>"a coverage lever with ZERO harness references until now… the mechanism most
/// directly matching the measured failure mode was the one thing no run could exercise."</i> Having
/// the options reachable is what makes a before/after ablation possible at all.
/// </para>
/// <para>
/// <b>Extensions are not optional decoration.</b> Wave-C features persist through the
/// SchemaExtension system. With the flag on and the extension absent, the DDL those writes need does
/// not exist, so they fail at the store — which reads as a broken feature rather than a dark one.
/// Each flag therefore carries its extension with it.
/// </para>
/// </remarks>
/// <param name="WorkingMemory">30.4 — the working-memory profile block on <c>:User</c>.</param>
/// <param name="ArithmeticMemory">30.6 — the session accountant that derives counts and sums.</param>
public sealed record PhaseThirtyFeatures(bool WorkingMemory = false, bool ArithmeticMemory = false)
{
    /// <summary>The shipped default, and the shape every existing sealed measurement was taken under.</summary>
    public static PhaseThirtyFeatures AllOff { get; } = new();

    /// <summary>Schema extensions required by whichever features are enabled.</summary>
    public IReadOnlyCollection<string> Extensions
    {
        get
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (WorkingMemory) set.Add("working-memory");
            if (ArithmeticMemory) set.Add("arithmetic");
            return set;
        }
    }

    /// <summary>True when nothing is enabled, so a run can assert it took the default path.</summary>
    public bool IsDefault => !WorkingMemory && !ArithmeticMemory;

    /// <summary>Stable one-line description for run provenance and report file names.</summary>
    public string Describe() => IsDefault
        ? "phase30:none"
        : "phase30:" + string.Join("+", new[]
        {
            WorkingMemory ? "working-memory" : null,
            ArithmeticMemory ? "arithmetic" : null,
        }.Where(part => part is not null));
}
