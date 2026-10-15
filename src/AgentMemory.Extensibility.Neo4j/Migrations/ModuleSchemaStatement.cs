namespace AgentMemory.Extensibility.Neo4j.Migrations;

internal sealed record ModuleMigrationSchemaObject
{
    internal required string Name { get; init; }
    internal required string Kind { get; init; }
    internal required string EntityType { get; init; }
    internal IReadOnlyList<string> Labels { get; init; } = [];
    internal IReadOnlyList<string> Properties { get; init; } = [];
    internal required bool IsConstraint { get; init; }
}

// Parses an exact range-index or uniqueness-constraint shape. This is not authorization to execute:
// the composition compiler must still validate module ownership and reserve the shape before DDL.
internal static class ModuleSchemaStatement
{
    internal static ModuleMigrationSchemaObject Parse(string source)
    {
        var statements = ModuleCypherScript.Parse(source);
        if (statements.Count != 1) throw Invalid("expected exactly one schema statement");
        var reader = new ModuleCypherReader(statements[0].Tokens);
        reader.Word("CREATE");
        var range = reader.TakeWord("RANGE");
        var constraint = !range && reader.TakeWord("CONSTRAINT");
        if (!constraint) reader.Word("INDEX");
        var name = reader.Identifier();
        reader.Word("IF");
        reader.Word("NOT");
        reader.Word("EXISTS");
        reader.Word("FOR");
        reader.Symbol("(");
        var relationship = reader.TakeSymbol(")");
        if (relationship)
        {
            reader.Symbol("-");
            reader.Symbol("[");
        }
        var variable = reader.Identifier();
        reader.Symbol(":");
        var label = reader.Identifier();
        if (relationship)
        {
            reader.Symbol("]");
            reader.Symbol("-");
            reader.Symbol("(");
        }
        reader.Symbol(")");
        reader.Word(constraint ? "REQUIRE" : "ON");
        var parenthesized = reader.TakeSymbol("(");
        if (!constraint && !parenthesized) throw Invalid("index properties require parentheses");
        var properties = new List<string>();
        do
        {
            if (!string.Equals(reader.Identifier(), variable, StringComparison.Ordinal))
                throw Invalid("property reference does not name the schema pattern variable");
            reader.Symbol(".");
            properties.Add(reader.Identifier());
        } while (parenthesized && reader.TakeSymbol(","));
        if (parenthesized) reader.Symbol(")");
        if (constraint)
        {
            reader.Word("IS");
            reader.Word("UNIQUE");
        }
        if (!reader.AtEnd) throw Invalid("unsupported trailing schema syntax");
        if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Count)
            throw Invalid("duplicate schema property");
        return new ModuleMigrationSchemaObject
        {
            Name = name,
            Kind = constraint ? relationship ? "RELATIONSHIP_UNIQUENESS" : "UNIQUENESS" : "RANGE",
            EntityType = relationship ? "RELATIONSHIP" : "NODE",
            Labels = Array.AsReadOnly(new[] { label }),
            Properties = properties.AsReadOnly(),
            IsConstraint = constraint
        };
    }

    private static FormatException Invalid(string message) => new("Module schema statement: " + message + ".");
}
