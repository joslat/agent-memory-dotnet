using System.Collections.Frozen;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Context;

public sealed partial class ContextCompiler
{
    private static IEnumerable<IContextContributor> Bind(ModuleResolution resolution, IEnumerable<IContextContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(contributors);
        if (!resolution.Succeeded) throw new ArgumentException("Successful metadata resolution is required.", nameof(resolution));
        var bound = new List<IContextContributor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contributor in contributors)
        {
            if (contributor?.Descriptor is not { } descriptor || !ids.Add(descriptor.Id))
                throw new ArgumentException("Contributors require distinct descriptors.", nameof(contributors));
            if (contributor is CoreMemoryContextContributor)
            {
                bound.Add(contributor);
                continue;
            }
            var module = resolution.Modules.SingleOrDefault(m => ModuleIdentity.IsOwnedId(m.Id, descriptor.Id));
            if (module is null) throw new ArgumentException("Contributor identity must belong to a resolved module.", nameof(contributors));
            var frozen = descriptor with { SectionTypes = descriptor.SectionTypes.ToFrozenSet(StringComparer.Ordinal) };
            bound.Add(new BoundContributor(contributor, frozen, module.Id, module.Version.ToString()));
        }
        return bound;
    }

    private ContextSection BindSection(IContextContributor contributor, ContextSection section)
    {
        if (contributor is not BoundContributor bound)
            return section.Binding is null ? section : section with { Binding = null };
        if (!string.Equals(section.ContributorId, bound.Descriptor.Id, StringComparison.Ordinal)
            || !bound.Descriptor.SectionTypes.Contains(section.TypeId))
            throw new InvalidOperationException("A bound contribution must retain its contributor and declared section type.");
        return section with
        {
            Binding = new()
            {
                ModuleId = bound.ModuleId, ModuleVersion = bound.ModuleVersion,
                ContributorId = bound.Descriptor.Id, SectionType = section.TypeId,
                MetadataFingerprint = Resolution!.ManifestFingerprint!
            }
        };
    }

    // Only this host binding creates these wrappers. Implementation getters cannot relabel a running span.
    private sealed class BoundContributor(IContextContributor inner, ContextContributorDescriptor descriptor,
        string moduleId, string moduleVersion) : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } = descriptor;
        public string ModuleId { get; } = moduleId;
        public string ModuleVersion { get; } = moduleVersion;
        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) =>
            inner.AppliesAsync(request, cancellationToken);
        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) =>
            inner.ContributeAsync(request, cancellationToken);
        public Task<ContextContributionResult> ContributeResultAsync(ContextRequest request, CancellationToken cancellationToken = default) =>
            inner.ContributeResultAsync(request, cancellationToken);
    }
}
