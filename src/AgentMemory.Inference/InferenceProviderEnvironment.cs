using System.Globalization;

namespace AgentMemory.Inference;

/// <summary>The outcome of reading the environment: settings, or the reason there are none.</summary>
/// <remarks>
/// The two halves report separately because they fail separately. A machine can have a perfectly
/// good chat provider and no embedding model, and a host that needs both should be told which one is
/// missing rather than "nothing configured".
/// </remarks>
public sealed record InferenceResolution
{
    /// <summary>The resolved settings, or null when no chat provider could be resolved.</summary>
    public InferenceProviderSettings? Settings { get; init; }

    /// <summary>Why nothing was resolved, or what was chosen. Never contains a key or a raw URI.</summary>
    public required string Diagnostic { get; init; }

    /// <summary>Why embeddings are unconfigured, or null when they are configured.</summary>
    public string? EmbeddingDiagnostic { get; init; }

    /// <summary>
    /// Why the judge override was refused, or null. A refused override means NO judge, not the
    /// subject's own model: a run must not come to grade itself because a URL was mistyped.
    /// </summary>
    public string? JudgeDiagnostic { get; init; }

    /// <summary>Whether a chat provider resolved.</summary>
    public bool IsConfigured => Settings is not null;
}

/// <summary>
/// Turns environment variables into <see cref="InferenceProviderSettings"/>, by four rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>1. Explicit beats detected.</b> <c>AI_INFERENCE_PROVIDER</c> names a provider, that provider is
/// used. <b>2. An explicit mistake fails closed</b> — a named provider with missing variables, or an
/// unknown name, resolves to nothing with the reason, never to some other host the operator did not
/// choose. <b>3. Auto-detect in a fixed order when unset:</b> Azure → Bitdeer → OpenAI → Foundry →
/// OpenAI-compatible, first complete one wins; Azure is first so a machine that only ever had
/// <c>AZURE_OPENAI_*</c> behaves exactly as it did before this package existed. Ollama is never
/// auto-detected (name it with <c>AI_INFERENCE_PROVIDER</c> or <c>AI_EMBEDDING_PROVIDER</c>): other tools
/// set <c>OLLAMA_MODEL</c> and <c>OLLAMA_API_KEY</c>, and a leftover must not choose a provider. <b>4. A
/// half-configured provider is a typo, not an unchosen one</b> — it names the missing variables of
/// THAT provider rather than reporting a bare "nothing configured".
/// </para>
/// <para>
/// <b>Every variable is read through <see cref="Specs"/> and the blocks below, and
/// <see cref="AllVariables"/> is built from the same place.</b> The obvious way to write this is a
/// switch that reads names and a separate list that repeats them — and then the list drifts, the
/// test scrubber misses a variable, and an ambient <c>OPENAI_API_KEY</c> on a developer's machine
/// quietly configures a provider in a test that thought it had a clean environment.
/// </para>
/// </remarks>
public static class InferenceProviderEnvironment
{
    /// <summary>The selector.</summary>
    public const string SelectorVariable = "AI_INFERENCE_PROVIDER";

    /// <summary>The sentinel an OpenAI-compatible host uses when it needs no key.</summary>
    public const string NoKeySentinel = "no-key-needed";

    private const string ExtractionModelVariable = "AGENTMEMORY_EXTRACTION_MODEL";
    private const string AzureExtractionAlias = "AZURE_OPENAI_EXTRACTION_DEPLOYMENT";
    private const string TimeoutVariable = "AGENTMEMORY_INFERENCE_NETWORK_TIMEOUT_S";
    private const string ShowRawVariable = "AGENTMEMORY_INFERENCE_SHOW_RAW";

    private static readonly string[] JudgeBlock =
        ["AI_JUDGE_PROVIDER", "AI_JUDGE_ENDPOINT", "AI_JUDGE_API_KEY", "AI_JUDGE_MODEL"];

