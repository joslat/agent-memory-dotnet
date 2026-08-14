using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Configuration;

/// <summary>
/// 25.5. The as-of recall path honours fewer options than the live one. That set is pinned here so it
/// is visible, justified, and cannot grow by accident.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a guard rather than a fix.</b> Ten options were honoured on the live path and ignored on the
/// as-of path. One — the per-request ranking intent — was a genuine wiring gap and is now wired. The
/// rest are <i>structural</i>: the capability does not exist on the as-of path at all, and adding it
/// means new repository methods and new Cypher, not a line of plumbing. Pretending otherwise by
/// silently accepting the option is what made this invisible for so long.
/// </para>
/// <para>
/// <b>What is genuinely missing, and what it costs a caller.</b>
/// </para>
/// <list type="bullet">
/// <item><b>Semantic message search.</b> <c>IMessageRepository</c> has no <c>SearchByVectorAsOfAsync</c>,
/// so an as-of recall returns <i>recent</i> messages but never <i>relevant</i> ones —
/// <c>MaxRelevantMessages</c> has nothing to cap. This is the largest gap: a bitemporal query gets a
/// strictly narrower message set than the same query at present time.</item>
/// <item><b>GraphRAG.</b> The context source has no as-of variant; a traversal cannot be replayed at a
/// past instant without temporal predicates the provider does not expose.</item>
/// <item><b>Fact expansion by predicate</b> and <b>query-relation resolution</b>, for the same reason.</item>
/// <item><b>Latency budget.</b> The live path degrades sections when a budget is exceeded; the as-of
/// path runs to completion. A caller passing a budget to an as-of recall does not get one.</item>
/// <item><b>Blend mode.</b> As-of assembles memory only, so there is nothing to blend.</item>
/// <item><b>Valid time.</b> Deliberate and correct — the as-of instant <i>is</i> the time coordinate,
/// and honouring a second one would silently pick one of two conflicting answers.</item>
/// </list>
/// <para>
/// A test asserting a list of names is normally a smell. It is right here because the failure being
/// prevented is <b>silent acceptance</b>: an option that binds, validates and does nothing on one of
/// two paths is exactly the defect this track keeps finding, and nothing else in the build notices it.
/// </para>
/// </remarks>
public sealed class AsOfRecallDivergenceTests
{
    /// <summary>Options the as-of path knowingly does not honour, each covered by the remarks above.</summary>
    private static readonly string[] KnownDivergence =
    [
        "BlendMode",
        "EnableGraphRag",
        "ExpandFactsByPredicate",
        "LatencyBudget",
        "MaxExpandedFacts",
        "MaxRelevantMessages",
        "ResolveQueryRelations",
        "ValidTime",
    ];

    [Fact]
    public void TheAsOfPathIgnoresExactlyTheOptionsThisTestNames()
    {
        var (live, asOf) = ReadPaths();

        var liveOptions = Referenced(live);
        var asOfOptions = Referenced(asOf);
        var diverging = liveOptions.Except(asOfOptions).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        diverging.Should().BeEquivalentTo(KnownDivergence,
            "an option honoured on one recall path and silently ignored on the other must be a "
            + "documented decision, not a discovery. If this failed because you ADDED a divergence, "
            + "wire it or document it here; if it failed because you REMOVED one, delete it from the "
            + "list.");
    }

    [Fact]
    public void TheRankingIntentIsHonouredOnBothPaths()
    {
        // The one item that was a real wiring gap rather than a structural absence, called out
        // separately so that closing it cannot be undone by editing a list.
        var (live, asOf) = ReadPaths();

        Referenced(live).Should().Contain("Intent");
        Referenced(asOf).Should().Contain("Intent",
            "an as-of recall asking for a non-default ranking intent used to be ranked by the default "
            + "policy, silently");
    }

    /// <summary>The assembler source split at the as-of entry point.</summary>
    /// <remarks>
    /// Source inspection, because the quantity of interest is "does this code path read this option",
    /// which no runtime assertion can establish without exercising every option combination against a
    /// live bitemporal store.
    /// </remarks>
    private static (string Live, string AsOf) ReadPaths()
    {
        var path = Path.Combine(
            RepositoryRoot(), "src", "AgentMemory.Core", "Services", "MemoryContextAssembler.cs");
        File.Exists(path).Should().BeTrue($"the assembler must be readable at {path}");

        var source = File.ReadAllText(path);
        var split = source.IndexOf("AssembleContextAsOfAsync", StringComparison.Ordinal);
        split.Should().BeGreaterThan(0);

        return (source[..split], source[split..]);
    }

    private static HashSet<string> Referenced(string source) =>
        Regex.Matches(source, @"recallOpts\.(\w+)")
            .Select(match => match.Groups[1].Value)
            .Concat(Regex.Matches(source, @"_options\.(\w+)").Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test must run from inside the repository");
        return directory!.FullName;
    }
}
