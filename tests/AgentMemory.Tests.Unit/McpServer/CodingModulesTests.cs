using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.McpHost.Coding;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable AMEXT001

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>Opt-in memory packs in coding mode (Extensibility 0.13 §M): configuration, composition and the module write request.</summary>
public sealed class CodingModulesTests
{
    private static CodingConfiguration Config(object? modules) => CodingConfiguration.Parse(JsonSerializer.Serialize(new
    {
        ownerId = "owner", projects = new { alpha = "ALPHA_TOKEN" }, modules,
    }), CodingConfigurationTests.EnvironmentValues().GetValueOrDefault);

    [Fact]
    public async Task WithoutModulesNothingIsComposed()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        CodingHost.Configure(builder, Config(null));
        await using var app = builder.Build();
        app.Services.GetService<ModuleSet>().Should().BeNull();
        app.Services.GetService<ModuleContextSelection>().Should().BeNull();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetService<IModuleIngestion>().Should().BeNull();
    }

    [Fact]
    public async Task TheRepositoryPackAndASignedKnowledgePackAreComposed()
    {
        var folder = Directory.CreateTempSubdirectory("coding-pack-").FullName;
        try
        {
            var key = BundleSigning.CreateKey();
            File.WriteAllText(Path.Combine(folder, "dev.public.pem"), key.PublicKeyPem);
            var bundle = Path.Combine(folder, "pack");
            Directory.CreateDirectory(Path.Combine(bundle, "seed"));
            foreach (var (path, bytes) in BundleSigning.Sign(new Dictionary<string, byte[]>
            {
                ["manifest.json"] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.module/v1alpha1","id":"io.example.codepack","version":"1.0.0","sdk":"[0.1,0.2)","seed":["seed/records.jsonl"],"ontology":{"labelPrefix":"CodePack","types":[{"name":"CodePackNote","scope":"shared","render":"{text}","properties":{"text":{"type":"string","required":true}}}]},"views":[{"id":"notes","type":"CodePackNote","search":{"properties":["text"]},"limit":4}]}"""),
                ["seed/records.jsonl"] = Encoding.UTF8.GetBytes("""{"type":"CodePackNote","key":"n1","properties":{"text":"Run dotnet test with -m:2"}}""" + "\n"),
            }, "io.example.codepack", "1.0.0", key.PrivateKeyPem))
                File.WriteAllBytes(Path.Combine(bundle, path.Replace('/', Path.DirectorySeparatorChar)), bytes);

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            CodingHost.Configure(builder, Config(new { repository = true, packs = new[] { bundle }, developerKeys = new[] { Path.Combine(folder, "dev.public.pem") } }));
            await using var app = builder.Build();
            var set = app.Services.GetRequiredService<ModuleSet>();
            set.Resolution.Modules.Select(m => m.Id).Should().BeEquivalentTo(["io.agentmemory.packs.repository", "io.example.codepack"]);
            app.Services.GetRequiredService<ModuleContextSelection>().Should().NotBeNull();
            app.Services.GetRequiredService<KnowledgePackImporter>().Should().NotBeNull();
            app.Services.GetRequiredService<CodingPacks>().Packs.Single().Bundle.Signer.Should().Be(BundleSigner.Developer);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("""{"repository":false}""")]
    [InlineData("""{"repository":true,"maxItems":0}""")]
    [InlineData("""{"repository":true,"packs":["a","b","c","d","e","f","g","h","i"]}""")]
    public void InvalidModuleSettingsAreRefused(string modules)
    {
        var act = () => Config(JsonDocument.Parse(modules).RootElement);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AnUnsignedPackStopsStartup()
    {
        var folder = Directory.CreateTempSubdirectory("coding-pack-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "manifest.json"), "{}");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            var act = () => CodingHost.Configure(builder, Config(new { packs = new[] { folder } }));
            act.Should().Throw<KnowledgePackException>();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void TheModuleWriteCarriesTheTurnsCommitSourceAndProjectFromTheHost()
    {
        var turn = new CodingRememberRequest { Session = "s", TurnId = "t1", UserText = "fixed MSB3021", Commit = new string('a', 40), Source = "claude-code" };
        var messages = new[] { new AgentMemory.Abstractions.Domain.Message { MessageId = "m1", ConversationId = "c", SessionId = "s", Role = "user", Content = "fixed MSB3021", TimestampUtc = DateTimeOffset.UnixEpoch } };
        var request = CodingModuleRecall.Request(new("owner", "alpha"), "s", messages, turn,
            new AgentMemory.Abstractions.Domain.ExtractionResult(), DateTimeOffset.UnixEpoch);
        request.HostAttributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["commit"] = new string('a', 40), ["source"] = "claude-code", ["repository"] = "alpha" });
        request.Messages.Single().Id.Should().Be("m1");
        request.Trigger.Should().Be(IngestionTrigger.EveryTurn);
    }
}
