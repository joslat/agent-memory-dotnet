using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Pure helpers for the <c>schema-check</c> CLI command: the set of constraint/index <em>names</em> that
/// <see cref="SchemaBootstrapper"/> creates, and the diff against what a live database actually has.
/// This is a <em>runtime conformance</em> check (does the deployed database match the bootstrap baseline?),
/// distinct from <c>schema-parity</c> (static check that the .NET schema is compatible with the upstream
/// Python snapshot).
/// </summary>
internal static class SchemaConformance
{
    /// <summary>
    /// L10. Of the FAILED indexes Neo4j reports, the ones AgentMemory is responsible for.
    /// </summary>
    /// <remarks>
    /// A failed index does not stop queries — they fall back to full scans — so it surfaces as
    /// unexplained slowness rather than an error, which is why bootstrap fails closed on it.
    /// <para>
    /// But failing on <b>any</b> failed index in the database turns an unrelated application's broken
    /// index into a startup crash in this library, on exactly the shared-instance deployment the
    /// multi-tenant work exists to support — and points the operator at the wrong owner. Scoping to
    /// the names this library creates keeps every failure we are responsible for and declines the
    /// rest. It reuses <see cref="ExpectedObjectNames"/>, so this and <c>schema-check</c> agree by
    /// construction rather than by two lists kept in step by hand.
    /// </para>
    /// <param name="failedDescriptors">Reported as <c>name (TYPE)</c>, the shape bootstrap collects.</param>
    /// </remarks>
    public static IReadOnlyList<string> SelectOwnedFailures(
        IReadOnlyList<string> failedDescriptors,
        int dimensions)
    {
        ArgumentNullException.ThrowIfNull(failedDescriptors);
        if (failedDescriptors.Count == 0)
            return Array.Empty<string>();

        var owned = ExpectedObjectNames(dimensions).ToHashSet(StringComparer.Ordinal);
        return failedDescriptors
            .Where(descriptor => owned.Contains(NameOf(descriptor)))
            .ToArray();
    }

    /// <summary>The bare index name from a <c>name (TYPE)</c> descriptor.</summary>
    private static string NameOf(string descriptor)
    {
        var space = descriptor.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? descriptor : descriptor[..space];
    }

    /// <summary>
    /// The names of every constraint and index the bootstrap creates for the given embedding
    /// <paramref name="dimensions"/> (constraints + fulltext + vector + property/point indexes).
    /// </summary>
    public static IReadOnlyList<string> ExpectedObjectNames(int dimensions)
    {
        var names = new List<string>(
            SchemaQueries.Constraints.Length + SchemaQueries.FulltextIndexes.Length +
            SchemaQueries.PropertyIndexes.Length + 6);

        foreach (var ddl in SchemaQueries.Constraints) names.Add(ParseObjectName(ddl));
        foreach (var ddl in SchemaQueries.FulltextIndexes) names.Add(ParseObjectName(ddl));
        foreach (var ddl in SchemaQueries.BuildVectorIndexes(dimensions)) names.Add(ParseObjectName(ddl));
        foreach (var ddl in SchemaQueries.PropertyIndexes) names.Add(ParseObjectName(ddl));
        return names;
    }

    /// <summary>
    /// Extracts the object name from a <c>CREATE [FULLTEXT|VECTOR|POINT] [CONSTRAINT|INDEX] &lt;name&gt;
    /// IF NOT EXISTS ...</c> statement — the token immediately following the <c>CONSTRAINT</c>/<c>INDEX</c>
    /// keyword.
    /// </summary>
    public static string ParseObjectName(string createDdl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createDdl);
        var tokens = createDdl.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (tokens[i].Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
                tokens[i].Equals("INDEX", StringComparison.OrdinalIgnoreCase))
            {
                return tokens[i + 1];
            }
        }
        throw new FormatException($"Could not parse a constraint/index name from: {createDdl}");
    }

    /// <summary>
    /// The expected names that are absent from <paramref name="existing"/> (case-sensitive — Neo4j
    /// constraint/index names are case-sensitive), preserving bootstrap order.
    /// </summary>
    public static IReadOnlyList<string> MissingObjects(
        IEnumerable<string> expected, IReadOnlyCollection<string> existing)
    {
        var have = new HashSet<string>(existing, StringComparer.Ordinal);
        return expected.Where(n => !have.Contains(n)).ToList();
    }
}
