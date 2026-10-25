using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;
using Microsoft.Extensions.Options;

namespace AgentMemory.Examples.RepositoryLessons;

/// <summary>
/// Repository lessons: what a future task in a repository needs and cannot easily rediscover (a failure signature, the
/// fix that resolved it, a convention, a pitfall). A model writes them through the store-aware lane: it sees the lessons
/// already kept and the turn's core write, updates a lesson under its own key, and names the lessons a new one replaces.
/// </summary>
/// <remarks>Declares host feature Inference: the host grants the chat model (<c>ModuleSetBuilder.GrantChatClient</c>).</remarks>
public sealed class RepositoryLessonsModule : IAgentMemoryModule
{
    /// <summary>The module identity.</summary>
    public const string Id = "io.agentmemory.examples.lessons";
    /// <summary>The one record type.</summary>
    public const string Type = "LessonItem";
    /// <summary>The view of current lessons: what the writer sees and what recall offers.</summary>
    public const string CurrentView = "current";

    private static readonly Lazy<ModuleManifest> Metadata = new(() => ModuleManifest.Load(typeof(RepositoryLessonsModule).Assembly));

    /// <summary>The embedded manifest.</summary>
    public static ModuleManifest Manifest => Metadata.Value;

    /// <inheritdoc/>
    public void Configure(IModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Configure(new RepositoryLessonsOptionsValidator());
        builder.AddIngestionProvider(c => new LessonWriter(c.ChatClient, c.GetOptions<RepositoryLessonsOptions>()));
        builder.AddContextContributor(c => new ViewContributor(c.Resolution, c.Reader, CurrentView));
    }
}

/// <summary>The module's options, bound from <c>AgentMemory:Modules:io.agentmemory.examples.lessons</c>.</summary>
public sealed class RepositoryLessonsOptions
{
    /// <summary>The repository this host works in; when set it is the repository of every lesson.</summary>
    public string? Repository { get; set; }

    /// <summary>At most this many lessons a turn.</summary>
    public int MaxLessonsPerTurn { get; set; } = 5;
}

/// <summary>Bounds the module's options.</summary>
public sealed class RepositoryLessonsOptionsValidator : IValidateOptions<RepositoryLessonsOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, RepositoryLessonsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.MaxLessonsPerTurn is < 1 or > 20) failures.Add("MaxLessonsPerTurn must be between 1 and 20.");
        if (options.Repository is { Length: > 200 }) failures.Add("Repository must be at most 200 characters.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