    private static readonly string[] AzureJudgeBlock =
        ["AZURE_OPENAI_JUDGE_ENDPOINT", "AZURE_OPENAI_JUDGE_API_KEY", "AZURE_OPENAI_JUDGE_DEPLOYMENT"];

    private static readonly string[] EmbeddingOverrideBlock =
        ["AI_EMBEDDING_PROVIDER", "AI_EMBEDDING_ENDPOINT", "AI_EMBEDDING_API_KEY", "AI_EMBEDDING_MODEL"];

    private const string EmbeddingDimensionsVariable = "AI_EMBEDDING_DIMENSIONS";

    /// <summary>What one provider is configured by. The single source for reading AND for listing.</summary>
    private sealed record ProviderSpec
    {
        public required InferenceProvider Provider { get; init; }

        /// <summary>The endpoint variable, or null when the provider's endpoint is always defaulted.</summary>
        public string? EndpointVariable { get; init; }

        public required string ApiKeyVariable { get; init; }

        public required string ModelVariable { get; init; }

        public required string EmbeddingModelVariable { get; init; }

        public string? DefaultEndpoint { get; init; }

        public string? DefaultModel { get; init; }

        public string? DefaultEmbeddingModel { get; init; }

        /// <summary>Comparison-slot defaults. Null means "fall back to the primary model".</summary>
        public string? DefaultModel2 { get; init; }

        public string? DefaultModel3 { get; init; }

        /// <summary>True when the host may be keyless (a local server on loopback).</summary>
        public bool KeyOptional { get; init; }

        /// <summary>False for a provider used only when named: its variables alone never select it.</summary>
        public bool AutoDetect { get; init; } = true;

        /// <summary>The comparison slots, kept for AgentEval contract parity.</summary>
        public string Model2Variable => $"{ModelVariable}_2";

        public string Model3Variable => $"{ModelVariable}_3";

        /// <summary>The variables that must be present for this provider to be considered complete.</summary>
        public IEnumerable<string> RequiredVariables
        {
            get
            {
                if (EndpointVariable is { } endpoint && DefaultEndpoint is null) yield return endpoint;
                if (!KeyOptional) yield return ApiKeyVariable;
                if (DefaultModel is null) yield return ModelVariable;
            }
        }

        /// <summary>Every variable this provider reads, required or not.</summary>
        public IEnumerable<string> AllVariables
        {
            get
            {
                if (EndpointVariable is { } endpoint) yield return endpoint;
                yield return ApiKeyVariable;
                yield return ModelVariable;
                yield return Model2Variable;
                yield return Model3Variable;
                yield return EmbeddingModelVariable;
            }
        }
    }

