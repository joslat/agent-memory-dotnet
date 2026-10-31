#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 06: a pack says, for the model, what it knows and when to ask it (its card's <c>whenToUse</c>). That text is shown on
/// every turn, so it is the pack's fixed cost in a trial, counted in the frame the model reads it in.
/// </summary>
public sealed class WhenToUseTests
{
    private const string Said = "Ask for kata names, their moves and their order when a question names a kata or a belt.";
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly PackSigningKey _publisher = PackAuthoring.CreateKey();

    [Fact]
    public void A_card_s_when_to_use_reaches_the_module_and_its_digest()
    {
        var said = Load(Said);
        var silent = Load(null);

        said.Manifest.WhenToUse.Should().Be(Said, "the card's text travels into the module the host composes");
        silent.Manifest.WhenToUse.Should().BeNull();
        PackTrialGate.DigestOf(said.Manifest).Should().NotBe(PackTrialGate.DigestOf(silent.Manifest), "it is a declared cost");
        said.Card!.WhenToUse.Should().Be(Said);
        PackAdvice.Of(silent, DateTimeOffset.UtcNow).Select(a => a.Rule).Should().Contain("card.when-to-use", "a knowledge pack that says nothing for the model is advised");
        PackAdvice.Of(said, DateTimeOffset.UtcNow).Select(a => a.Rule).Should().NotContain("card.when-to-use");

        var shown = PackTrialRender.AlwaysShown(said.Manifest);
        shown.Should().Be($"<record module=\"{said.Manifest.Id}\" version=\"{said.Manifest.Version}\" type=\"whenToUse\">{Said}</record>");
        PackTrialRender.FixedBytes(said.Manifest).Should().Be(Encoding.UTF8.GetByteCount(shown!), "the counted bytes are the shown bytes");
        PackTrialRender.FixedBytes(silent.Manifest).Should().Be(0);
    }

    [Fact]
    public void A_trial_counts_the_text_against_every_task_s_saving()
    {
        // 40 tasks, the baseline 1,000 bytes each, the pack 600: 400 saved, less its fixed cost per turn.
        var with = Measured(600);
        var without = Measured(1000);
        var manifest = Load(Said).Manifest with
        {
            Trial = ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.notes","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","baseline":"host","tasks":40,"targets":{"hitRateMicro":100000},"cost":{"savingBytesMedian":300}}}""").Trial,
        };
        var fixedBytes = PackTrialRender.FixedBytes(manifest);

        var decision = PackTrialGate.Decide(manifest, with, without, DateTimeOffset.UnixEpoch);

        decision.FixedBytes.Should().Be(fixedBytes);
        decision.Checks.Single(c => c.Target == "savingBytes").Observed.Should().Be(400 - fixedBytes);
    }

    [Fact]
    public void The_host_refuses_a_long_summary_and_a_long_view_summary()
    {
        static IReadOnlyList<string> Problems(string extra) => ModuleManifest.Parse(
            $$"""{"format":"agentmemory.module/v1alpha1","id":"com.example.notes","version":"1.0.0","sdk":"[0.1,0.2)"{{extra}}}""").Validate();

        Problems($$""","whenToUse":"{{new string('x', 401)}}" """).Should().Contain(p => p.Contains("$.whenToUse"));
        Problems($$""","whenToUse":"{{new string('x', 400)}}" """).Should().NotContain(p => p.Contains("$.whenToUse"));
        var view = ",\"views\":[{\"id\":\"notes\",\"type\":\"ComExampleNotesNote\",\"limit\":5,\"summary\":\"" + new string('x', 161) + "\"}]";
        Problems(view)
            .Should().Contain(p => p.Contains(".summary"));
    }

    private static PackTrialMeasurement Measured(long bytes) => new()
    {
        Tasks = 40, Hits = 40, Offered = 40, Correct = 40, P95Ms = 1, Spacing = PackTrialGate.BurstLength,
        PerTask = [.. Enumerable.Range(0, 40).Select(_ => new PackTrialTask(1, 1) { OfferedBytes = bytes })],
    };

    private LoadedPack Load(string? whenToUse)
    {
        var card = JsonNode.Parse(PackAdviceTests.Card)!.AsObject();
        card.Remove("whenToUse");
        if (whenToUse is not null) card["whenToUse"] = whenToUse;
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal)
        {
            ["card.json"] = Encoding.UTF8.GetBytes(card.ToJsonString() + "\n"),
        };
        return PackLoader.Load(PackAuthoring.Build(source, _publisher.PrivateKeyPem), Trust());
    }

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["format"] = "agentmemory.pack.trust/1.1",
        ["keys"] = new JsonArray(new JsonObject
        {
            ["keyId"] = _publisher.KeyId, ["publicKey"] = _publisher.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.agentmemory"), ["roles"] = new JsonArray("publisher"),
        }),
    }.ToJsonString()));
}
