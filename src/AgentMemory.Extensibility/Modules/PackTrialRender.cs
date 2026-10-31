using System.Diagnostics.CodeAnalysis;
using System.Text;
using AgentMemory.Extensibility.Context;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// The size of what an arm puts into a model's context (feature 05): each offered item in the frame the provider renders it in
/// (feature 04, <c>RecordFrame</c>, one source with the provider's), counted in UTF-8 bytes. One renderer for both arms, so a
/// saving is a saving in what the model reads.
/// </summary>
[Experimental("AMEXT001")]
public static class PackTrialRender
{
    /// <summary>The bytes of a module's item as the provider frames it (<see cref="ContextFrames.Of"/>), then its text.</summary>
    public static long Bytes(ContextSection section, ContextItem item)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(item);
        return Encoding.UTF8.GetByteCount(RecordFrame.Frame(ContextFrames.Of(section.Binding, item.Provenance), item.Text));
    }

    /// <summary>The bytes of what a host's own retrieval offers, framed the same way: its source, the item's key, its text.</summary>
    public static long Bytes(string source, string key, string text)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(text);
        return Encoding.UTF8.GetByteCount(RecordFrame.Frame([new("source", source), new("key", key)], text));
    }

    /// <summary>
    /// What a pack adds to every turn whatever the task, in bytes (feature 06): its <c>whenToUse</c> in its frame, counted whether
    /// or not this host shows it, so a chatty summary costs the pack in every trial; 0 without one.
    /// </summary>
    public static long FixedBytes(ModuleManifest manifest) => AlwaysShown(manifest) is { } shown ? Encoding.UTF8.GetByteCount(shown) : 0;

    /// <summary>
    /// The pack's always-shown block as the model reads it (feature 06): its <c>whenToUse</c> in a frame naming the module, its
    /// version and the type <c>whenToUse</c>, escaped; null without one. The one definition a host renders and a trial counts.
    /// </summary>
    public static string? AlwaysShown(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.WhenToUse is { } text
            ? RecordFrame.Frame([new("module", manifest.Id), new("version", manifest.Version.ToString()), new("type", "whenToUse")], text)
            : null;
    }
}
