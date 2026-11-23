#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Intent;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// A coding pack resolved with what it depends on (CO-2b): the code map and decisions link into the intent pack's requirements,
/// so neither resolves on its own any more.
/// </summary>
internal static class CodingResolutions
{
    internal static ModuleResolution CodeMap => Of(CodeMapPack.Manifest);

    internal static ModuleResolution Decisions => Of(DecisionsPack.Manifest);

    private static ModuleResolution Of(ModuleManifest manifest)
    {
        var resolution = ModuleResolver.Resolve([IntentPack.Manifest, manifest], ModuleSet.SdkVersion, ModuleHostFeatures.Known);
        if (!resolution.Succeeded) throw new InvalidOperationException(string.Join("; ", resolution.Errors));
        return resolution;
    }
}
