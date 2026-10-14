using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Shared;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

internal sealed record ModuleMigrationOperation
{
    internal required string Text { get; init; }
    internal ModuleMigrationSchemaObject? Shape { get; init; }
    internal bool IsSchema => Shape is not null;
    internal IReadOnlyList<string> Labels { get; init; } = [];
    internal IReadOnlyList<string> RelationshipTypes { get; init; } = [];
    internal IReadOnlyList<string> WrittenProperties { get; init; } = [];
}

// Compilation is data-only. Durable ownership and per-statement recovery still belong to the migrator.
[Experimental("AMEXT001")]
internal static class ModuleMigrationOperations
{
    internal static ModuleMigrationOperation Compile(ModuleManifest manifest, string source)
    {
        var statements = ModuleCypherScript.Parse(source);
        if (statements.Count != 1) throw new FormatException("A module operation requires exactly one statement.");
        var statement = statements[0];
        var tokens = statement.Tokens;
        ModuleMigrationOperation operation;
        if (tokens[0].Kind == ModuleCypherTokenKind.Word && tokens[0].Text.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            var shape = ModuleSchemaStatement.Parse(source);
            operation = new()
            {
                Text = statement.Text, Shape = shape,
                Labels = shape.EntityType == "NODE" ? shape.Labels : [],
                RelationshipTypes = shape.EntityType == "RELATIONSHIP" ? shape.Labels : []
            };
        }
        else operation = ModuleBackfillStatement.Parse(statement);

        // Share the existing additive lint without interpreting literal/quoted text as commands.
        // RANGE is an explicit spelling of the already parsed default range index.
        var lintText = string.Join(" ", tokens.Where((t, i) => !(operation.IsSchema && i == 1
                && t.Kind == ModuleCypherTokenKind.Word && t.Text.Equals("RANGE", StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Kind is ModuleCypherTokenKind.QuotedIdentifier or ModuleCypherTokenKind.Literal ? "quoted_value" : t.Text));
        var errors = AdditiveMigrationLint.Validate([lintText], manifest.Id)
            .Concat(ModuleNamespaceReferences.Validate(manifest, operation.Labels, operation.RelationshipTypes,
                operation.Shape is { } schema ? [schema.Name] : [], operation.WrittenProperties)).ToArray();
        if (errors.Length != 0) throw new FormatException(string.Join(Environment.NewLine, errors));
        return operation;
    }
}