    /// <summary>
    /// The providers, IN AUTO-DETECT ORDER. Azure first, and a test pins that.
    /// </summary>
    private static readonly ProviderSpec[] Specs =
    [
        new()
        {
            Provider = InferenceProvider.AzureOpenAI,
            EndpointVariable = "AZURE_OPENAI_ENDPOINT",
            ApiKeyVariable = "AZURE_OPENAI_API_KEY",
            // Azure names DEPLOYMENTS, not models. The variable name is the existing one and does not
            // change: a machine configured before this package must not need editing.
            ModelVariable = "AZURE_OPENAI_DEPLOYMENT",
            EmbeddingModelVariable = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
            DefaultEmbeddingModel = "text-embedding-ada-002",
            DefaultModel2 = "gpt-4o-mini",
            DefaultModel3 = "gpt-4.1",
        },
        new()
        {
            Provider = InferenceProvider.Bitdeer,
            EndpointVariable = "BITDEER_ENDPOINT",
            ApiKeyVariable = "BITDEER_API_KEY",
            ModelVariable = "BITDEER_MODEL",
            EmbeddingModelVariable = "BITDEER_EMBEDDING_MODEL",
            // ONE VARIABLE IS THE WHOLE POINT: BITDEER_API_KEY alone gets chat and embeddings.
            DefaultEndpoint = "https://api-inference.bitdeer.ai/v1",
            DefaultModel = "zai-org/GLM-5.3-Flash",
            DefaultEmbeddingModel = "BAAI/bge-m3",
        },
        new()
        {
            Provider = InferenceProvider.OpenAI,
            EndpointVariable = "OPENAI_BASE_URL",
            ApiKeyVariable = "OPENAI_API_KEY",
            ModelVariable = "OPENAI_MODEL",
            EmbeddingModelVariable = "OPENAI_EMBEDDING_MODEL",
            DefaultEndpoint = "https://api.openai.com/v1",
            DefaultModel = "gpt-4o-mini",
            DefaultEmbeddingModel = "text-embedding-3-small",
        },
        new()
        {
            Provider = InferenceProvider.Foundry,
            EndpointVariable = "FOUNDRY_ENDPOINT",
            ApiKeyVariable = "FOUNDRY_API_KEY",
            ModelVariable = "FOUNDRY_MODEL",
            EmbeddingModelVariable = "FOUNDRY_EMBEDDING_MODEL",
        },
        new()
        {
            Provider = InferenceProvider.OpenAICompatible,
            EndpointVariable = "OPENAI_COMPATIBLE_ENDPOINT",
            ApiKeyVariable = "OPENAI_COMPATIBLE_API_KEY",
            ModelVariable = "OPENAI_COMPATIBLE_MODEL",
            EmbeddingModelVariable = "OPENAI_COMPATIBLE_EMBEDDING_MODEL",
            // A local server often needs no key; the sentinel keeps "unset" meaning "not configured".
            KeyOptional = true,
        },
        new()
        {
            // NEVER AUTO-DETECTED: used only when named (AI_INFERENCE_PROVIDER=ollama or
            // AI_EMBEDDING_PROVIDER=ollama), because other tools set OLLAMA_MODEL / OLLAMA_API_KEY and a
            // leftover must neither choose a provider nor mask a half-configured one. Chat needs
            // OLLAMA_MODEL; embeddings default to bge-m3 (the model Bitdeer serves as BAAI/bge-m3: same
            // vectors, same width).
            Provider = InferenceProvider.Ollama,
            AutoDetect = false,
            EndpointVariable = "OLLAMA_ENDPOINT",
            // 127.0.0.1, NOT localhost: on Windows "localhost" tries IPv6 first and Ollama listens on
            // IPv4 only, which costs ~2 s per request before the fallback (measured: 2,090 ms vs 52 ms).
            DefaultEndpoint = "http://127.0.0.1:11434/v1",
            ApiKeyVariable = "OLLAMA_API_KEY",
            ModelVariable = "OLLAMA_MODEL",
            EmbeddingModelVariable = "OLLAMA_EMBEDDING_MODEL",
            DefaultEmbeddingModel = "bge-m3",
            KeyOptional = true,
        },
    ];

    /// <summary>
    /// EVERY variable this resolver reads — required, optional, embedding, judge, role and tuning.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="Specs"/> rather than retyped, so it cannot fall behind them. Tests scrub
    /// this whole list: a variable missing from it is a variable that leaks in from the developer's
    /// own machine and configures a provider the test never set.
    /// </remarks>
    public static IReadOnlyList<string> AllVariables { get; } =
    [
        SelectorVariable,
        .. Specs.SelectMany(s => s.AllVariables),
        .. EmbeddingOverrideBlock,
        EmbeddingDimensionsVariable,
        .. JudgeBlock,
        .. AzureJudgeBlock,
        ExtractionModelVariable,
        AzureExtractionAlias,
        TimeoutVariable,
        ShowRawVariable,
    ];

    /// <summary>Whether the operator set anything at all this resolver looks at.</summary>
    /// <remarks>
    /// The difference between "this machine has no model configured" and "this machine has a typo".
    /// Rule 4 turns on it.
    /// </remarks>
    public static bool AnyConfigurationAttempted(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return AllVariables.Any(v => !string.IsNullOrWhiteSpace(read(v)));
    }

