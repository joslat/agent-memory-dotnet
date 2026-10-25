using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Search views (Extensibility 0.13 §K4): terms, admission, evaluation and the contributor's question.</summary>
public sealed class ModuleSearchTests
{
    private const string Module = "io.example.dojo";

    [Fact]
    public void TermsAreLowercaseDistinctWordsWithoutStopWordsOrQuerySyntax()
    {
        ModuleSearch.TermsOf("What kata do I need for my next belt? Kata! (belt*) -OR- \"Heian\"")
            .Should().Equal("kata", "need", "next", "belt", "heian");
        ModuleSearch.TermsOf("   ").Should().BeEmpty();
        ModuleSearch.TermsOf(string.Join(" ", Enumerable.Range(0, 50).Select(i => "word" + i))).Should().HaveCount(ModuleSearch.MaximumTerms);
    }

    [Fact]
    public async Task ASearchViewSendsTheTermsOfItsTextToTheReader()
    {
        var reader = new Reader();
        var result = await Views(reader).EvaluateWithArgumentsAsync("kata", new() { Search = "Which techniques are in Heian Shodan?" }, Context());
        result.Status.Should().Be(ModuleReadStatus.Succeeded);
        reader.Query!.Search!.ViewId.Should().Be("kata");
        reader.Query.Search.Terms.Should().Equal("techniques", "heian", "shodan");
        reader.Query.OrderBy.Should().BeNull();
        reader.Query.Limit.Should().Be(12);
    }

    [Fact]
    public async Task WithoutTextASearchViewIsNotApplicableAndOnlyStopWordsSearchNothing()
    {
        var reader = new Reader();
        (await Views(reader).EvaluateAsync("kata", Context())).Status.Should().Be(ModuleReadStatus.NotApplicable);
        var empty = await Views(reader).EvaluateWithArgumentsAsync("kata", new() { Search = "what is it?" }, Context());
        empty.Status.Should().Be(ModuleReadStatus.Succeeded);
        empty.Items.Should().BeEmpty();
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AListViewRefusesSearchText()
    {
        var result = await Views(new Reader()).EvaluateWithArgumentsAsync("ranks", new() { Search = "belt" }, Context());
        result.Status.Should().Be(ModuleReadStatus.Refused);
    }

    [Theory]
    [InlineData("kata", "heian*")]
    [InlineData("kata", "Heian")]
    [InlineData("kata", "a")]
    [InlineData("ranks", "belt")]
    [InlineData("missing", "belt")]
    public void AdmissionRefusesSyntaxUppercaseShortTermsAndNonSearchViews(string view, string term)
    {
        var query = new ModuleQuery { ModuleId = Module, Type = "DojoKata", Limit = 12, Search = new() { ViewId = view, Terms = [term] } };
        ModuleQueryAdmission.Admit(Resolution(), Module, query, Context(), DateTimeOffset.UnixEpoch).Status.Should().Be(ModuleReadStatus.Refused);
    }

    [Fact]
    public void AdmissionKeepsAnIndependentCopyOfTheTerms()
    {
        var terms = new List<string> { "heian" };
        var query = new ModuleQuery { ModuleId = Module, Type = "DojoKata", Limit = 12, Search = new() { ViewId = "kata", Terms = terms } };
        var admitted = ModuleQueryAdmission.Admit(Resolution(), Module, query, Context(), DateTimeOffset.UnixEpoch);
        admitted.Status.Should().Be(ModuleReadStatus.Succeeded);
        terms.Add("shodan");
        admitted.Query!.Search!.Terms.Should().Equal("heian");
    }

    [Fact]
    public async Task TheContributorSearchesWithTheTurnsQuestion()
    {
        var record = new ModuleRecord { ModuleId = Module, Type = "DojoKata", Id = "k1", Key = "shotokan/heian-shodan",
            CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "pack:io.example.dojo", Source = new() { Excerpt = "seed/records.jsonl:1" },
            Properties = new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("Heian Shodan"), ["summary"] = JsonSerializer.SerializeToElement("21 moves"),
            } };
        var reader = new Reader { Result = new() { Status = ModuleReadStatus.Succeeded, Records = [record] } };
        var compiler = Compiler(reader);

        var envelope = await compiler.CompileAsync(new() { SessionId = "s", Owner = "lea", Query = "Which techniques are in Heian Shodan?" });
        envelope.Sections.Single().Items.Single().Text.Should().Be("Heian Shodan: 21 moves");
        reader.Query!.Search!.Terms.Should().Equal("techniques", "heian", "shodan");

        var none = await compiler.CompileAsync(new() { SessionId = "s", Owner = "lea" });
        none.Sections.Should().BeEmpty();
        none.Omissions.Single().Reason.Should().Be(ContextOmissionReason.NotApplicable);
    }

    private static ModuleViews Views(Reader reader) => new(Resolution(), reader);
    private static ModuleReadContext Context() => new() { Scope = MemoryScope.For("lea") };

    private static ContextCompiler Compiler(Reader reader)
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(null, Arg.Any<string?>(), Arg.Any<string>(), AgentMemory.Abstractions.Domain.MemoryOperationAccess.Tenant)
            .Returns(MemoryScope.For("lea"));
        return new ContextCompiler([new ViewContributor(Resolution(), reader, "kata")], policy, TimeProvider.System, NullLogger<ContextCompiler>.Instance);
    }

    private static ModuleResolution Resolution()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(KnowledgePackManifestTests.Pack)!;
        root["views"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "ranks", ["type"] = "DojoRank", ["limit"] = 20 });
        var json = root.ToJsonString();
        var resolution = ModuleResolver.Resolve([ModuleManifest.Parse(json)], ModuleVersion.Parse("0.1.0"), ModuleHostFeatures.Known);
        resolution.Errors.Should().BeEmpty();
        return resolution;
    }

    private sealed class Reader : IModuleReader
    {
        public string CallerModuleId => Module;
        public ModuleQuery? Query { get; private set; }
        public int Calls { get; private set; }
        public ModuleReadResult Result { get; init; } = new() { Status = ModuleReadStatus.Succeeded };
        public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        { Calls++; Query = query; return Task.FromResult(Result); }
    }
}
