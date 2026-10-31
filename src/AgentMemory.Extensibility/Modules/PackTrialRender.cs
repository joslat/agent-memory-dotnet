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
    /// <summary>The bytes of a pack's item as the provider frames it: its pack, version, type and key, then its text.</summary>
    public static long Bytes(ContextItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var attributes = item.Provenance is { SourcePack: { } pack } provenance
            ? new KeyValuePair<string, string>[] { new("pack", pack.Id), new("version", pack.Version), new("type", provenance.RecordType), new("key", provenance.RecordKey) }
            : [];
        return Encoding.UTF8.GetByteCount(RecordFrame.Frame(attributes, item.Text));
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
    /// What a pack adds to every turn whatever the task, in bytes: nothing yet. Feature 06's always-shown summary fills it, and
    /// a saving is counted after it.
    /// </summary>
    public static long FixedBytes(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return 0;
    }
}