    /// <summary>Reads the process environment.</summary>
    public static InferenceResolution Resolve() => Resolve(Environment.GetEnvironmentVariable);

    /// <summary>Reads through a delegate, which is how this is tested without touching the process.</summary>
    public static InferenceResolution Resolve(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var selector = Value(read, SelectorVariable);

        // RULE 1 and RULE 2. An explicitly named provider is used or fails; it never falls through to
        // auto-detect, because falling through would silently send traffic (and a key) to a host the
        // operator did not choose.
        if (selector is not null)
        {
            if (!InferenceProviderNames.TryParse(selector, out var chosen))
            {
                return Failure(
                    $"{SelectorVariable} is '{selector}', which is not a provider. "
                    + $"Expected one of: {string.Join(", ", InferenceProviderNames.AllTokens)}.");
            }

            var spec = Specs.Single(s => s.Provider == chosen);
            var missing = MissingRequired(read, spec).ToArray();
            return missing.Length > 0
                ? Failure(
                    $"{SelectorVariable}={InferenceProviderNames.ToToken(chosen)} but "
                    + $"{Plural(missing.Length, "variable is", "variables are")} not set: {string.Join(", ", missing)}.")
                : Build(read, spec, InferenceProviderSelection.Explicit);
        }

        // RULE 3. Fixed order, first complete one wins.
        foreach (var spec in Specs.Where(s => s.AutoDetect))
        {
            if (!MissingRequired(read, spec).Any())
            {
                return Build(read, spec, InferenceProviderSelection.AutoDetected);
            }
        }

        // RULE 4. Nothing is complete; if something was attempted, name what THAT provider is missing.
        foreach (var spec in Specs.Where(s => s.AutoDetect))
        {
            if (!spec.AllVariables.Any(v => Value(read, v) is not null)) continue;

            var missing = MissingRequired(read, spec).ToArray();
            return Failure(
                $"{InferenceProviderNames.ToToken(spec.Provider)} looks half-configured: "
                + $"{string.Join(", ", missing)} {Plural(missing.Length, "is", "are")} not set. "
                + $"Set {Plural(missing.Length, "it", "them")}, or set {SelectorVariable} to choose a different provider.");
        }

        return Failure(
            "No inference provider is configured. Set one of: "
            + "AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT (azure); "
            + "BITDEER_API_KEY (bitdeer); OPENAI_API_KEY (openai); "
            + "FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL (foundry); "
            + "OPENAI_COMPATIBLE_ENDPOINT + OPENAI_COMPATIBLE_MODEL (openai-compatible); "
            + "AI_INFERENCE_PROVIDER=ollama + OLLAMA_MODEL (ollama). "
            + $"Or name one explicitly with {SelectorVariable}.");
    }

