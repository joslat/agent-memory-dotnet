using AgentMemory.Inference;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The harness's model clients, for every role, from one resolved provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sixteen programs each built their own <c>AzureOpenAIClient</c> from three environment
/// variables read inline.</b> That is sixteen places for a provider decision to be made slightly
/// differently, and it is why the extraction override was honoured in five programs and ignored in
/// the rest. One resolution, one place, every role.
/// </para>
/// <para>
/// <b>The roles are not interchangeable.</b> The ANSWER client is the subject under test and keeps
/// the operator's timeout so a hung model fails fast. The JUDGE is not the subject: timing it out
/// discards a measurement that has already been paid for, so it gets a generous one and honours the
/// judge override block when set. EXTRACTION runs on
/// <see cref="InferenceProviderSettings.EffectiveExtractionModel"/>, which is the named override or
/// the primary.
/// </para>
/// </remarks>
internal sealed class HarnessClients
{
    private HarnessClients(InferenceProviderSettings settings) => Settings = settings;

    /// <summary>The resolved settings. The run identity comes from here.</summary>
    internal InferenceProviderSettings Settings { get; }

    /// <summary>
    /// Resolves the provider, or throws with the operator-facing reason.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning a flag because every caller is a benchmark entry point that
    /// cannot proceed without a model, and the diagnostic already names exactly what is missing.
    /// </remarks>
    internal static HarnessClients Create()
    {
        var resolution = InferenceProviderEnvironment.Resolve();

        if (resolution.Settings is not { } settings)
        {
            throw new InvalidOperationException(resolution.Diagnostic);
        }

        if (!settings.HasEmbeddings)
        {
            throw new InvalidOperationException(
                resolution.EmbeddingDiagnostic
                ?? "An embedding model is required and none is configured.");
        }

        return new HarnessClients(settings);
    }

    /// <summary>
    /// Builds the harness view over settings already in hand, rather than reading the environment.
    /// </summary>
    /// <remarks>
    /// The same reasoning that gives the resolver a <c>Func&lt;string,string?&gt;</c> overload:
    /// anything that can be handed its inputs does not need process-wide environment mutation to be
    /// tested, and the run identities are exactly the part worth testing without a key. Without this
    /// seam the identities could only be exercised by a live run, which is how the judge identity
    /// came to be wrong in the manifest without anything noticing.
    /// </remarks>
    internal static HarnessClients ForSettings(InferenceProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HarnessClients(settings);
    }

    /// <summary>The subject under test.</summary>
    internal IChatClient CreateAnswerClient() => Chat("answer", Settings.Model);

    /// <summary>The extraction model — the named override, or the primary.</summary>
    internal IChatClient CreateExtractionClient() =>
        Chat("extraction", Settings.EffectiveExtractionModel);

    /// <summary>The judge, which honours its own override block when one is set.</summary>
    internal IChatClient CreateJudgeClient() =>
        InferenceClientFactory.TryCreateJudgeChatClient(Settings, out var client, out var diagnostic)
            ? client
            : throw new InvalidOperationException(diagnostic);

    /// <summary>The embedding generator.</summary>
    internal IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddings() =>
        InferenceClientFactory.TryCreateEmbeddingGenerator(Settings, out var generator, out var diagnostic)
            ? generator
            : throw new InvalidOperationException(diagnostic);

    /// <summary>
    /// The run identity for a measured number: <c>model@provider</c>.
    /// </summary>
    /// <remarks>
    /// Extends PR #224's stamp, which recorded the model and the backend build but not the HOST. The
    /// same model id served by two providers is not the same measurement — different quantisation,
    /// different serving stack, different sampling defaults — and without the host in the identity
    /// two such runs are indistinguishable in the artifact.
    /// </remarks>
    internal string AnswerIdentity => Settings.ModelIdentity;

    /// <summary>The judge's identity, which may name a different host entirely.</summary>
    internal string JudgeIdentity =>
        Settings.HasJudgeOverride
            ? $"{Settings.JudgeModel}@{InferenceProviderNames.ToToken(Settings.JudgeProvider)}"
            : Settings.ModelIdentity;

    /// <summary>The extraction model's identity.</summary>
    internal string ExtractionIdentity =>
        $"{Settings.EffectiveExtractionModel}@{InferenceProviderNames.ToToken(Settings.Provider)}";

    /// <summary>The embedding identity, including the width: <c>model@provider/dims</c>.</summary>
    internal string EmbeddingIdentity => Settings.EmbeddingIdentity!;

    private IChatClient Chat(string purpose, string model) =>
        InferenceClientFactory.TryCreateChatClient(
            Settings, purpose, out var client, out var diagnostic, model)
            ? client
            : throw new InvalidOperationException(diagnostic);
}
