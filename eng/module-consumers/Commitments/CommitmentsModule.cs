using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;

namespace AgentMemory.Examples.Commitments;

/// <summary>Static authoring surface. Runtime trust, activation and service lifetimes belong to the host.</summary>
public static class CommitmentsModule
{
    public const string Id = "io.agentmemory.examples.commitments";
    public const string Type = "CommitmentItem";
    public const string About = "COMMITMENT_ABOUT";
    private static readonly Lazy<ModuleManifest> Metadata = new(() => ModuleManifest.Load(typeof(CommitmentsModule).Assembly));
    public static ModuleManifest Manifest => Metadata.Value;

    /// <summary>The host supplies a selected scoped entity identity; a missing selection performs no read.</summary>
    public static ViewContributor ForEntity(ModuleResolution resolution, IModuleReader reader, Func<ContextRequest, string?> selectEntity, int priority = 100)
    {
        ArgumentNullException.ThrowIfNull(selectEntity);
        return Contributor(resolution, reader, "open-for-entity", priority, request =>
        {
            var entity = selectEntity(request);
            return string.IsNullOrWhiteSpace(entity) ? null : new() { Links = [new() { Relation = About, TargetId = entity }] };
        });
    }
    /// <summary>Uses the reader's clock or the request's system-time pin for the declared due view.</summary>
    public static ViewContributor Due(ModuleResolution resolution, IModuleReader reader, int priority = 100) =>
        Contributor(resolution, reader, "due", priority, _ => new());

    /// <summary>The host supplies one logical key; it cannot select another module or owner.</summary>
    public static ViewContributor History(ModuleResolution resolution, IModuleReader reader, Func<ContextRequest, string?> selectKey, int priority = 100)
    {
        ArgumentNullException.ThrowIfNull(selectKey);
        return Contributor(resolution, reader, "history", priority, request =>
        {
            var key = selectKey(request);
            return string.IsNullOrWhiteSpace(key) ? null : new() { Key = key };
        });
    }
    private static ViewContributor Contributor(ModuleResolution resolution, IModuleReader reader, string view, int priority,
        Func<ContextRequest, ModuleViewArguments?> arguments)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.CallerModuleId != Id) throw new ArgumentException("Commitments requires its own bound reader.", nameof(reader));
        return new ViewContributor(resolution, reader, view, priority, arguments);
    }
}
