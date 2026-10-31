namespace AgentMemory.Packs.Format;

/// <summary>
/// Which profiles a pack uses, derived from what it holds by the registry's profile map (feature 01, step 2): one rule, which
/// both verifiers apply, so the profiles a pack needs are never declared beside what it holds (no twin to keep equal).
/// </summary>
internal static class PackProfiles
{
    /// <summary>The profiles <paramref name="manifest"/> and its <paramref name="files"/> use, in the registry's order.</summary>
    internal static IReadOnlyList<PackRegistry.Profile> Used(IEnumerable<string> files, PackManifest manifest)
    {
        var paths = files.ToList();
        return [.. PackRegistry.Profiles.Where(p =>
            p.Files.Any(f => paths.Any(path => f.EndsWith('/') ? path.StartsWith(f, StringComparison.Ordinal) : path == f))
            || p.Fields.Any(manifest.Present.Contains)
            || p.Kinds.Contains(manifest.Kind)
            || p.Extensions.Any(manifest.ExtensionNames.Contains)
            || p.Values.Any(v => manifest.Strings.TryGetValue(v.Key, out var value) && v.Value.Contains(value)))];
    }

    /// <summary>Why a host that does not implement <paramref name="profile"/> refuses the pack, in the registry's words.</summary>
    internal static string Refusal(PackRegistry.Profile profile, PackManifest manifest) =>
        (profile.Refusal ?? "{id} uses the " + profile.Name + " profile, which this host does not implement.")
            .Replace("{id}", manifest.Id, StringComparison.Ordinal).Replace("{protection}", manifest.Protection, StringComparison.Ordinal)
            .Replace("{code}", manifest.Code, StringComparison.Ordinal);

}
