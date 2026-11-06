using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Review 8, F1, F2 and F9: what a reader refuses without a key, the writers and lint refuse too. The envelope shapes and a set's
/// structure lived only where a key was at hand, so <c>ampack build</c> wrote a junk envelope, <c>ampack set</c> wrote a set
/// inside a set, and lint passed both. They are one rule now, in <c>PackVerifier.CheckIntegrity</c>.
/// </summary>
public sealed class Review8WriterGuardTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");
    private readonly PackSigningKey _key = PackAuthoring.CreateKey();

    private PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(_key.PublicKeyPem, "io.agentmemory", "io.example")));

    private byte[] Archive(PackSigningKey? key = null) => PackAuthoring.Build(new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(
        Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal)), StringComparer.Ordinal),
        (key ?? _key).PrivateKeyPem);

    private static Dictionary<string, byte[]> Files(byte[] archive) =>
        new(StrictZip.Read(archive, PackLimits.Default), StringComparer.Ordinal);

    [Fact]
    public void F1_an_envelope_every_reader_refuses_is_refused_by_the_writer_and_by_lint()
    {
        var junk = Files(Archive());
        junk["META/signatures/junk.dsse.json"] = "{}"u8.ToArray();
        // A stale envelope: a real signature, over another pack's list.
        var stale = Files(Archive());
        var other = Files(PackAuthoring.BuildSet("io.example.other-set", "1.0.0", [Archive()], _key.PrivateKeyPem, Trust()));
        stale["META/signatures/stale.dsse.json"] = other.First(f => PackPaths.IsEnvelope(f.Key)).Value;

        foreach (var (files, reason) in new[] { (junk, "*junk.dsse.json is not a DSSE envelope*"), (stale, "*stale.dsse.json signs another*") })
        {
            FluentActions.Invoking(() => PackBuilder.Pack(files)).Should().Throw<InvalidDataException>().WithMessage("No reader would accept this pack" + reason);
            PackVerifier.CheckIntegrity(new PackContent(files, "lint")).Problems.Should().ContainMatch(reason);
        }
    }

    [Fact]
    public void F4_the_library_acts_only_on_packs_its_host_s_trust_verifies()
    {
        // The library twins of countersign, lock and set checked integrity only, so they signed, pinned and carried packs no key
        // the host trusts had signed; they verify each pack whole under the host's trust now, as the commands do.
        var foreign = Archive(PackAuthoring.CreateKey());
        FluentActions.Invoking(() => PackAuthoring.Countersign(foreign, _key.PrivateKeyPem, "assurer", Trust()))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("No valid publisher signature"));
        FluentActions.Invoking(() => PackAuthoring.BuildSet("io.example.foreign-set", "1.0.0", [foreign], _key.PrivateKeyPem, Trust()))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("No valid publisher signature"));
        // Review 9, D11: the composite's own call site, not only the helper it shares.
        var composite = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes("{\"format\":\"agentmemory.composite/v1alpha1\",\"id\":\"io.example.family\",\"version\":\"1.0.0\",\"members\":[{\"id\":\"io.agentmemory.packs.martialarts\",\"version\":\"[1,2)\"}]}"),
        }), StringComparer.Ordinal);
        FluentActions.Invoking(() => PackAuthoring.BuildComposite(composite, [foreign], _key.PrivateKeyPem, Trust()))
            .Should().Throw<PackLoadException>().Which.Problems.Should().Contain(p => p.Contains("No valid publisher signature"));

        // A host that requires a curator: the pack holds under its policy apart from the curator this signature adds.
        var curator = PackAuthoring.CreateKey();
        var both = PackTrust.Parse(Encoding.UTF8.GetBytes($$"""
            {"format":"agentmemory.pack.trust/1.1","requireRoles":["publisher","curator"],"keys":[
            {"keyId":"{{PackKey.KeyIdOf(_key.PublicKeyPem)}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(_key.PublicKeyPem)}},"idPrefixes":["io.agentmemory"],"roles":["publisher"]},
            {"keyId":"{{PackKey.KeyIdOf(curator.PublicKeyPem)}}","publicKey":{{System.Text.Json.JsonSerializer.Serialize(curator.PublicKeyPem)}},"idPrefixes":["io.agentmemory"],"roles":["curator"]}]}
            """));
        PackLoader.Load(PackAuthoring.Countersign(Archive(), curator.PrivateKeyPem, "curator", both), both).Signers.Should().HaveCount(2);
    }

    [Fact]
    public void F2_a_set_every_reader_refuses_is_refused_by_the_writer_and_by_lint()
    {
        var inner = PackAuthoring.BuildSet("io.example.dojo-set", "1.0.0", [Archive()], _key.PrivateKeyPem, Trust());

        FluentActions.Invoking(() => PackAuthoring.BuildSet("io.example.outer", "1.0.0", [inner], _key.PrivateKeyPem, Trust()))
            .Should().Throw<PackLoadException>().WithMessage("*a set carries packs, not sets*");
        FluentActions.Invoking(() => PackAuthoring.BuildSet("io.agentmemory.packs.martialarts", "2.0.0", [Archive()], _key.PrivateKeyPem, Trust()))
            .Should().Throw<PackLoadException>().WithMessage("*carries a member with its own id*");

        // Each member checked without trust as a pack of its own: a member that no longer matches its own list refuses the set
        // at lint, though the set's list (built around it) matches.
        var member = Files(Archive());
        var record = member.Keys.First(k => k.EndsWith(".jsonl", StringComparison.Ordinal));
        member[record] = [.. member[record], .. """{"type":"Note","key":"added","text":"well formed, unlisted by the member"}"""u8, .. "\n"u8];
        var (damaged, _) = PackBuilder.Build(PackSets.Source("io.example.damaged-set", "1.0.0",
            new Dictionary<string, IReadOnlyDictionary<string, byte[]>>(StringComparer.Ordinal) { ["io.agentmemory.packs.martialarts"] = member }));
        PackVerifier.CheckIntegrity(new PackContent(damaged, "lint")).Problems
            .Should().ContainMatch($"packs/io.agentmemory.packs.martialarts/: '{record}' does not match*");
    }
}
