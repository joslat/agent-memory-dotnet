using System.Text.Json;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleHistoryReadTests
{
    [Fact]
    public void HistoryRequiresAnExactKeyAndRefusesPropertySortsAndUnknownSelections()
    {
        var query = new ModuleQuery { ModuleId = "com.example.data", Type = "DataItem", Limit = 10, Selection = ModuleRecordSelection.History };
        ModuleQueryAdmissionResult Admit(ModuleQuery q) => ModuleQueryAdmission.Admit(ModuleQueryAdmissionTests.Resolve(), q.ModuleId, q, new(), DateTimeOffset.UnixEpoch);
        Admit(query).Status.Should().Be(ModuleReadStatus.Refused);
        Admit(query with { Key = "one" }).Status.Should().Be(ModuleReadStatus.Succeeded);
        Admit(query with { Key = "one", OrderBy = new() { Property = "title" } }).Status.Should().Be(ModuleReadStatus.Refused);
        Admit(query with { Key = "one", Selection = (ModuleRecordSelection)42 }).Status.Should().Be(ModuleReadStatus.Refused);
    }

    [Fact]
    public void ParserAcceptsHistoryAndDefaultSelectionDoesNotChangeSerializedMetadata()
    {
        var parsed = ModuleManifest.Parse(Manifest("history"));
        parsed.Views.Single().Selection.Should().Be(ModuleRecordSelection.History);
        parsed.Validate().Should().BeEmpty();
        var snapshot = ModuleManifest.Parse(Manifest("snapshot"));
        snapshot.Views.Single().Selection.Should().Be(ModuleRecordSelection.Snapshot);
        JsonSerializer.SerializeToElement(snapshot.Views.Single()).TryGetProperty("Selection", out _).Should().BeFalse();
        Resolve(parsed).ManifestFingerprint.Should().NotBe(Resolve(snapshot).ManifestFingerprint);
        var implicitSnapshot = snapshot with { Views = [new() { Id = "versions", Type = "HistoryItem", Limit = 10 }] };
        Resolve(snapshot).ManifestFingerprint.Should().Be(Resolve(implicitSnapshot).ManifestFingerprint);
    }

    [Fact]
    public void HistoryViewsRefusePropertyOrderingAndUnknownEnumValues()
    {
        var manifest = BaselineManifest(); var view = manifest.Views.Single();
        (manifest with { Views = [view with { Selection = ModuleRecordSelection.History, OrderBy = new() { Property = "title" } }] }).Validate().Should().NotBeEmpty();
        (manifest with { Views = [view with { Selection = (ModuleRecordSelection)99 }] }).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public async Task SharedViewPassesDeclaredHistorySelectionButRefusesAMissingKeyBeforeCallingReader()
    {
        var manifest = BaselineManifest();
        manifest = manifest with { Views = [manifest.Views.Single() with { Selection = ModuleRecordSelection.History }] };
        var reader = new Reader(); IModuleViews views = new ModuleViews(Resolve(manifest), reader);
        (await views.EvaluateAsync("versions", new())).Status.Should().Be(ModuleReadStatus.Refused);
        reader.Query.Should().BeNull();
        var result = await views.EvaluateWithArgumentsAsync("versions", new() { Key = "one", Limit = 2 }, new());
        result.Status.Should().Be(ModuleReadStatus.Succeeded);
        reader.Query!.Selection.Should().Be(ModuleRecordSelection.History);
        reader.Query.Key.Should().Be("one"); reader.Query.Limit.Should().Be(2);
    }

    private static ModuleManifest BaselineManifest() => ModuleManifest.Parse(Manifest("snapshot").Replace("\"selection\":\"snapshot\",", "", StringComparison.Ordinal));
    internal static string Manifest(string selection) => """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.history","version":"1.0.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"History","types":[{"name":"HistoryItem","properties":{"title":{"type":"string"}}}]},
         "views":[{"id":"versions","type":"HistoryItem","selection":"SELECTION","limit":10}]}
        """.Replace("SELECTION", selection, StringComparison.Ordinal);
    private static ModuleResolution Resolve(ModuleManifest m) => ModuleResolver.Resolve([m], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
    private sealed class Reader : IModuleReader
    {
        public string CallerModuleId => "com.example.history";
        public ModuleQuery? Query { get; private set; }
        public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        { Query = query; return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded }); }
    }
}
