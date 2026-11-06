using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Repository;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>The repository-experience pack (Extensibility 0.13 §C): its manifest, its writer's policy and the host's commit.</summary>
public sealed class RepositoryPackTests
{
    private static IngestionRequest Turn(IReadOnlyDictionary<string, string>? host = null) => new()
    {
        SessionId = "s", Owner = "dev",
        Messages = [new() { Id = "m1", Role = "user", Text = "MSB3021: the arena held the DLLs; --artifacts-path artifacts/alt-build fixed it." }],
        HostAttributes = host,
    };

    private const string Reply = """
        {"lessons":[
          {"key":"msb3021-dll-lock","kind":"failure-fix","lesson":"MSB3021 means a process holds the DLLs; build with --artifacts-path artifacts/alt-build.",
           "failure":"MSB3021 Unable to copy file","fix":"dotnet build --artifacts-path artifacts/alt-build","component":"tests/Integration",
           "commit":"model-made-this-up","verified":true,"repository":"from-the-model"},
          {"key":"no-kind","lesson":"A lesson without a kind is left out."},
          {"key":"bad-kind","kind":"opinion","lesson":"An unknown kind is left out."},
          {"key":"build-cmd","kind":"command","lesson":"Build with -m:2 to keep memory down.","command":"dotnet build -m:2"}]}
        """;

    [Fact]
    public void TheManifestIsAValidOwnerModuleWithASearchViewForRecall()
    {
        var manifest = RepositoryPack.Manifest;
        manifest.Validate().Should().BeEmpty();
        manifest.Id.Should().Be(RepositoryPack.Id);
        manifest.IsKnowledgePack.Should().BeFalse();
        manifest.Host.Should().Equal("Inference");
        manifest.Ontology!.Types.Single().Scope.Should().Be("owner");
        manifest.Views.Single(v => v.Id == RepositoryPack.RelevantView).Search!.Properties.Should().Contain("failure");
        manifest.Display!.Title.Should().Be("Repository experience");
    }

    [Fact]
    public void TheWriterKeepsKnownKindsAndStampsTheHostsCommitNotTheModels()
    {
        var proposals = RepositoryWriter.Parse(Reply, Turn(new Dictionary<string, string> { ["commit"] = "0123abcd", ["repository"] = "agent-memory-dotnet" }),
            new RepositoryPackOptions());
        proposals.Select(p => p.Key).Should().Equal("msb3021-dll-lock", "build-cmd");
        var lesson = proposals[0].Properties;
        lesson["kind"].GetString().Should().Be("failure-fix");
        lesson["commit"].GetString().Should().Be("0123abcd");
        lesson["repository"].GetString().Should().Be("agent-memory-dotnet");
        lesson["component"].GetString().Should().Be("tests/Integration");
        lesson.Should().NotContainKey("verified");
        proposals[1].Properties["command"].GetString().Should().Be("dotnet build -m:2");
    }

    [Fact]
    public void WithoutAHostCommitALessonCarriesNoneAndTheConfiguredRepositoryWins()
    {
        var proposals = RepositoryWriter.Parse(Reply, Turn(new Dictionary<string, string> { ["repository"] = "from-the-host" }),
            new RepositoryPackOptions { Repository = "configured" });
        proposals[0].Properties.Should().NotContainKey("commit");
        proposals[0].Properties["repository"].GetString().Should().Be("configured");
    }

    [Fact]
    public void EveryProposalPassesTheManifestsOwnAdmission()
    {
        var resolution = ModuleResolver.Resolve([RepositoryPack.Manifest], ModuleVersion.Parse("0.1.0"), ModuleHostFeatures.Known);
        resolution.Errors.Should().BeEmpty();
        var turn = Turn(new Dictionary<string, string> { ["commit"] = "0123abcd" });
        var provider = new IngestionProviderDescriptor { Id = RepositoryPack.Id + ":writer" };
        foreach (var proposal in RepositoryWriter.Parse(Reply, turn, new RepositoryPackOptions()))
            ModuleProposalAdmission.Admit(resolution, provider, turn, proposal).Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Commit", "abc")]
    [InlineData("commit", "line\nbreak")]
    public void HostAttributesAreBoundedLowercaseKeysAndPlainValues(string key, string value)
    {
        var bad = Turn(new Dictionary<string, string> { [key] = value });
        FluentActions.Invoking(() => ModuleIngestionSnapshot.Request(bad, "dev")).Should().Throw<ArgumentException>().WithMessage("*Host attributes*");
        var nine = Turn(Enumerable.Range(0, 9).ToDictionary(i => "k" + i, _ => "v"));
        FluentActions.Invoking(() => ModuleIngestionSnapshot.Request(nine, "dev")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HostAttributesJoinTheFingerprintOnlyWhenPresent()
    {
        var plain = ModuleIngestionSnapshot.Fingerprint(ModuleIngestionSnapshot.Request(Turn(), "dev"));
        JsonSerializer.Serialize(ModuleIngestionSnapshot.Request(Turn(), "dev")).Should().NotContain("HostAttributes");
        var stamped = ModuleIngestionSnapshot.Fingerprint(ModuleIngestionSnapshot.Request(Turn(new Dictionary<string, string> { ["commit"] = "0123abcd" }), "dev"));
        stamped.Should().NotBe(plain);
    }
}
