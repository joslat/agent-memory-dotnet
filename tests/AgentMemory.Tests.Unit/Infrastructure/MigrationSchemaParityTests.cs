using System.Reflection;
using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// Every DDL statement shipped as a migration must also exist in <see cref="SchemaQueries"/>.
/// </summary>
/// <remarks>
/// The two surfaces answer different questions — <see cref="SchemaQueries"/> is what a <b>fresh</b>
/// database gets from <c>SchemaBootstrapper</c>, and <c>Schema/Migrations/*.cypher</c> is what brings
/// an <b>existing</b> one to parity, as every migration header in the folder says in so many words.
/// A new index needs both, and nothing enforced that.
/// <para>
/// It was found the expensive way: L11 added <c>fact_merge_key_idx</c> to <see cref="SchemaQueries"/>
/// alone, so a deployment whose upgrade path is <c>migrate</c> rather than <c>BootstrapAsync</c>
/// would never have received it. The whole point of a schema catalog is that this cannot be a matter
/// of remembering.
/// </para>
/// <para>
/// The comparison is deliberately <b>verbatim</b>. Two statements that differ only in property order
/// build different indexes, so "equivalent" is not good enough and normalising would hide exactly the
/// drift worth catching.
/// </para>
/// </remarks>
public sealed class MigrationSchemaParityTests
{
    [Fact]
    public void EveryMigrationStatementExistsInTheSchemaCatalog()
    {
        var catalog = SchemaCatalog();
        var statements = MigrationStatements();

        statements.Should().NotBeEmpty("the migration folder must ship with the assembly");

        foreach (var (file, statement) in statements)
        {
            catalog.Should().Contain(
                statement,
                "migration {0} ships DDL that SchemaQueries does not, so a FRESH database would " +
                "never get it: {1}",
                file,
                statement);
        }
    }

    /// <summary>The specific gap that motivated this test, pinned by name.</summary>
    [Fact]
    public void TheFactMergeKeyIndexShipsAsAMigrationToo()
    {
        MigrationStatements()
            .Select(entry => entry.Statement)
            .Should().Contain(SchemaQueries.FactMergeKeyIndex);
    }

    private static HashSet<string> SchemaCatalog()
    {
        var catalog = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in typeof(SchemaQueries)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            if (field.GetRawConstantValue() is string value)
                catalog.Add(value);
        }

        // Vector index DDL is built per embedding dimension rather than declared as a constant.
        foreach (var dimensions in new[] { 384, 1536 })
            foreach (var ddl in SchemaQueries.BuildVectorIndexes(dimensions))
                catalog.Add(ddl);

        return catalog;
    }

    private static IReadOnlyList<(string File, string Statement)> MigrationStatements()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Schema", "Migrations");
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "*.cypher")
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => File.ReadAllLines(path)
                .Select(line => line.Trim().TrimEnd(';'))
                .Where(line => line.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase))
                .Select(line => (File: Path.GetFileName(path), Statement: line)))
            .ToArray();
    }
}
