using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Inference;

/// <summary>
/// A stub rescues an UNCONFIGURED machine, never a MISCONFIGURED one.
/// </summary>
/// <remarks>
/// <para>
/// The port guide's §9 rule, and the reason it is a rule: if a host falls back to a placeholder
/// embedding generator whenever a real provider cannot be built, then
/// <c>AI_INFERENCE_PROVIDER=foundry</c> with a typo stops being a failure and becomes
/// <b>stub-generated evidence</b> — the resolver's fail-closed contract undone from beneath, by a
/// fallback that was only ever meant for a machine with no provider at all.
/// </para>
/// <para>
/// <b>Today no such seam exists, and this test is what keeps that true.</b> Two projects register
/// <c>StubEmbeddingGenerator</c> — the CLI evaluator and the TCK bridge — and both are model-free by
/// design: neither reads a single provider variable, so neither can degrade from a real provider to
/// a stub. The hazard is one wiring change away, not present. A test is cheaper than remembering.
/// </para>
/// <para>
/// If a project ever legitimately needs both, the fix is not to delete this test: it is to guard the
/// fallback with <c>InferenceProviderEnvironment.AnyConfigurationAttempted</c> and refuse when
/// something was attempted and could not be built. Then narrow this test to exempt it BY NAME, so
/// the exemption is a decision someone made rather than a check quietly weakening.
/// </para>
/// </remarks>
public sealed class StubNeverRescuesAMisconfiguredMachineTests
{
    /// <summary>No project both registers a stub generator and reads provider variables.</summary>
    [Fact]
    public void NoProjectBothStubsAndResolvesAProvider()
    {
        var root = FindRepoRoot();

        var offenders = new List<string>();
        foreach (var area in new[] { "src", "tools", "samples" })
        {
            var areaPath = Path.Combine(root, area);
            if (!Directory.Exists(areaPath)) continue;

            foreach (var project in Directory.EnumerateDirectories(areaPath))
            {
                var sources = Directory
                    .EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal)
                                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal))
                    .Select(File.ReadAllText)
                    .ToArray();

                // "new StubEmbeddingGenerator(" — the act of INSTALLING one, not merely naming the
                // type. Core declares it and the CLI's own comment mentions it; neither is a fallback.
                var installsStub = sources.Any(s =>
                    s.Contains("new StubEmbeddingGenerator(", StringComparison.Ordinal));

                var resolvesProvider = sources.Any(s =>
                    s.Contains("InferenceProviderEnvironment.Resolve", StringComparison.Ordinal)
                    || s.Contains("RealModel.TryCreate", StringComparison.Ordinal)
                    || s.Contains("HarnessClients.Create", StringComparison.Ordinal));

                if (installsStub && resolvesProvider)
                {
                    offenders.Add(Path.GetFileName(project));
                }
            }
        }

        offenders.Should().BeEmpty(
            "a project that can resolve a real provider AND install a stub can silently turn a "
            + "misconfigured machine into stub-generated evidence; guard the fallback with "
            + "AnyConfigurationAttempted and exempt it here by name");
    }

    /// <summary>
    /// The predicate that rule depends on exists and answers.
    /// </summary>
    /// <remarks>
    /// The guard above is only implementable because <c>AnyConfigurationAttempted</c> can tell
    /// "nothing configured" from "something attempted". If it ever stopped seeing a variable, the
    /// rule would silently become unenforceable — which is what
    /// <see cref="AllVariablesInStepTests"/> covers from the other side.
    /// </remarks>
    [Fact]
    public void TheDistinctionTheRuleNeedsIsAvailable()
    {
        AgentMemory.Inference.InferenceProviderEnvironment
            .AnyConfigurationAttempted(_ => null).Should().BeFalse("an untouched machine");

        AgentMemory.Inference.InferenceProviderEnvironment
            .AnyConfigurationAttempted(n => n == "AI_INFERENCE_PROVIDER" ? "foundry" : null)
            .Should().BeTrue("a named provider with nothing else set is an ATTEMPT, and must not be "
                             + "rescued by a stub");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentMemory.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