    private static InferenceResolution Build(
        Func<string, string?> read, ProviderSpec spec, InferenceProviderSelection selection)
    {
        var endpoint = (spec.EndpointVariable is { } v ? Value(read, v) : null) ?? spec.DefaultEndpoint;
        var model = Value(read, spec.ModelVariable) ?? spec.DefaultModel;
        var apiKey = Value(read, spec.ApiKeyVariable) ?? (spec.KeyOptional ? NoKeySentinel : null);

        if (endpoint is null || model is null || apiKey is null)
        {
            // Unreachable via Resolve (completeness is checked first) but not via a future caller.
            return Failure(
                $"{InferenceProviderNames.ToToken(spec.Provider)} is incomplete: "
                + $"{string.Join(", ", MissingRequired(read, spec))} not set.");
        }

        // THE KEY TRAVELS TO THIS ENDPOINT, so the endpoint is checked before anything is built.
        if (!InferenceEndpoints.TryValidate(
                endpoint, spec.EndpointVariable ?? "endpoint", out var endpointError))
        {
            return Failure(endpointError!);
        }

        var (embedding, embeddingDiagnostic) = ResolveEmbeddings(read, spec, endpoint, apiKey);
        var (judge, judgeDiagnostic) = ResolveJudge(read);

        var settings = new InferenceProviderSettings
        {
            Provider = spec.Provider,
            Endpoint = endpoint,
            ApiKey = apiKey,
            Model = model,
            // FALL BACK TO THE PRIMARY, never to null: the contract says a slot with no default of
            // its own takes the primary model "rather than something the operator did not ask for",
            // and a null here makes a comparison run quietly one arm short.
            Model2 = Value(read, spec.Model2Variable) ?? spec.DefaultModel2 ?? model,
            Model3 = Value(read, spec.Model3Variable) ?? spec.DefaultModel3 ?? model,
            Selection = selection,
            ExtractionModel = Value(read, ExtractionModelVariable)
                // The Azure alias is honoured ONLY under Azure: the same name on a Bitdeer machine
                // would be a leftover from a deleted deployment, not an instruction.
                ?? (spec.Provider == InferenceProvider.AzureOpenAI ? Value(read, AzureExtractionAlias) : null),
            JudgeProvider = judge.Provider,
            JudgeEndpoint = judge.Endpoint,
            JudgeApiKey = judge.ApiKey,
            JudgeModel = judge.Model,
            EmbeddingProvider = embedding.Provider,
            EmbeddingEndpoint = embedding.Endpoint,
            EmbeddingApiKey = embedding.ApiKey,
            EmbeddingModel = embedding.Model,
            EmbeddingDimensions = embedding.Dimensions,
            NetworkTimeoutSeconds = PositiveInt(Value(read, TimeoutVariable)) ?? 180,
            ShowRaw = IsTruthy(Value(read, ShowRawVariable)),
        };

        return new InferenceResolution
        {
            Settings = settings,
            Diagnostic = $"Using {settings.Summary}.",
            EmbeddingDiagnostic = embeddingDiagnostic,
            JudgeDiagnostic = judgeDiagnostic,
        };
    }

    private readonly record struct EmbeddingHalf(
        InferenceProvider Provider, string? Endpoint, string? ApiKey, string? Model, int? Dimensions);

