#pragma warning disable AMEXT001 // Explicit operator tooling consumes the held module API.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Cli;
using AgentMemory.Cli.Commands;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class ModuleCommandInputTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("agentmemory-module-cli-");
    private const string Manifest = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.notes","version":"0.1.0","sdk":"[0.1,0.2)"}
        """;
    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
    private string Profile(string manifest = Manifest, string? pin = null, string store = "main", bool second = false)
    {
        Write("module.json", manifest);
        var stores = new Dictionary<string, object> { ["main"] = new { provider = "neo4j-direct" } };
        if (second) stores["other"] = new { provider = "neo4j-direct" };
        var entry = new Dictionary<string, object> { ["id"] = "com.example.notes", ["version"] = "[0.1,0.2)", ["store"] = store, ["manifest"] = "module.json" };
        if (pin is not null) entry["sha256"] = pin;
        return Write("profile.json", JsonSerializer.Serialize(new { format = "agentmemory.modules/v1alpha1", stores, modules = new[] { entry } }));
    }
    private static ModuleCommandInput Load(string path, params string[] rest) =>
        ModuleCommandInput.Load(CliArgs.Parse(["modules", "plan", "--profile", path, .. rest]));

    [Fact]
    public void ResolvesRelativeSourcesAndFreezesTheirVerifiedIdentity()
    {
        var pin = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Manifest))).ToLowerInvariant();
        var path = Profile(pin: pin);
        var input = Load(path);
        File.WriteAllText(Path.Combine(_directory.FullName, "module.json"), "changed after validation");
        input.Store.Should().Be("main");
        input.Resolution.Modules.Single().Id.Should().Be("com.example.notes");
        input.Sources.Single().Sha256.Should().Be(pin);
        input.ProfileSha256.Should().Be(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        input.TargetHostFeatures.Should().Equal("ModuleSchema");
    }

    [Fact]
    public void RejectsChangedPinnedBytesBeforeParsingThem()
    {
        var path = Profile("invalid JSON", new string('0', 64));
        FluentActions.Invoking(() => Load(path)).Should().Throw<FormatException>().WithMessage("*SHA-256*");
    }

    [Theory]
    [InlineData("com.example.other", "0.1.0", "identity")]
    [InlineData("com.example.notes", "0.2.0", "version")]
    public void ChecksEachProfileEntryAgainstItsActualSource(string id, string version, string reason)
    {
        var path = Profile(Manifest.Replace("com.example.notes", id).Replace("\"version\":\"0.1.0\"", "\"version\":\"" + version + "\""));
        FluentActions.Invoking(() => Load(path)).Should().Throw<FormatException>().WithMessage("*" + reason + "*");
    }

    [Fact]
    public void RequiresAnExplicitStoreForMultipleStoresAndRejectsUnknownAliases()
    {
        var path = Profile(second: true);
        FluentActions.Invoking(() => Load(path)).Should().Throw<FormatException>().WithMessage("*--store*");
        FluentActions.Invoking(() => Load(path, "--store", "Main")).Should().Throw<FormatException>().WithMessage("*store*");
        Load(path, "--store", "other").Resolution.Modules.Should().BeEmpty();
    }

    [Fact]
    public void DoesNotSilentlyInventHostCapabilities()
    {
        var path = Profile(Manifest[..^1] + ",\"requires\":{\"host\":[\"History\"]}}");
        FluentActions.Invoking(() => Load(path)).Should().Throw<FormatException>().WithMessage("*History*");
        Load(path, "--host-features", "History").TargetHostFeatures.Should().Equal("History", "ModuleSchema");
        FluentActions.Invoking(() => Load(path, "--host-features", "Everything")).Should().Throw<FormatException>();
    }

    [Fact]
    public void AnEmptySelectedStoreDoesNotHideAnInvalidWholeProfile()
    {
        var path = Profile(Manifest[..^1] + ",\"requires\":{\"modules\":{\"com.example.missing\":\"[0.1,0.2)\"}}}", second: true);
        FluentActions.Invoking(() => Load(path, "--store", "other")).Should().Throw<FormatException>().WithMessage("*missing*");
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("unknown")]
    public void RejectsInvalidOperationOrMissingActorBeforeOpeningTheProfile(string operation)
    {
        FluentActions.Invoking(() => ModuleCommandInput.Load(CliArgs.Parse(["modules", operation, "--profile", "nonexistent.json"])))
            .Should().Throw<FormatException>();
    }

    [Fact]
    public void PreservesApplyActorAndRejectsExtraPositionals()
    {
        var path = Profile();
        var input = ModuleCommandInput.Load(CliArgs.Parse(["modules", "apply", "--profile", path, "--actor", "release-operator"]));
        input.Operation.Should().Be("apply"); input.Actor.Should().Be("release-operator");
        FluentActions.Invoking(() => ModuleCommandInput.Load(CliArgs.Parse(["modules", "plan", "extra", "--profile", path])))
            .Should().Throw<FormatException>();
    }

    [Fact]
    public void RefusesOversizedProfileAndManifestBeforeParsing()
    {
        var profile = Write("oversized.json", new string(' ', 128 * 1024 + 1));
        FluentActions.Invoking(() => Load(profile)).Should().Throw<FormatException>().WithMessage("*size*");
        profile = Profile(new string(' ', 128 * 1024 + 1));
        FluentActions.Invoking(() => Load(profile)).Should().Throw<FormatException>().WithMessage("*size*");
    }

    [Fact]
    public void RejectsMalformedUtf8RatherThanHashingReplacementCharacters()
    {
        var path = Profile();
        File.WriteAllBytes(Path.Combine(_directory.FullName, "module.json"), [0xff, 0xfe, 0x80]);
        FluentActions.Invoking(() => Load(path)).Should().Throw<FormatException>().WithMessage("*UTF-8*");
    }

    [Fact]
    public void RejectsUnsupportedProviderBeforeConnecting()
    {
        var path = Profile();
        File.WriteAllText(path, File.ReadAllText(path).Replace("neo4j-direct", "another-provider"));
        FluentActions.Invoking(() => Load(path)).Should().Throw<FormatException>().WithMessage("*provider*");
    }

    [Fact]
    public void RefusesUnknownOptionsInsteadOfApplyingAnIgnoredDryRunFlag()
    {
        var path = Profile();
        FluentActions.Invoking(() => ModuleCommandInput.Load(CliArgs.Parse(
            ["modules", "apply", "--profile", path, "--actor", "operator", "--dry-run"])))
            .Should().Throw<FormatException>().WithMessage("*--dry-run*");
    }

    [Fact]
    public void InspectsAssemblyManifestAndScriptsWithoutRunningInitializerOrAttributes()
    {
        var image = Path.Combine(AppContext.BaseDirectory, "ModuleFixtures", "ModuleMigrationFixture.dll");
        var path = Write("profile.json", JsonSerializer.Serialize(new
        {
            format = "agentmemory.modules/v1alpha1",
            stores = new { main = new { provider = "neo4j-direct" } },
            modules = new[] { new { id = "com.example.migrationfixture", version = "[0.1,0.2)", store = "main", assembly = image } }
        }));
        var input = Load(path);
        input.Scripts.Single().Key.Should().Be("0001_backfill");
        input.Sources.Single().Kind.Should().Be("assembly");
        AppDomain.CurrentDomain.GetAssemblies().Should().NotContain(a => a.GetName().Name == "ModuleMigrationFixture");
    }

    [Theory]
    [InlineData("--uri")]
    [InlineData("--database")]
    [InlineData("--user")]
    [InlineData("--password")]
    public void RejectsValuelessConnectionOverridesRatherThanFallingBackToAnotherTarget(string option)
    {
        var path = Profile();
        FluentActions.Invoking(() => Load(path, option)).Should().Throw<FormatException>().WithMessage("*" + option + "*");
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
