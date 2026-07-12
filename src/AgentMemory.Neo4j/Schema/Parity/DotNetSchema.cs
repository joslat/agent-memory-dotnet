using System.Reflection;
using AgentMemory.Abstractions.Schema;

namespace AgentMemory.Neo4j.Schema.Parity;

/// <summary>
/// Projects the live .NET schema contract (<see cref="SchemaConstants"/>) into a comparable
/// <see cref="SchemaDescriptor"/> by reflecting its <c>const string</c> fields. This is the single source
/// of truth for "what the .NET port's schema actually is" — used by both the parity test and the CLI
/// self-verification, so they can never disagree.
/// </summary>
internal static class DotNetSchema
{
    /// <summary>Builds a descriptor of the current .NET schema.</summary>
    public static SchemaDescriptor Describe() => new(
        Version: "net",
        NodeLabels: ConstStrings(typeof(SchemaConstants.NodeLabels)),
        RelationshipTypes: ConstStrings(typeof(SchemaConstants.RelationshipTypes)),
        Properties: ConstStrings(typeof(SchemaConstants.Properties)));

    private static IReadOnlySet<string> ConstStrings(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
}