    /// <summary>
    /// Embeddings resolve after chat: from the override block when <c>AI_EMBEDDING_PROVIDER</c> names a
    /// provider, else from the chat provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values the block leaves out come from the NAMED provider's own variables and defaults, never from
    /// the chat provider. A block without a provider, or one whose named provider cannot supply a value,
    /// fails the embedding half by name: silently falling back to the chat host would build a store on a
    /// model the operator did not choose.
    /// </para>
    /// <para>
    /// <b>The key comes from the same place as the endpoint.</b> An endpoint from
    /// <c>AI_EMBEDDING_ENDPOINT</c> takes its key only from <c>AI_EMBEDDING_API_KEY</c> (or none, for a
    /// keyless provider); the named provider's own key goes only to the named provider's own endpoint.
    /// Otherwise a leftover <c>AI_EMBEDDING_ENDPOINT</c> plus <c>AI_EMBEDDING_PROVIDER=bitdeer</c> would
    /// send the Bitdeer key to whatever host that endpoint names.
    /// </para>
    /// </remarks>
    private static (EmbeddingHalf Half, string? Diagnostic) ResolveEmbeddings(
        Func<string, string?> read, ProviderSpec spec, string chatEndpoint, string chatApiKey)
    {
        var none = new EmbeddingHalf(InferenceProvider.None, null, null, null, null);
        var declared = EmbeddingOverrideBlock.Where(v => Value(read, v) is not null).ToArray();

        InferenceProvider provider;
        string endpoint, apiKey, model;

        if (declared.Length > 0)
        {
            // The block needs a provider. What it leaves out is taken from THAT provider's own settings
            // (its endpoint, key and embedding model variables, then its defaults), so
            // `AI_EMBEDDING_PROVIDER=ollama` alone moves embeddings to a local server, and
            // `AI_EMBEDDING_PROVIDER=bitdeer` alone back. Anything still missing fails by name: a half-
            // specified block is never completed from the CHAT provider, which is not what was named.
            var providerValue = Value(read, "AI_EMBEDDING_PROVIDER");
            if (providerValue is null)
            {
                return (none,
                    "The embedding override block is incomplete: AI_EMBEDDING_PROVIDER not set. It names "
                    + $"the provider the other {string.Join(", ", EmbeddingOverrideBlock.Skip(1))} belong to.");
            }

            if (!InferenceProviderNames.TryParse(providerValue, out provider))
            {
                return (none,
                    $"AI_EMBEDDING_PROVIDER is '{providerValue}', which is not a provider. "
                    + $"Expected one of: {string.Join(", ", InferenceProviderNames.AllTokens)}.");
            }

            var named = Specs.Single(s => s.Provider == provider);

            // Endpoint and key travel together (see remarks): an override endpoint never receives the named
            // provider's own key.
            string? chosenEndpoint, chosenKey, endpointVariable, keyHint;
            if (Value(read, "AI_EMBEDDING_ENDPOINT") is { } overrideEndpoint)
            {
                chosenEndpoint = overrideEndpoint;
                endpointVariable = "AI_EMBEDDING_ENDPOINT";
                chosenKey = Value(read, "AI_EMBEDDING_API_KEY") ?? (named.KeyOptional ? NoKeySentinel : null);
                keyHint = "AI_EMBEDDING_API_KEY (an AI_EMBEDDING_ENDPOINT never receives "
                    + $"{named.ApiKeyVariable}: the key must be issued for that endpoint)";
            }
            else
            {
                var ownEndpoint = named.EndpointVariable is { } ev ? Value(read, ev) : null;
                chosenEndpoint = ownEndpoint ?? named.DefaultEndpoint;
                endpointVariable = ownEndpoint is not null ? named.EndpointVariable : $"{named.EndpointVariable ?? "the endpoint"} (default)";
                chosenKey = Value(read, "AI_EMBEDDING_API_KEY")
                    ?? Value(read, named.ApiKeyVariable) ?? (named.KeyOptional ? NoKeySentinel : null);
                keyHint = $"AI_EMBEDDING_API_KEY (or {named.ApiKeyVariable})";
            }
            var chosenModel = Value(read, "AI_EMBEDDING_MODEL")
                ?? Value(read, named.EmbeddingModelVariable) ?? named.DefaultEmbeddingModel;

            var unresolved = new List<string>();
            if (chosenEndpoint is null) unresolved.Add($"AI_EMBEDDING_ENDPOINT (or {named.EndpointVariable})");
            if (chosenKey is null) unresolved.Add(keyHint);
            if (chosenModel is null) unresolved.Add($"AI_EMBEDDING_MODEL (or {named.EmbeddingModelVariable})");
            if (unresolved.Count > 0)
            {
                return (none,
                    $"AI_EMBEDDING_PROVIDER={InferenceProviderNames.ToToken(provider)} but "
                    + $"{string.Join(", ", unresolved)} {Plural(unresolved.Count, "is", "are")} not set.");
            }

            endpoint = chosenEndpoint!;
            apiKey = chosenKey!;
            model = chosenModel!;

            if (!InferenceEndpoints.TryValidate(endpoint, endpointVariable!, out var overrideError))
            {
                return (none, overrideError);
            }
        }
        else
        {
            var chatModel = Value(read, spec.EmbeddingModelVariable) ?? spec.DefaultEmbeddingModel;
            if (chatModel is null)
            {
                return (none,
                    $"No embedding model. Set {spec.EmbeddingModelVariable}, or point embeddings "
                    + $"elsewhere with {string.Join(" + ", EmbeddingOverrideBlock)}.");
            }

            provider = spec.Provider;
            endpoint = chatEndpoint;
            apiKey = chatApiKey;
            model = chatModel;
        }

        // THE DIMENSION IS STORE-DEFINING, so an explicit value wins and an unknown one stops here.
        //
        // AN EXPLICIT VALUE THAT CANNOT BE PARSED IS A FAILURE, NOT AN ABSENCE. Falling back to the
        // table on `AI_EMBEDDING_DIMENSIONS=512.0` would take the operator's deliberate override,
        // discard it for a typo, and build the store at a width they did not choose -- silently, and
        // in the one place this package refuses to guess. "Set but unreadable" and "not set" are
        // different states and only the second may fall through.
        var rawDimensions = Value(read, EmbeddingDimensionsVariable);
        if (rawDimensions is not null && PositiveInt(rawDimensions) is null)
        {
            return (none,
                $"{EmbeddingDimensionsVariable} is set but is not a positive whole number. The "
                + "embedding width defines the vector index, so a value that cannot be read is "
                + "refused rather than ignored in favour of the built-in table.");
        }

        var dimensions = PositiveInt(rawDimensions);
        if (dimensions is null && !TryKnownDimensions(model, out dimensions))
        {
            return (none,
                $"The embedding dimension for '{model}' is not known to this package, and "
                + $"{EmbeddingDimensionsVariable} is not set. The dimension defines the vector index, "
                + "so it is not guessed. Set it explicitly, or use one of: "
                + $"{string.Join(", ", KnownEmbeddingDimensions.KnownModels)}.");
        }

        return (new EmbeddingHalf(provider, endpoint, apiKey, model, dimensions), null);
    }

