// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Neo4j.Queries;

[Experimental("AMEXT001")]
internal static class ModuleSchemaQueries
{
    private const string UniqueConstraint = "CREATE CONSTRAINT `{0}` IF NOT EXISTS FOR (n:`{1}`) REQUIRE n.`{2}` IS UNIQUE;";
    private const string RangeIndex = "CREATE INDEX `{0}` IF NOT EXISTS FOR (n:`{1}`) ON (n.`{2}`);";

    // Callers use descriptors from validated, frozen metadata, never arbitrary schema identifiers.
    internal static string Create(ModuleSchemaObject shape) => shape.Kind == ModuleSchemaObjectKind.FullTextIndex
        ? "CREATE FULLTEXT INDEX `" + shape.Name + "` IF NOT EXISTS FOR (n:`" + shape.Label + "`) ON EACH ["
            + string.Join(", ", shape.Properties.Select(p => "n.`" + p + "`")) + "];"
        : string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        shape.Kind switch
        {
            ModuleSchemaObjectKind.UniqueConstraint => UniqueConstraint,
            ModuleSchemaObjectKind.RangeIndex => RangeIndex,
            _ => throw new ArgumentOutOfRangeException(nameof(shape), "Unsupported module schema kind.")
        }, shape.Name, shape.Label, shape.Property);
}
