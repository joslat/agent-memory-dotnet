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