    private static bool TryKnownDimensions(string model, out int? dimensions)
    {
        if (KnownEmbeddingDimensions.TryGet(model, out var known))
        {
            dimensions = known;
            return true;
        }

        dimensions = null;
        return false;
    }

    private readonly record struct JudgeHalf(
        InferenceProvider Provider, string? Endpoint, string? ApiKey, string? Model);

    /// <summary>The judge override: the generic block, else AgentEval's Azure-shaped one.</summary>
    /// <remarks>
    /// <b>THE JUDGE ENDPOINT GOES THROUGH THE SAME POLICY AS EVERY OTHER ONE.</b> This is the one
    /// path that still names a host directly — naming a judge endpoint is the entire point of it —
    /// and it is therefore the one path where the endpoint rules are easiest to forget. The port
    /// guide records that exact bug happening in the reference implementation: the judge branch
    /// built its client directly, accepted a plain-http remote endpoint, and sent the judge key in
    /// cleartext while the generic path refused the same URL. A refused judge override returns NO
    /// judge rather than a silently-downgraded one: falling back to the subject's own model would
    /// mean a run graded itself because a URL was mistyped.
    /// </remarks>
    private static (JudgeHalf Half, string? Diagnostic) ResolveJudge(Func<string, string?> read)
    {
        // A PARTIAL BLOCK IS THE DANGEROUS CASE, because it looks configured from the outside. An
        // operator who set three of the four believes the judge is independent; silently running the
        // subject as its own judge produces a self-graded score that reads like an arm's-length one.
        // Same rule the embedding override already follows: name what is missing, yield no judge.
        var judgeDeclared = JudgeBlock.Where(v => Value(read, v) is not null).ToArray();
        if (judgeDeclared.Length > 0 && judgeDeclared.Length < JudgeBlock.Length)
        {
            return (NoJudge,
                "The judge override block is incomplete: "
                + $"{string.Join(", ", JudgeBlock.Where(v => Value(read, v) is null))} not set. "
                + $"All of {string.Join(", ", JudgeBlock)} are required together.");
        }

        var azureJudgeDeclared = AzureJudgeBlock.Where(v => Value(read, v) is not null).ToArray();
        if (judgeDeclared.Length == 0
            && azureJudgeDeclared.Length > 0 && azureJudgeDeclared.Length < AzureJudgeBlock.Length)
        {
            return (NoJudge,
                "The Azure-shaped judge override block is incomplete: "
                + $"{string.Join(", ", AzureJudgeBlock.Where(v => Value(read, v) is null))} not set. "
                + $"All of {string.Join(", ", AzureJudgeBlock)} are required together.");
        }

        if (JudgeBlock.All(v => Value(read, v) is not null))
        {
            if (!InferenceProviderNames.TryParse(Value(read, "AI_JUDGE_PROVIDER"), out var provider))
            {
                return (NoJudge,
                    $"AI_JUDGE_PROVIDER is '{Value(read, "AI_JUDGE_PROVIDER")}', which is not a provider. "
                    + $"Expected one of: {string.Join(", ", InferenceProviderNames.AllTokens)}.");
            }

            var endpoint = Value(read, "AI_JUDGE_ENDPOINT")!;
            if (!InferenceEndpoints.TryValidate(endpoint, "AI_JUDGE_ENDPOINT", out var judgeError))
            {
                return (NoJudge, judgeError);
            }

            return (new JudgeHalf(
                provider, endpoint,
                Value(read, "AI_JUDGE_API_KEY"), Value(read, "AI_JUDGE_MODEL")), null);
        }

        // Honoured for AgentEval parity so one operator configures both repositories identically.
        if (AzureJudgeBlock.All(v => Value(read, v) is not null))
        {
            var endpoint = Value(read, "AZURE_OPENAI_JUDGE_ENDPOINT")!;
            if (!InferenceEndpoints.TryValidate(endpoint, "AZURE_OPENAI_JUDGE_ENDPOINT", out var azureError))
            {
                return (NoJudge, azureError);
            }

            return (new JudgeHalf(
                InferenceProvider.AzureOpenAI, endpoint,
                Value(read, "AZURE_OPENAI_JUDGE_API_KEY"), Value(read, "AZURE_OPENAI_JUDGE_DEPLOYMENT")), null);
        }

        return (NoJudge, null);
    }

