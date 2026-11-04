// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>
/// The frame every module item reaches a model in (feature 04, R-HOST-053): one function for every path that renders module
/// items (sections, selection, the trial's measure), so none can leave an item unframed. A record a pack brought names its pack,
/// version, type and key; any other module item names its module, version, and its type and key when it has them.
/// </summary>
[Experimental("AMEXT001")]
public static class ContextFrames
{
    /// <summary>The frame's attributes, in order; never empty, so a module item is never rendered bare.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Of(ContextModuleBinding? binding, ContextItemProvenance? provenance)
    {
        // A capped record says how far it is trusted (feature 09), so the model weighs it.
        if (provenance is { SourcePack: { } pack })
            return provenance.ConfidenceMicro is { } capped
                ? [new("pack", pack.Id), new("version", pack.Version), new("type", provenance.RecordType), new("key", provenance.RecordKey), new("confidence", Packs.ModuleConfidence.Shown(capped))]
                : [new("pack", pack.Id), new("version", pack.Version), new("type", provenance.RecordType), new("key", provenance.RecordKey)];
        var frame = new List<KeyValuePair<string, string>> { new("module", provenance?.RecordModuleId ?? binding?.ModuleId ?? "unknown") };
        if (binding is not null && (provenance is null || provenance.RecordModuleId == binding.ModuleId)) frame.Add(new("version", binding.ModuleVersion));
        if ((provenance?.RecordType ?? binding?.SectionType) is { } type) frame.Add(new("type", type));
        if (provenance?.RecordKey is { } key) frame.Add(new("key", key));
        return frame;
    }
}
