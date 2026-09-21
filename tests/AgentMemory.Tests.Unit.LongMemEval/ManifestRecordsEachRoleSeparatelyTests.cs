using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The preparation manifest is handed a DIFFERENT identity for each role.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a source test and not a behavioural one.</b> `HarnessRunIdentityTests` proves the four
/// identities are computed correctly; it cannot prove the manifest is given the right one, because
/// the call site takes four adjacent `string` parameters and passing the same value twice compiles,
/// runs, and produces a plausible artifact. That is exactly what happened: `AnswerModelId` and
/// `JudgeModelId` both received the answer model. It was TRUE while one Azure deployment served both
/// roles, and became false the moment `AI_JUDGE_*` made them separable — with nothing to notice.
/// </para>
/// <para>
/// The real fix is a typed argument rather than four positional strings, which is a larger change
/// than this phase should carry. Until then this test is the guard, and it fails loudly if the
/// duplicate ever comes back. <b>A provenance field that quietly restates another one is worse than
/// a missing field:</b> a missing one prompts a question, a wrong one answers it incorrectly.
/// </para>
/// </remarks>
public sealed class ManifestRecordsEachRoleSeparatelyTests
{
    [Fact]
    public void TheManifestCallSitePassesADistinctIdentityPerRole()
    {
        var source = File.ReadAllText(FindRepoFile(
            "tools/AgentMemory.LongMemEval/LongMemEvalPreparedPairProgram.cs"));

        source.Should().Contain("model.JudgeIdentity",
            "the judge's own identity must reach the manifest, not the answer model's");
        source.Should().Contain("model.ExtractionIdentity",
            "extraction must name its host like the other three roles do");

        // The shape of the original bug: two identical arguments in a row, landing on
        // AnswerModelId and JudgeModelId.
        var normalised = string.Join(
            "\n", source.Split('\n').Select(line => line.Trim()));

        normalised.Should().NotContain("deployment,\ndeployment,",
            "passing the same identity twice is how the manifest came to claim a run was graded by "
            + "the model that produced it");
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.", relativePath);
    }
}