    private static JudgeHalf NoJudge => new(InferenceProvider.None, null, null, null);

    private static IEnumerable<string> MissingRequired(Func<string, string?> read, ProviderSpec spec) =>
        spec.RequiredVariables.Where(v => Value(read, v) is null);

    /// <summary>A variable's value, or null when unset OR whitespace — those mean the same thing here.</summary>
    private static string? Value(Func<string, string?> read, string name)
    {
        var raw = read(name);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>
    /// A positive whole number, parsed the same way on every machine.
    /// </summary>
    /// <remarks>
    /// <b>Invariant culture and <see cref="NumberStyles.None"/>, both deliberate.</b> The default
    /// overload reads the CURRENT culture's <c>NumberFormatInfo</c>, so the same variable could parse
    /// on one machine and not another — and this feeds <c>AI_EMBEDDING_DIMENSIONS</c>, where the
    /// whole contract is that a value is either read exactly or refused. A fail-closed rule that
    /// depends on the operator's locale is not one. <c>None</c> additionally rejects signs and
    /// separators outright, so <c>-1</c> and <c>1,024</c> fail at the parse rather than at the
    /// positivity check — the refusal then says "not a positive whole number" for the same reason it
    /// is true, in every culture. This repository has already swept for culture-dependent formatting
    /// once (#69); this is the same class arriving in a new package.
    /// </remarks>
    private static int? PositiveInt(string? raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;

    private static bool IsTruthy(string? raw) =>
        raw is not null
        && (raw.Equals("1", StringComparison.Ordinal)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;

    private static InferenceResolution Failure(string diagnostic) =>
        new() { Settings = null, Diagnostic = diagnostic, EmbeddingDiagnostic = null };
}
