using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

[Experimental("AMEXT001")]
internal static class ModuleManifestSnapshot
{
    internal static ModuleManifest Freeze(ModuleManifest m) => m with
    {
        Modules = Map(m.Modules, value => value), Host = List(m.Host, value => value),
        Ontology = m.Ontology is not { } ontology ? null : ontology with
        {
            Types = List(ontology.Types, type => type with { Properties = Map(type.Properties, property => property with
            { Values = List(property.Values, value => value), Minimum = property.Minimum?.Clone(), Maximum = property.Maximum?.Clone() }) }),
            Relations = List(ontology.Relations, relation => relation with { })
        },
        Views = List(m.Views, view => view with
        {
            Where = List(view.Where, filter => filter with { Value = filter.Value.Clone() }),
            OrderBy = view.OrderBy is null ? null : view.OrderBy with { }
        })
    };

    // Preserve malformed nulls for the local validator, rather than silently repairing input.
    private static IReadOnlyList<T> List<T>(IReadOnlyList<T>? values, Func<T, T> freeze) where T : class =>
        values is null ? null! : values.Select(v => v is null ? null! : freeze(v)).ToList().AsReadOnly();
    private static IReadOnlyDictionary<string, T> Map<T>(IReadOnlyDictionary<string, T>? values, Func<T, T> freeze) where T : class =>
        values is null ? null! : values.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value is null ? null! : freeze(p.Value), StringComparer.Ordinal).AsReadOnly();
}
