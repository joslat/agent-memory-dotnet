using AgentMemory.Inference;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Inference;

/// <summary>
/// The four resolution rules, and the embedding half that hangs off them.
/// </summary>
/// <remarks>
/// <para>
/// These drive the resolver through its <c>Func&lt;string,string?&gt;</c> overload, so they touch no
/// process state and run in parallel. The one test that exercises the real environment is in
/// <see cref="RealEnvironmentResolutionTests"/>, serialised for that reason.
/// </para>
/// <para>
/// <b>The rules exist because the failure they prevent is silent.</b> A resolver that falls through
/// to another provider when the chosen one is half-configured sends an API key to a host the
/// operator did not pick, and answers arrive normally.
/// </para>
/// </remarks>
public sealed class ProviderSelectionTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    /// <summary>Rule 1: an explicitly named provider is the one used.</summary>
    [Fact]
    public void ExplicitProviderWinsOverAnAutoDetectableOne()
    {
        // Azure is complete AND would win auto-detect; the selector must override that.
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "bitdeer"),
            ("BITDEER_API_KEY", "k"),
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", "azure-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-mini")));

        resolution.Settings!.Provider.Should().Be(InferenceProvider.Bitdeer);
    }

    /// <summary>Rule 2: an unknown provider name fails closed and lists the real ones.</summary>
    [Fact]
    public void AnUnknownProviderNameFailsClosed()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "bitdeeer"),
            ("BITDEER_API_KEY", "k")));

        resolution.IsConfigured.Should().BeFalse();
        resolution.Diagnostic.Should().Contain("bitdeeer").And.Contain("bitdeer");
    }

    /// <summary>
    /// Rule 2: a named provider missing its variables NEVER falls through to a complete one.
    /// </summary>
    /// <remarks>
    /// The important half is the negative. Azure is fully configured here; if the resolver fell back
    /// to it, every call would succeed against a host the operator explicitly did not choose.
    /// </remarks>
    [Fact]
    public void AnExplicitChoiceNeverFallsBackToAnotherProvider()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "foundry"),
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", "azure-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-mini")));

        resolution.IsConfigured.Should().BeFalse();
        resolution.Diagnostic.Should()
            .Contain("FOUNDRY_ENDPOINT").And.Contain("FOUNDRY_API_KEY").And.Contain("FOUNDRY_MODEL");
    }

    /// <summary>
    /// Rule 3 and the backward-compatibility pin: an Azure-only machine resolves to Azure.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that protects everyone who configured this repository before the provider
    /// layer existed.</b> Azure is first in the auto-detect order for exactly this reason; if someone
    /// reorders the list, this fails rather than a user's machine quietly changing host.
    /// </remarks>
    [Fact]
    public void AnAzureOnlyMachineIsUnchanged()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", "azure-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "my-deployment")));

        var settings = resolution.Settings!;
        settings.Provider.Should().Be(InferenceProvider.AzureOpenAI);
        settings.Model.Should().Be("my-deployment");
        settings.EmbeddingModel.Should().Be("text-embedding-ada-002");
        settings.EmbeddingDimensions.Should().Be(1536);
    }

    /// <summary>Auto-detect order: Azure beats Bitdeer when both are complete.</summary>
    [Fact]
    public void AzureIsFirstInAutoDetect()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", "azure-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-mini"),
            ("BITDEER_API_KEY", "k")));

        resolution.Settings!.Provider.Should().Be(InferenceProvider.AzureOpenAI);
    }

    /// <summary>
    /// Bitdeer needs ONE variable, and gets chat and embeddings from it.
    /// </summary>
    /// <remarks>The headline claim of the whole contract, so it is asserted end to end.</remarks>
    [Fact]
    public void BitdeerNeedsOnlyItsApiKey()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(("BITDEER_API_KEY", "k")));

        var settings = resolution.Settings!;
        settings.Provider.Should().Be(InferenceProvider.Bitdeer);
        settings.Endpoint.Should().Be("https://api-inference.bitdeer.ai/v1");
        settings.Model.Should().Be("zai-org/GLM-5.3-Flash");
        settings.ModelIdentity.Should().Be("zai-org/GLM-5.3-Flash@bitdeer");
        settings.HasEmbeddings.Should().BeTrue();
        settings.EmbeddingIdentity.Should().Be("BAAI/bge-m3@bitdeer/1024");
    }

    /// <summary>Rule 4: a half-configured provider names its own missing variables.</summary>
    [Fact]
    public void AHalfConfiguredProviderIsTreatedAsATypo()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/")));

        resolution.IsConfigured.Should().BeFalse();
        resolution.Diagnostic.Should()
            .Contain("AZURE_OPENAI_API_KEY").And.Contain("AZURE_OPENAI_DEPLOYMENT");
        resolution.Diagnostic.Should().NotContain("BITDEER",
            "naming another provider's variables would answer a question the operator did not ask");
    }

    /// <summary>A bare machine gets the full option list, not a bare failure.</summary>
    [Fact]
    public void NothingConfiguredListsEveryOption()
    {
        var resolution = InferenceProviderEnvironment.Resolve(_ => null);

        resolution.IsConfigured.Should().BeFalse();
        foreach (var token in InferenceProviderNames.AllTokens)
        {
            resolution.Diagnostic.Should().Contain(token);
        }
    }

    /// <summary>A diagnostic never contains a key, whatever went wrong.</summary>
    [Fact]
    public void DiagnosticsNeverEchoTheKey()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "foundry"),
            ("FOUNDRY_API_KEY", "sk-super-secret-value")));

        resolution.Diagnostic.Should().NotContain("sk-super-secret-value");
    }

    /// <summary>The embedding override block wins outright when complete.</summary>
    [Fact]
    public void TheEmbeddingOverrideBlockWinsWhenComplete()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("BITDEER_API_KEY", "k"),
            ("AI_EMBEDDING_PROVIDER", "openai-compatible"),
            ("AI_EMBEDDING_ENDPOINT", "http://localhost:11434/v1"),
            ("AI_EMBEDDING_API_KEY", "no-key-needed"),
            ("AI_EMBEDDING_MODEL", "nomic-embed-text")));

        var settings = resolution.Settings!;
        settings.Provider.Should().Be(InferenceProvider.Bitdeer, "chat is unaffected");
        settings.EmbeddingProvider.Should().Be(InferenceProvider.OpenAICompatible);
        settings.EmbeddingIdentity.Should().Be("nomic-embed-text@openai-compatible/768");
    }

    /// <summary>
    /// A PARTIAL override block fails the embedding half by name, rather than falling back.
    /// </summary>
    /// <remarks>
    /// Falling back to the chat host here would build the store on a model the operator was actively
    /// trying to move away from, and nothing would say so.
    /// </remarks>
    [Fact]
    public void APartialEmbeddingOverrideFailsClosed()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("BITDEER_API_KEY", "k"),
            ("AI_EMBEDDING_PROVIDER", "openai-compatible"),
            ("AI_EMBEDDING_ENDPOINT", "http://localhost:11434/v1")));

        resolution.IsConfigured.Should().BeTrue("chat resolved fine");
        resolution.Settings!.HasEmbeddings.Should().BeFalse();
        resolution.EmbeddingDiagnostic.Should()
            .Contain("AI_EMBEDDING_API_KEY").And.Contain("AI_EMBEDDING_MODEL");
    }

    /// <summary>An unknown embedding model fails closed rather than guessing a dimension.</summary>
    /// <remarks>
    /// The dimension defines the vector index. A guess does not throw — it builds a store that cannot
    /// be searched correctly, and the symptom surfaces much later as poor recall.
    /// </remarks>
    [Fact]
    public void AnUnknownEmbeddingModelFailsClosed()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("BITDEER_API_KEY", "k"),
            ("BITDEER_EMBEDDING_MODEL", "nvidia/Nemotron-3-Embed-Whatever")));

        resolution.Settings!.HasEmbeddings.Should().BeFalse();
        resolution.EmbeddingDiagnostic.Should().Contain("AI_EMBEDDING_DIMENSIONS");
    }

    /// <summary>An explicit dimension rescues an unknown model, and beats the table.</summary>
    [Fact]
    public void AnExplicitDimensionWinsOverTheTable()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("BITDEER_API_KEY", "k"),
            ("BITDEER_EMBEDDING_MODEL", "BAAI/bge-m3"),
            ("AI_EMBEDDING_DIMENSIONS", "512")));

        resolution.Settings!.EmbeddingDimensions.Should().Be(
            512, "the operator knows their deployment better than a table compiled here");
    }

    /// <summary>A cleartext key to a remote host is refused.</summary>
    [Fact]
    public void HttpToARemoteHostIsRefused()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("OPENAI_COMPATIBLE_ENDPOINT", "http://remote-host/v1"),
            ("OPENAI_COMPATIBLE_MODEL", "some-model"),
            ("OPENAI_COMPATIBLE_API_KEY", "sk-secret")));

        resolution.IsConfigured.Should().BeFalse();
        resolution.Diagnostic.Should().Contain("cleartext");
        resolution.Diagnostic.Should().NotContain("sk-secret");
    }

    /// <summary>http to loopback is allowed, because a local server has no wire to sniff.</summary>
    [Fact]
    public void HttpToLoopbackIsAllowed()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("OPENAI_COMPATIBLE_ENDPOINT", "http://localhost:11434/v1"),
            ("OPENAI_COMPATIBLE_MODEL", "llama3"),
            ("OPENAI_COMPATIBLE_EMBEDDING_MODEL", "nomic-embed-text")));

        resolution.IsConfigured.Should().BeTrue();
        resolution.Settings!.ApiKey.Should().Be(
            InferenceProviderEnvironment.NoKeySentinel, "a keyless local host still needs a credential object");
    }

    /// <summary>The Azure extraction alias is honoured under Azure and ignored elsewhere.</summary>
    /// <remarks>
    /// On a Bitdeer machine that name is a leftover from a deleted Azure deployment, not an
    /// instruction — honouring it there would route extraction to a model id that does not exist.
    /// </remarks>
    [Theory]
    [InlineData("azure", "legacy-extract")]
    [InlineData("bitdeer", "zai-org/GLM-5.3-Flash")]
    public void TheAzureExtractionAliasAppliesOnlyToAzure(string provider, string expected)
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", provider),
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", "azure-key"),
            ("AZURE_OPENAI_DEPLOYMENT", "gpt-4o-mini"),
            ("BITDEER_API_KEY", "k"),
            ("AZURE_OPENAI_EXTRACTION_DEPLOYMENT", "legacy-extract")));

        resolution.Settings!.EffectiveExtractionModel.Should().Be(expected);
    }

    /// <summary>
    /// The alternative selector spellings the reference accepts are accepted here too.
    /// </summary>
    /// <remarks>
    /// Parity, not politeness. The point of copying this contract is that one operator configures
    /// both repositories identically; a value that works there and fails closed here is exactly the
    /// surprise a shared contract exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("azure")]
    [InlineData("azure-openai")]
    [InlineData("azureopenai")]
    [InlineData("AZURE")]
    public void TheAzureSpellingsAllResolveAzure(string token)
    {
        InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", token),
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", "k"),
            ("AZURE_OPENAI_DEPLOYMENT", "d")))
            .Settings!.Provider.Should().Be(InferenceProvider.AzureOpenAI);
    }

    /// <summary>Same for the other providers' alternative spellings.</summary>
    [Theory]
    [InlineData("foundry", InferenceProvider.Foundry)]
    [InlineData("azure-foundry", InferenceProvider.Foundry)]
    [InlineData("azure-ai-foundry", InferenceProvider.Foundry)]
    [InlineData("openai-compatible", InferenceProvider.OpenAICompatible)]
    [InlineData("openai_compatible", InferenceProvider.OpenAICompatible)]
    [InlineData("compatible", InferenceProvider.OpenAICompatible)]
    [InlineData("openai-compat", InferenceProvider.OpenAICompatible)]
    public void TheOtherSpellingsResolveToo(string token, InferenceProvider expected)
    {
        InferenceProviderNames.TryParse(token, out var parsed).Should().BeTrue();
        parsed.Should().Be(expected);
    }

    /// <summary>
    /// A JUDGE ENDPOINT GOES THROUGH THE SAME POLICY AS EVERY OTHER ONE.
    /// </summary>
    /// <remarks>
    /// The port guide records this exact bug in the reference implementation: the judge branch built
    /// its client directly, so it accepted a plain-http remote endpoint and sent the judge key in
    /// cleartext while the generic path refused the same URL. It is the one path that still names a
    /// host directly, which is precisely why the rule is easy to forget there.
    /// </remarks>
    [Fact]
    public void ACleartextJudgeEndpointIsRefused()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("BITDEER_API_KEY", "k"),
            ("AI_JUDGE_PROVIDER", "openai"),
            ("AI_JUDGE_ENDPOINT", "http://remote-judge/v1"),
            ("AI_JUDGE_API_KEY", "sk-judge-secret"),
            ("AI_JUDGE_MODEL", "gpt-4o")));

        resolution.Settings!.HasJudgeOverride.Should().BeFalse(
            "a refused override means NO judge, never a silently downgraded one");
        resolution.JudgeDiagnostic.Should().Contain("cleartext").And.NotContain("sk-judge-secret");
    }

    /// <summary>An https judge endpoint is accepted, so the refusal above is about the scheme.</summary>
    [Fact]
    public void AnHttpsJudgeEndpointIsAccepted()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("BITDEER_API_KEY", "k"),
            ("AI_JUDGE_PROVIDER", "openai"),
            ("AI_JUDGE_ENDPOINT", "https://api.openai.com/v1"),
            ("AI_JUDGE_API_KEY", "sk-judge"),
            ("AI_JUDGE_MODEL", "gpt-4o")));

        resolution.Settings!.HasJudgeOverride.Should().BeTrue();
        resolution.JudgeDiagnostic.Should().BeNull();
    }

    /// <summary>The comparison slots fall back to the primary, never to null.</summary>
    [Fact]
    public void TheComparisonSlotsFallBackToThePrimary()
    {
        var settings = InferenceProviderEnvironment.Resolve(Env(("BITDEER_API_KEY", "k"))).Settings!;

        settings.Model2.Should().Be(settings.Model);
        settings.Model3.Should().Be(settings.Model);
    }

    /// <summary>Azure's comparison slots have their own defaults, as the reference defines them.</summary>
    [Fact]
    public void AzureComparisonSlotsUseItsOwnDefaults()
    {
        var settings = InferenceProviderEnvironment.Resolve(Env(
            ("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/"),
            ("AZURE_OPENAI_API_KEY", "k"),
            ("AZURE_OPENAI_DEPLOYMENT", "my-deployment"))).Settings!;

        settings.Model2.Should().Be("gpt-4o-mini");
        settings.Model3.Should().Be("gpt-4.1");
    }

    /// <summary>The banner records HOW the provider was chosen, not only which one.</summary>
    /// <remarks>
    /// A run that auto-detected Azure from a stale variable nobody unset looks identical, in every
    /// other respect, to one that chose it.
    /// </remarks>
    [Fact]
    public void SelectionRecordsWhetherItWasChosenOrDetected()
    {
        InferenceProviderEnvironment.Resolve(Env(("BITDEER_API_KEY", "k")))
            .Settings!.Selection.Should().Be(InferenceProviderSelection.AutoDetected);

        var explicitly = InferenceProviderEnvironment.Resolve(Env(
            ("AI_INFERENCE_PROVIDER", "bitdeer"), ("BITDEER_API_KEY", "k"))).Settings!;

        explicitly.Selection.Should().Be(InferenceProviderSelection.Explicit);
        explicitly.Summary.Should().Contain("AI_INFERENCE_PROVIDER=bitdeer")
            .And.Contain("Bitdeer AI Model Studio");
    }

    /// <summary>The settings never print the key, however they are stringified.</summary>
    [Fact]
    public void SettingsRedactTheKeyWhenPrinted()
    {
        var resolution = InferenceProviderEnvironment.Resolve(Env(
            ("BITDEER_API_KEY", "sk-do-not-print-me")));

        resolution.Settings!.ToString().Should().NotContain("sk-do-not-print-me").And.Contain("***");
    }
}

