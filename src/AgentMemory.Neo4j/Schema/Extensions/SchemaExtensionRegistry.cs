using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// Every registered <see cref="ISchemaExtension"/>, and which of them a given
/// <see cref="Neo4jOptions"/> actually activates.
/// </summary>
/// <remarks>
/// <para>
/// Registration is unconditional; activation is by id. That split is the reranker lesson: gating the
/// DI registration on a flag means a host that flips the flag later still gets nothing, and the
/// failure is silent.
/// </para>
/// <para>
/// <b>Order is deterministic, always.</b> Active extensions come back topologically sorted by
/// <see cref="ISchemaExtension.DependsOn"/> with ties broken by ordinal id — because this order decides
/// the sequence migration scripts run in, and a migration order that varied per process would be a
/// schema that varied per process.
/// </para>
/// </remarks>
internal sealed class SchemaExtensionRegistry
{
    private readonly IReadOnlyDictionary<string, ISchemaExtension> _byId;

    public SchemaExtensionRegistry(IEnumerable<ISchemaExtension> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        All = [.. extensions.OrderBy(e => e.Id, StringComparer.Ordinal)];

        var duplicates = All
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new SchemaInitializationException(
                $"Duplicate schema-extension id(s): {string.Join(", ", duplicates)}. An id is the key "
                + "of its (:Migration) rows, so two extensions sharing one would overwrite each other's "
                + "migration history.",
                "schema-extension-registration");
        }

        var identityProblems = All.SelectMany(SchemaExtensionValidator.ValidateIdentity).ToList();
        if (identityProblems.Count > 0)
        {
            throw new SchemaInitializationException(
                "Invalid schema-extension declaration(s):" + Environment.NewLine
                + string.Join(Environment.NewLine, identityProblems.Select(p => "  - " + p)),
                "schema-extension-registration");
        }

        _byId = All.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// The extensions this package ships, as instances. <b>The single list</b> — DI registration and
    /// every host-less caller (the <c>schema-parity</c> CLI verb, which builds no container) read it,
    /// so the two can never disagree about what exists.
    /// </summary>
    /// <remarks>
    /// A second hand-maintained list is how an extension ends up registered in one place and invisible
    /// in another. <c>SchemaExtensionReachabilityTests</c> still reflects over the assembly and compares
    /// against what DI produces, so an implementation missing from <i>this</i> list is caught too.
    /// </remarks>
    public static IReadOnlyList<ISchemaExtension> CreateShipped() =>
    [
        new ProceduralSchemaExtension(), new WorkingMemorySchemaExtension(),
        new DeltaRecallSchemaExtension(), new ArithmeticSchemaExtension(),
    ];

    /// <summary>A registry over <see cref="CreateShipped"/>, for callers with no container.</summary>
    public static SchemaExtensionRegistry CreateDefault() => new(CreateShipped());

    /// <summary>Every registered extension, ordinal-sorted by id. Registration is not activation.</summary>
    public IReadOnlyList<ISchemaExtension> All { get; }

    /// <summary>The ids of every registered extension, for error messages and the owners report.</summary>
    public IReadOnlyList<string> KnownIds => [.. All.Select(e => e.Id)];

    /// <summary>Looks up a registered extension by id.</summary>
    public ISchemaExtension? Find(string id) =>
        _byId.TryGetValue(id, out var extension) ? extension : null;

    /// <summary>
    /// The extensions <paramref name="options"/> activates, dependency closure included, in the order
    /// their migrations must run.
    /// </summary>
    /// <remarks>
    /// An unknown id throws rather than being ignored. Ignoring it is how a run that asked for
    /// arithmetic quietly measures a database without it — the whole class of defect this codebase
    /// keeps finding, and the reason <c>SchemaParityPolicy.ForVersion</c> throws in the same style.
    /// </remarks>
    public IReadOnlyList<ISchemaExtension> Active(Neo4jOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Active(options.Extensions);
    }

    /// <summary>The id-set overload, so callers without a full options object can resolve activation.</summary>
    public IReadOnlyList<ISchemaExtension> Active(IEnumerable<string> requestedIds)
    {
        ArgumentNullException.ThrowIfNull(requestedIds);

        var requested = requestedIds.ToList();
        var unknown = requested.Where(id => !_byId.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new SchemaInitializationException(
                $"Unknown schema extension(s): {string.Join(", ", unknown)}. Known: "
                + $"{string.Join(", ", KnownIds)}.",
                "schema-extension-activation");
        }

        // Empty in, empty out -- and that is the byte-identical default. No sorting, no closure, no
        // logging: with no extensions requested this method must be indistinguishable from not existing.
        if (requested.Count == 0) return [];

        var closure = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in requested)
            AddClosure(id, closure, []);

        return TopologicalOrder(closure);
    }

    private void AddClosure(string id, HashSet<string> closure, List<string> path)
    {
        if (!closure.Add(id)) return;

        var extension = _byId[id];
        foreach (var dependency in extension.DependsOn.OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!_byId.ContainsKey(dependency))
            {
                throw new SchemaInitializationException(
                    $"Schema extension '{id}' depends on '{dependency}', which is not registered. "
                    + $"Known: {string.Join(", ", KnownIds)}.",
                    "schema-extension-activation");
            }

            // Implicit activation, by design: asking for an extension gets you what it needs to work.
            // The alternative -- refusing until the dependency is named explicitly -- turns a solvable
            // configuration into a startup failure for no safety gained, since the dependency is
            // additive schema either way.
            AddClosure(dependency, closure, [.. path, id]);
        }
    }

    /// <summary>
    /// Kahn's algorithm over the dependency edges, ties broken by ordinal id so the result is a total
    /// order rather than merely a valid one.
    /// </summary>
    private IReadOnlyList<ISchemaExtension> TopologicalOrder(HashSet<string> ids)
    {
        var remaining = new HashSet<string>(ids, StringComparer.Ordinal);
        var ordered = new List<ISchemaExtension>(ids.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(id => _byId[id].DependsOn.All(dependency => !remaining.Contains(dependency)))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (ready.Count == 0)
            {
                throw new SchemaInitializationException(
                    "Schema-extension dependency cycle among: "
                    + string.Join(", ", remaining.OrderBy(id => id, StringComparer.Ordinal))
                    + ". Migration order cannot be decided, so nothing is applied.",
                    "schema-extension-activation");
            }

            foreach (var id in ready)
            {
                ordered.Add(_byId[id]);
                remaining.Remove(id);
            }
        }

        return ordered;
    }
}
