using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleSchemaObjectTests
{
    private static ModuleManifest Module(string id, string prefix, params ModuleType[] types) => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example." + id,
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new ModuleOntology { LabelPrefix = prefix, Types = types }
    };
    private static ModuleType Type(string name, string property) => new()
    {
        Name = name, Properties = new Dictionary<string,ModuleProperty>
        { [property] = new() { Type = "string", Indexed = true } }
    };

    [Fact]
    public void DistinctLabelsCanStillCollideInGeneratedIndexNames()
    {
        Module("reviews","Review",Type("ReviewItem","due_at"),Type("ReviewItemDue","at")).Validate()
            .Should().Contain(e => e.Contains("schema object",StringComparison.Ordinal));
    }

    [Fact]
    public void CaseNormalizedCustomPropertyCannotCollideWithAnSdkIndex()
    {
        Module("reviews","Review",Type("ReviewItem","OwnerId")).Validate()
            .Should().Contain(e => e.Contains("schema object",StringComparison.Ordinal));
    }

    [Fact]
    public void ANewLabelCannotClaimAnExistingCoreIndexName()
    {
        Module("traces","Trace",Type("Trace","kind")).Validate()
            .Should().Contain(e => e.Contains("reserved schema object",StringComparison.Ordinal));
    }

    [Fact]
    public void DistinctModulePrefixesCannotHideAGeneratedIndexCollision()
    {
        var first = Module("reviews","Review",Type("ReviewItem","due_at"));
        var second = Module("due","ReviewItemDue",Type("ReviewItemDue","at"));
        var result = ModuleResolver.Resolve([first,second], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        result.Errors.Should().Contain(e => e.Contains("schema object",StringComparison.Ordinal));
        result.Modules.Should().BeEmpty();
    }

    [Fact]
    public void DescribesDeterministicOwnedShapesWithoutRetainingMutableProperties()
    {
        var props = new Dictionary<string,ModuleProperty> { ["dueDate"] = new() { Type = "datetime", Indexed = true }, ["text"] = new() { Type = "string" } };
        var manifest = Module("reviews","Review", new ModuleType { Name = "ReviewItem", Properties = props });
        var shapes = ModuleSchemaObjects.Describe(manifest);
        props.Clear();
        shapes.Select(x => x.Name).Should().Equal("review_item_due_date_idx","review_item_id","review_item_key_id_idx","review_item_owner_id_idx");
        shapes[0].Property.Should().Be("dueDate");
        shapes[1].Kind.Should().Be(ModuleSchemaObjectKind.UniqueConstraint);
        shapes.Count(x => x.Kind == ModuleSchemaObjectKind.RangeIndex).Should().Be(3);
        ((ICollection<ModuleSchemaObject>)shapes).IsReadOnly.Should().BeTrue();
        FluentActions.Invoking(() => ModuleSchemaObjects.Describe(Module("bad","Trace",Type("Trace","kind"))))
            .Should().Throw<FormatException>();
    }

    [Fact]
    public void ReservationsCoverActualBuildersShippedMigrationsAndUpstreamObjects()
    {
        var neo = Assembly.Load("AgentMemory.Neo4j");
        var queries = neo.GetType("AgentMemory.Neo4j.Queries.SchemaQueries")!;
        var statements = queries.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        foreach (var method in new[] { "BootstrapStatements", "BuildOwnerFilteredVectorIndexes" })
            statements.AddRange((IEnumerable<string>)queries.GetMethod(method)!.Invoke(null,[1536])!);
        var scripts = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"Schema","Migrations"),"*.cypher",SearchOption.AllDirectories);
        scripts.Should().Contain(p => p.Contains("ext",StringComparison.Ordinal));
        statements.AddRange(scripts.Select(File.ReadAllText));
        var names = statements.SelectMany(s => Regex.Matches(s,
            @"CREATE\s+(?:(?:RANGE|FULLTEXT|VECTOR|POINT|TEXT|LOOKUP)\s+)?(?:INDEX|CONSTRAINT)\s+([A-Za-z_][A-Za-z0-9_]*)\s+IF\s+NOT\s+EXISTS",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        names.Should().Contain("entity_embedding_owner_idx");names.Should().Contain("fact_derivation_key_idx");
        using var stream = neo.GetManifestResourceStream("AgentMemory.Neo4j.Schema.Parity.Snapshots.0.5.0.json")!;
        using var upstream = JsonDocument.Parse(stream);
        foreach (var constraint in upstream.RootElement.GetProperty("constraints").EnumerateArray()) names.Add(constraint.GetProperty("name").GetString()!);
        foreach (var kind in upstream.RootElement.GetProperty("indexes").EnumerateObject())
            foreach (var index in kind.Value.EnumerateArray()) names.Add(index.GetProperty("name").GetString()!);
        ModuleReservedNames.SchemaObjects.Should().Contain(names);
        ModuleReservedNames.SchemaObjects.Should().Contain("module_migration_key");
    }

}
