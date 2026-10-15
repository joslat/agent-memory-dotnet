using System.Globalization;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleViewsTests
{
    [Fact]
    public async Task UsesInvariantFormattingAndTreatsStoredBracesAsData()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var reader = new Reader();
            var result = await new ModuleViews(Resolve("{{{title}}} {count} {ratio:F2} {date:d} {at:O} {active} {optional}"), reader).EvaluateAsync("items", new());
            result.Status.Should().Be(ModuleReadStatus.Succeeded);
            result.Items.Single().Text.Should().Be("{{count}} 9007199254740993 1.25 10/15/2026 2026-10-15T10:00:00.0000000+00:00 true ");
            result.Items.Single().Record.Source.MessageIds.Should().Equal("source");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task NoTemplateUsesOnlyDeclaredPropertiesInStableOrder()
    {
        var reader = new Reader();
        var result = await new ModuleViews(Resolve(null), reader).EvaluateAsync("items", new());
        result.Items.Single().Text.Should().Be("active=true; at=2026-10-15T10:00:00.0000000+00:00; count=9007199254740993; date=2026-10-15; ratio=1.25; title={count}");
        result.Items.Single().Text.Should().NotContain("undeclared");
    }

    [Fact]
    public async Task UnknownViewAndCancellationDoNotRead()
    {
        var reader = new Reader(); var views = new ModuleViews(Resolve("{title}"), reader);
        (await views.EvaluateAsync("unknown", new())).Status.Should().Be(ModuleReadStatus.Refused);
        reader.Calls.Should().Be(0);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var act = () => views.EvaluateAsync("items", new(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(); reader.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(ModuleReadStatus.NotApplicable)]
    [InlineData(ModuleReadStatus.Refused)]
    public async Task PreservesNonSuccessAndDoesNotRenderPartialRows(ModuleReadStatus status)
    {
        var reader = new Reader { Status = status };
        var result = await new ModuleViews(Resolve("{title}"), reader).EvaluateAsync("items", new());
        result.Status.Should().Be(status); result.Items.Should().BeEmpty(); result.Errors.Should().Equal("read problem");
    }

    [Fact]
    public async Task BadFormatIsRefusalRatherThanEmptySearchOrPartialSuccess()
    {
        var result = await new ModuleViews(Resolve("{count:Q}"), new Reader()).EvaluateAsync("items", new());
        result.Status.Should().Be(ModuleReadStatus.Refused); result.Items.Should().BeEmpty(); result.Errors.Single().Should().Contain("rendering failed");
    }

    [Fact]
    public async Task PassesExactViewQueryAndContextToBoundReader()
    {
        var reader = new Reader(); var context = new ModuleReadContext { SessionId = "session", SystemAsOfUtc = DateTimeOffset.UtcNow };
        await new ModuleViews(Resolve("{title}"), reader).EvaluateAsync("items", context);
        reader.Context.Should().BeSameAs(context); reader.Query!.ModuleId.Should().Be(reader.CallerModuleId);
        reader.Query.Type.Should().Be("ViewsItem"); reader.Query.Limit.Should().Be(3);
        reader.Query.OrderBy!.Descending.Should().BeTrue();
    }

    [Fact]
    public async Task RuntimeArgumentsRestrictBeforeReadAndPreserveDeclaredPredicates()
    {
        var reader = new Reader(); var resolution = Resolve("{title}");
        IModuleViews views = new ModuleViews(resolution, reader);
        var links = new List<ModuleLinkFilter> { new() { Relation = "VIEWS_ABOUT", TargetId = "entity" } };
        var context = new ModuleReadContext { SessionId = "session" };
        var result = await views.EvaluateWithArgumentsAsync("items", new() { Key = "commitment", Links = links, Limit = 1 }, context);
        result.Status.Should().Be(ModuleReadStatus.Succeeded);
        links.Clear();
        reader.Query!.Key.Should().Be("commitment"); reader.Query.Limit.Should().Be(1);
        reader.Query.Links.Single().TargetId.Should().Be("entity");
        reader.Query.Where.Should().BeEquivalentTo(resolution.Modules.Single().Views.Single().Where);
        reader.Query.OrderBy!.Descending.Should().BeTrue(); reader.Context.Should().BeSameAs(context);
    }

    [Fact]
    public async Task RuntimeArgumentsCannotExpandLimitOrSilentlyDropMalformedRestrictions()
    {
        var reader = new Reader(); IModuleViews views = new ModuleViews(Resolve("{title}"), reader);
        foreach (var arguments in new ModuleViewArguments[] { new() { Limit = 4 }, new() { Limit = 0 }, new() { Key = " " },
            new() { Links = null! }, new() { Links = [null!] }, new() { Links = [new() { Relation = "VIEWS_UNKNOWN", TargetId = "entity" }] } })
        {
            var result = await views.EvaluateWithArgumentsAsync("items", arguments, new());
            result.Status.Should().Be(ModuleReadStatus.Refused); result.Items.Should().BeEmpty();
        }
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EmptyArgumentsHaveTheSameQueryAsTheOriginalEntryPoint()
    {
        var reader = new Reader(); IModuleViews views = new ModuleViews(Resolve("{title}"), reader);
        await views.EvaluateAsync("items", new()); var original = reader.Query;
        await views.EvaluateWithArgumentsAsync("items", new(), new());
        reader.Query.Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task LegacyViewImplementationsRefuseRuntimeArguments()
    {
        IModuleViews views = new LegacyViews();
        (await views.EvaluateWithArgumentsAsync("items", new() { Key = "required" }, new())).Status.Should().Be(ModuleReadStatus.Refused);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var action = () => views.EvaluateWithArgumentsAsync("items", new(), new(), cts.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class LegacyViews : IModuleViews
    {
        public Task<ModuleViewResult> EvaluateAsync(string viewId, ModuleReadContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleViewResult { Status = ModuleReadStatus.Succeeded });
    }

    private static ModuleResolution Resolve(string? render) => ModuleResolver.Resolve([new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example.views", Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new() { LabelPrefix = "Views", Types = [new() { Name = "ViewsItem", Render = render, Properties = new Dictionary<string, ModuleProperty>
        { ["title"] = new() { Type = "string", Required = true }, ["count"] = new() { Type = "int" }, ["ratio"] = new() { Type = "float" },
            ["date"] = new() { Type = "date" }, ["at"] = new() { Type = "datetime" }, ["active"] = new() { Type = "bool" }, ["optional"] = new() { Type = "string" } } }], Relations = [new() { Name = "VIEWS_ABOUT", From = "ViewsItem", To = "core:Entity" }] },
        Views = [new() { Id = "items", Type = "ViewsItem", Limit = 3, Where = [new() { Property = "active", Operator = "=", Value = JsonSerializer.SerializeToElement(true) }], OrderBy = new() { Property = "count", Descending = true } }]
    }], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });

    private sealed class Reader : IModuleReader
    {
        public string CallerModuleId => "com.example.views";
        public int Calls { get; private set; }
        public ModuleQuery? Query { get; private set; }
        public ModuleReadContext? Context { get; private set; }
        public ModuleReadStatus Status { get; init; } = ModuleReadStatus.Succeeded;
        public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            Calls++; Query = query; Context = context;
            var properties = new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("{count}"), ["count"] = JsonSerializer.SerializeToElement(9007199254740993L),
                ["ratio"] = JsonSerializer.SerializeToElement(1.25), ["date"] = JsonSerializer.SerializeToElement("2026-10-15"),
                ["at"] = JsonSerializer.SerializeToElement("2026-10-15T12:00:00+02:00"), ["active"] = JsonSerializer.SerializeToElement(true),
                ["undeclared"] = JsonSerializer.SerializeToElement("must not render")
            };
            return Task.FromResult(new ModuleReadResult { Status = Status, Errors = Status == ModuleReadStatus.Succeeded ? [] : ["read problem"],
                Records = [new() { ModuleId = CallerModuleId, Type = "ViewsItem", Id = "version-1", Key = "item", CreatedAtUtc = DateTimeOffset.UnixEpoch,
                    SourceSessionId = "session", Source = new ProposalSource { MessageIds = ["source"], Excerpt = "recorded source" }, Properties = properties }] });
        }
    }
}
