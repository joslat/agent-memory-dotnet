using System.Reflection;

namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Discovers all centralized Cypher query constants via reflection for EXPLAIN validation.
/// </summary>
public static class CypherQueryRegistry
{
    /// <summary>
    /// Returns all (name, cypherText) pairs from all *Queries classes in this assembly.
    /// </summary>
    public static IReadOnlyList<(string Name, string Cypher)> GetAll()
    {
        var queryTypes = typeof(CypherQueryRegistry).Assembly
            .GetTypes()
            .Where(t => t.IsPublic && t.IsAbstract && t.IsSealed // static classes
                        && t.Name.EndsWith("Queries")
                        && t.Namespace == "Neo4j.AgentMemory.Neo4j.Queries");

        var results = new List<(string, string)>();
        foreach (var type in queryTypes)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral && field.FieldType == typeof(string))
                {
                    var value = (string?)field.GetValue(null);
                    if (!string.IsNullOrWhiteSpace(value))
                        results.Add(($"{type.Name}.{field.Name}", value));
                }
            }
        }
        return results;
    }
}
