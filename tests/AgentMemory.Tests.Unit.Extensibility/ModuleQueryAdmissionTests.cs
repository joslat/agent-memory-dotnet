using System.Text.Json;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleQueryAdmissionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    [Theory]
    [InlineData("count", "=", "9007199254740993", true)]
    [InlineData("count", "=", "9223372036854775808", false)]
    [InlineData("count", "<", "null", false)]
    [InlineData("title", "=", "null", true)]
    [InlineData("title", "!=", "null", true)]
    [InlineData("count", "in", "[1,2]", true)]
    [InlineData("count", "in", "[1,null]", false)]
    [InlineData("active", "<", "true", false)]
    [InlineData("active", "=", "true", true)]
    [InlineData("owner_id", "=", "\"other\"", false)]
    [InlineData("title", "MATCH", "\"x\"", false)]
    [InlineData("title`", "=", "\"x\"", false)]
    public void AppliesTheSameClosedFilterLanguageAsManifestViews(string property, string op, string json, bool accepted)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Admit(Query() with { Where = [new() { Property = property, Operator = op, Value = doc.RootElement }] });
        (result.Status == ModuleReadStatus.Succeeded).Should().Be(accepted);
    }

    [Fact]
    public void BindsCallerAndAllowsOnlyOwnModuleOrDirectDeclaredDependency()
    {
        var resolution = Resolve();
        ModuleQueryAdmission.Admit(resolution, "com.example.reader", Query(), new(), Now).Status.Should().Be(ModuleReadStatus.Succeeded);
        ModuleQueryAdmission.Admit(resolution, "com.example.reader", Query() with { ModuleId = "com.example.other", Type = "OtherItem" }, new(), Now).Status.Should().Be(ModuleReadStatus.Refused);
        ModuleQueryAdmission.Admit(resolution, "com.example.other", Query(), new(), Now).Status.Should().Be(ModuleReadStatus.Refused);
        ModuleQueryAdmission.Admit(resolution, "missing", Query(), new(), Now).Status.Should().Be(ModuleReadStatus.Refused);
        Admit(Query() with { Type = "Entity" }).Status.Should().Be(ModuleReadStatus.Refused);
    }

    [Fact]
    public void PinsNowToSystemTimeAndResolvesSessionEvenInsideMembership()
    {
        var pin = Now.AddDays(-1);
        var filters = new List<ModuleViewFilter> {
            new() { Property = "at", Operator = "<=", Value = JsonSerializer.SerializeToElement("$now") },
            new() { Property = "title", Operator = "in", Value = JsonSerializer.SerializeToElement(new[] { "$session", "literal" }) }
        };
        var result = Admit(Query() with { Where = filters }, new() { SystemAsOfUtc = pin, SessionId = "session-a" });
        result.Status.Should().Be(ModuleReadStatus.Succeeded);
        filters.Clear();
        result.Query!.Where[0].Value.GetDateTimeOffset().Should().Be(pin);
        result.Query.Where[1].Value.EnumerateArray().Select(x => x.GetString()).Should().Equal("session-a", "literal");
        Admit(Query() with { Where = [new() { Property = "title", Operator = "=", Value = JsonSerializer.SerializeToElement("$session") }] }).Status.Should().Be(ModuleReadStatus.Refused);
    }

    [Fact]
    public void UnsupportedValidTimeNeverReturnsCurrentDataAndSnapshotsSurviveDisposal()
    {
        Admit(Query(), new() { ValidAsOfUtc = Now }).Status.Should().Be(ModuleReadStatus.NotApplicable);
        using var doc = JsonDocument.Parse("\"lesson\"");
        var admitted = Admit(Query() with { Where = [new() { Property = "title", Operator = "=", Value = doc.RootElement }] });
        doc.Dispose(); admitted.Query!.Where[0].Value.GetString().Should().Be("lesson");
        Admit(Query() with { Where = [new() { Property = "title", Operator = "=", Value = default }] }).Status.Should().Be(ModuleReadStatus.Refused);
    }

    [Fact]
    public void RejectsMalformedOrUnboundedQueriesBeforeStorage()
    {
        foreach (var query in new[] { Query() with { Limit = 0 }, Query() with { Limit = ModuleQuery.MaximumLimit + 1 },
            Query() with { Where = null! }, Query() with { Where = [null!] }, Query() with { Key = "" },
            Query() with { OrderBy = new() { Property = "owner_id" } } })
            Admit(query).Status.Should().Be(ModuleReadStatus.Refused);
    }

    [Fact]
    public void LinkFiltersAreDeclaredBoundedAndIndependentlyCaptured()
    {
        var filters = new List<ModuleLinkFilter> { new() { Relation = "DATA_ABOUT", TargetId = "entity-id" } };
        var admitted = Admit(Query() with { Links = filters });
        admitted.Status.Should().Be(ModuleReadStatus.Succeeded);
        filters.Clear();
        admitted.Query!.Links.Should().ContainSingle().Which.TargetId.Should().Be("entity-id");
    }

    [Fact]
    public void InvalidLinkFiltersAreRefusedBeforeStorage()
    {
        foreach (var filters in new IReadOnlyList<ModuleLinkFilter>[] { null!, [null!],
            [new() { Relation = "DATA_ABOUT", TargetId = " " }],
            [new() { Relation = "DATA_ABOUT", TargetId = new string('x', 1025) }],
            [new() { Relation = "DATA_OTHER", TargetId = "entity" }],
            [new() { Relation = "DATA_ABOUT`", TargetId = "entity" }],
            Enumerable.Range(0, 65).Select(i => new ModuleLinkFilter { Relation = "DATA_ABOUT", TargetId = "id" + i }).ToArray() })
            Admit(Query() with { Links = filters }).Status.Should().Be(ModuleReadStatus.Refused);
    }

    private static ModuleQueryAdmissionResult Admit(ModuleQuery query, ModuleReadContext? context = null) =>
        ModuleQueryAdmission.Admit(Resolve(), "com.example.data", query, context ?? new(), Now);
    private static ModuleQuery Query() => new() { ModuleId = "com.example.data", Type = "DataItem", Limit = 10 };
    internal static ModuleResolution Resolve()
    {
        var data = ModuleManifest.Parse("""
            {"format":"agentmemory.module/v1alpha1","id":"com.example.data","version":"0.1.0","sdk":"[0.1,0.2)",
             "ontology":{"labelPrefix":"Data","types":[{"name":"DataItem","properties":{
               "title":{"type":"string"},"count":{"type":"int"},"active":{"type":"bool"},"at":{"type":"datetime"}}}],"relations":[{"name":"DATA_ABOUT","from":"DataItem","to":"core:Entity"}]}}
            """);
        var reader = ModuleManifest.Parse("""
            {"format":"agentmemory.module/v1alpha1","id":"com.example.reader","version":"0.1.0","sdk":"[0.1,0.2)",
             "requires":{"modules":{"com.example.data":"[0.1,0.2)"}}}
            """);
        var other = data with { Id = "com.example.other", Ontology = new() { LabelPrefix = "Other", Types = [new() { Name = "OtherItem" }] } };
        return ModuleResolver.Resolve([data, reader, other], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
    }
}
