namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Builds parameterized Cypher WHERE clause fragments from a metadata filter specification.
/// </summary>
/// <remarks>
/// Supported operators: <c>$eq</c>, <c>$ne</c>, <c>$contains</c>, <c>$in</c>, <c>$exists</c>.
/// Values are always bound as parameters to prevent Cypher injection.
/// </remarks>
public static class MetadataFilterBuilder
{
    /// <summary>
    /// Builds a WHERE clause fragment and a parameter dictionary from <paramref name="filters"/>.
    /// </summary>
    /// <param name="filters">
    /// Filter spec where each key is a node property name and each value is a
    /// <c>Dictionary&lt;string,object&gt;</c> containing exactly one operator entry.
    /// Example: <c>{ "metadata.source": { "$eq": "slack" } }</c>
    /// </param>
    /// <param name="nodeAlias">Cypher alias used in the generated predicates (default: <c>m</c>).</param>
    /// <returns>
    /// A tuple of the WHERE clause fragment (may be empty) and the parameters dictionary.
    /// The caller is responsible for merging the returned parameters into their own parameter map.
    /// </returns>
    public static (string WhereClause, Dictionary<string, object> Parameters) Build(
        Dictionary<string, object>? filters,
        string nodeAlias = "m")
    {
        if (filters is null || filters.Count == 0)
            return (string.Empty, new Dictionary<string, object>());

        var clauses = new List<string>();
        var parameters = new Dictionary<string, object>();
        var index = 0;

        foreach (var (key, operatorSpec) in filters)
        {
            if (operatorSpec is not Dictionary<string, object> ops)
                continue;

            foreach (var (op, value) in ops)
            {
                var paramName = $"filter_{index++}";
                var propRef = $"{nodeAlias}.`{key}`";

                var clause = op switch
                {
                    "$eq"       => BuildEq(propRef, paramName, value, parameters),
                    "$ne"       => BuildNe(propRef, paramName, value, parameters),
                    "$contains" => BuildContains(propRef, paramName, value, parameters),
                    "$in"       => BuildIn(propRef, paramName, value, parameters),
                    "$exists"   => BuildExists(propRef, value),
                    _ => throw new NotSupportedException($"Unsupported metadata filter operator: {op}")
                };

                clauses.Add(clause);
            }
        }

        return (string.Join(Environment.NewLine, clauses), parameters);
    }

    private static string BuildEq(string propRef, string paramName, object value, Dictionary<string, object> parameters)
    {
        parameters[paramName] = value;
        return $"AND {propRef} = ${paramName}";
    }

    private static string BuildNe(string propRef, string paramName, object value, Dictionary<string, object> parameters)
    {
        parameters[paramName] = value;
        return $"AND {propRef} <> ${paramName}";
    }

    private static string BuildContains(string propRef, string paramName, object value, Dictionary<string, object> parameters)
    {
        parameters[paramName] = value;
        return $"AND {propRef} CONTAINS ${paramName}";
    }

    private static string BuildIn(string propRef, string paramName, object value, Dictionary<string, object> parameters)
    {
        parameters[paramName] = value;
        return $"AND {propRef} IN ${paramName}";
    }

    private static string BuildExists(string propRef, object value)
    {
        // $exists: true  → property IS NOT NULL
        // $exists: false → property IS NULL
        var exists = value is bool b ? b : Convert.ToBoolean(value);
        return exists
            ? $"AND {propRef} IS NOT NULL"
            : $"AND {propRef} IS NULL";
    }
}
