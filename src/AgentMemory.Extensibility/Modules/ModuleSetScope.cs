using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Ingestion;

namespace AgentMemory.Extensibility.Modules;

/// <summary>One scope's module contributors and providers, created once on first use.</summary>
[Experimental("AMEXT001")]
internal sealed class ModuleSetScope(ModuleSet set, IServiceProvider services)
{
    private IReadOnlyList<IContextContributor>? _contributors;
    private IReadOnlyList<IIngestionProvider>? _providers;

    public IContextContributor Contributor(int index) => (_contributors ??= set.CreateContributors(services))[index];

    public IReadOnlyList<IIngestionProvider> Providers => _providers ??= set.CreateProviders(services);
}
