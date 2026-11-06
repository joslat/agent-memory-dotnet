// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;
using Microsoft.Extensions.Options;

namespace AgentMemory.Packs.Repository;

/// <summary>
/// The repository-experience memory pack (Extensibility 0.13 §C): what a coding agent learns in a repository and would
/// otherwise rediscover, such as a failure and its fix, a command that works here, a convention, a pitfall, or an
/// environment constraint. A model writes the lessons through the store-aware lane: it sees the lessons already kept and the
/// turn's core write, updates a lesson under its own key, and names the lessons a new one replaces. The commit a lesson was
/// seen at comes from the host (<c>IngestionRequest.HostAttributes["commit"]</c>), never from the model.
/// </summary>
/// <remarks>
/// Declares host feature Inference: the host grants the chat model (<c>ModuleSetBuilder.GrantChatClient</c>). Recall uses the
/// <see cref="RelevantView"/> search view, so the gate judges the few lessons that match a turn instead of every lesson.
/// Lessons carry no "verified" flag: no host records build or test outcomes, and a model marks every lesson verified
/// (EXT-measure M1).
/// </remarks>
[Experimental("AMEXT001")]
public sealed class RepositoryPack : IAgentMemoryModule
{
    /// <summary>The module identity.</summary>
    public const string Id = "io.agentmemory.packs.repository";
    /// <summary>The one record type.</summary>
    public const string Type = "RepoLesson";
    /// <summary>The current lessons: what the writer sees before it writes.</summary>
    public const string CurrentView = "current";
    /// <summary>The search view recall uses.</summary>
    public const string RelevantView = "relevant";
    /// <summary>The host attribute that carries the turn's commit.</summary>
    public const string CommitAttribute = "commit";
    /// <summary>The host attribute that names the repository, when the host knows it.</summary>
    public const string RepositoryAttribute = "repository";

    private static readonly Lazy<ModuleManifest> Metadata = new(() => ModuleManifest.Load(typeof(RepositoryPack).Assembly));

    /// <summary>The embedded manifest.</summary>
    public static ModuleManifest Manifest => Metadata.Value;

    /// <inheritdoc/>
    public void Configure(IModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Configure(new RepositoryPackOptionsValidator());
        builder.AddIngestionProvider(c => new RepositoryWriter(c.ChatClient, c.GetOptions<RepositoryPackOptions>()));
        builder.AddContextContributor(c => new ViewContributor(c.Resolution, c.Reader, RelevantView));
    }
}

/// <summary>The pack's options, bound from <c>AgentMemory:Modules:io.agentmemory.packs.repository</c>.</summary>
[Experimental("AMEXT001")]
public sealed class RepositoryPackOptions
{
    /// <summary>The repository this host works in; when set it is the repository of every lesson.</summary>
    public string? Repository { get; set; }

    /// <summary>At most this many lessons a turn.</summary>
    public int MaxLessonsPerTurn { get; set; } = 5;
}

/// <summary>Bounds the pack's options.</summary>
[Experimental("AMEXT001")]
public sealed class RepositoryPackOptionsValidator : IValidateOptions<RepositoryPackOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, RepositoryPackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.MaxLessonsPerTurn is < 1 or > 20) failures.Add("MaxLessonsPerTurn must be between 1 and 20.");
        if (options.Repository is { Length: > 200 }) failures.Add("Repository must be at most 200 characters.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