/// <summary>The one path that reads the real process environment.</summary>
/// <remarks>
/// Serialised, because environment variables are process-global. It exists so the parameterless
/// <see cref="InferenceProviderEnvironment.Resolve()"/> — the overload every host actually calls —
/// is covered by something, rather than only its delegate sibling.
/// </remarks>
[Collection(EnvVarTestsCollection.Name)]
public sealed class RealEnvironmentResolutionTests
{
    /// <summary>The parameterless overload reads the process environment.</summary>
    [Fact]
    public void ResolveReadsTheProcessEnvironment()
    {
        using var scope = new ProviderEnvironmentScope().Set("BITDEER_API_KEY", "k");

        InferenceProviderEnvironment.Resolve().Settings!.Provider.Should().Be(InferenceProvider.Bitdeer);
    }

    /// <summary>With the whole list scrubbed, a clean machine resolves to nothing.</summary>
    /// <remarks>
    /// Also proves the scrubber works: if it missed a variable that a developer happens to have set,
    /// this resolves to that provider and fails here rather than somewhere confusing.
    /// </remarks>
    [Fact]
    public void AScrubbedEnvironmentResolvesToNothing()
    {
        using var scope = new ProviderEnvironmentScope();

        InferenceProviderEnvironment.Resolve().IsConfigured.Should().BeFalse();
    }
}
