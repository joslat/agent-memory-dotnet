using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentMemory.Inference;

/// <summary>Options for <see cref="ServiceCollectionExtensions.AddAgentMemoryInferenceFromEnvironment"/>.</summary>
public sealed class InferenceRegistrationOptions
{
    /// <summary>
    /// Whether an embedding generator is required. Default true.
    /// </summary>
    /// <remarks>
    /// True by default because AgentMemory's vector search is core: a host that starts without
    /// embeddings does not fail, it silently retrieves nothing. A chat-only consumer sets this false
    /// deliberately.
    /// </remarks>
    public bool RequireEmbeddings { get; set; } = true;

    /// <summary>
    /// Where variables are read from. Default is the process environment.
    /// </summary>
    /// <remarks>Exists so a host's own configuration system, or a test, can supply them instead.</remarks>
    public Func<string, string?> ReadVariable { get; set; } = Environment.GetEnvironmentVariable;
}

/// <summary>Registers inference clients resolved from the environment contract.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Resolves the provider and registers <see cref="InferenceProviderSettings"/>,
    /// <see cref="IChatClient"/> and <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fails at registration, not at first use.</b> A misconfigured host that starts cleanly and
    /// throws on the first user turn has moved the error from the operator who can fix it to the user
    /// who cannot. The exception carries the resolver's diagnostic, which names the missing variables.
    /// </para>
    /// <para>
    /// The settings are registered as a singleton and the clients are resolved from them, so
    /// everything in the process agrees about which host it is talking to. Applying
    /// <c>EmbeddingDimensions</c> to <c>Neo4jOptions</c> is the HOST's job — this package holds no
    /// reference to AgentMemory and is usable by anyone.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No provider resolved, or embeddings are required and absent.</exception>
    public static IServiceCollection AddAgentMemoryInferenceFromEnvironment(
        this IServiceCollection services, Action<InferenceRegistrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new InferenceRegistrationOptions();
        configure?.Invoke(options);

        var resolution = InferenceProviderEnvironment.Resolve(options.ReadVariable);
        if (resolution.Settings is not { } settings)
        {
            throw new InvalidOperationException(resolution.Diagnostic);
        }

        if (options.RequireEmbeddings && !settings.HasEmbeddings)
        {
            throw new InvalidOperationException(
                resolution.EmbeddingDiagnostic
                ?? "An embedding model is required and none is configured.");
        }

        services.TryAddSingleton(settings);

        services.TryAddSingleton<IChatClient>(_ =>
            InferenceClientFactory.TryCreateChatClient(settings, "chat", out var chat, out var diagnostic)
                ? chat
                : throw new InvalidOperationException(diagnostic));

        if (settings.HasEmbeddings)
        {
            services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
                InferenceClientFactory.TryCreateEmbeddingGenerator(
                    settings, out var embeddings, out var diagnostic)
                    ? embeddings
                    : throw new InvalidOperationException(diagnostic));
        }

        return services;
    }
}
