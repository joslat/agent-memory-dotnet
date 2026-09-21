using AgentMemory.Inference;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Each role's run identity names the model AND the host that actually served it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The manifest used to pass the answer model twice</b> — once as <c>AnswerModelId</c> and once
/// as <c>JudgeModelId</c> — and that was *true* while a single Azure deployment served both roles.
/// The provider layer made them separable, and a record that still named the answer model would
/// assert a run was graded by something it was not. That is the one thing provenance exists to
/// prevent, so it is pinned rather than remembered.
/// </para>
/// <para>
/// Extraction is here for the same reason in a quieter form: it was stamped as a bare model name
/// while answer and embedding carried <c>model@provider</c>, so one of the four fields silently did
/// not say which host it came from.
/// </para>
/// </remarks>
public sealed class HarnessRunIdentityTests
{
    private static HarnessClients Clients(Func<string, string?> env)
    {
        // Resolve through the delegate so no process state is touched, then build the harness view
        // over the result -- the same settings object HarnessClients.Create() would have produced.
        var resolution = InferenceProviderEnvironment.Resolve(env);
        resolution.Settings.Should().NotBeNull(resolution.Diagnostic);
        return HarnessClients.ForSettings(resolution.Settings!);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    /// <summary>Every role carries the host, not just the model.</summary>
    [Fact]
    public void EveryRoleIdentityNamesItsHost()
    {
        var clients = Clients(Env(("BITDEER_API_KEY", "k")));

        clients.AnswerIdentity.Should().Be("zai-org/GLM-5.3-Flash@bitdeer");
        clients.ExtractionIdentity.Should().Be(
            "zai-org/GLM-5.3-Flash@bitdeer", "extraction must name its host like the others do");
        clients.JudgeIdentity.Should().Be("zai-org/GLM-5.3-Flash@bitdeer");
        clients.EmbeddingIdentity.Should().Be("BAAI/bge-m3@bitdeer/1024");
    }

    /// <summary>
    /// With a judge override in force, the judge identity is NOT the answer identity.
    /// </summary>
    /// <remarks>
    /// The case the old record got wrong. If these two were still the same string, a manifest would
    /// claim the subject graded itself when an independent grader did the work — or the reverse,
    /// which is worse.
    /// </remarks>
    [Fact]
    public void AJudgeOverrideProducesADistinctJudgeIdentity()
    {
        var clients = Clients(Env(
            ("BITDEER_API_KEY", "k"),
            ("AI_JUDGE_PROVIDER", "openai"),
            ("AI_JUDGE_ENDPOINT", "https://api.openai.com/v1"),
            ("AI_JUDGE_API_KEY", "sk-judge"),
            ("AI_JUDGE_MODEL", "gpt-4o")));

        clients.JudgeIdentity.Should().Be("gpt-4o@openai");
        clients.JudgeIdentity.Should().NotBe(clients.AnswerIdentity);
    }

    /// <summary>An extraction override produces a distinct extraction identity.</summary>
    [Fact]
    public void AnExtractionOverrideProducesADistinctExtractionIdentity()
    {
        var clients = Clients(Env(
            ("BITDEER_API_KEY", "k"),
            ("AGENTMEMORY_EXTRACTION_MODEL", "some-other-model")));

        clients.ExtractionIdentity.Should().Be("some-other-model@bitdeer");
        clients.ExtractionIdentity.Should().NotBe(clients.AnswerIdentity);
    }

    /// <summary>
    /// Without overrides the roles legitimately share an identity — and that is not a bug.
    /// </summary>
    /// <remarks>
    /// The counterpart that stops the two tests above from passing against an implementation that
    /// made every identity gratuitously different. Running the judge on the subject's model is the
    /// documented default; the record should say so plainly rather than invent a distinction.
    /// </remarks>
    [Fact]
    public void WithoutOverridesTheRolesShareAnIdentity()
    {
        var clients = Clients(Env(("BITDEER_API_KEY", "k")));

        clients.JudgeIdentity.Should().Be(clients.AnswerIdentity);
        clients.ExtractionIdentity.Should().Be(clients.AnswerIdentity);
    }
}
