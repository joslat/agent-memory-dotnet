using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-harden: mutation fuzzing of what a host runs on a pack before anything reaches a store. A mutated .ampack of the
/// real martial arts pack loads only if it is the signed content, and is otherwise refused with a PackLoadException; a
/// mutated module manifest parses or is refused with a FormatException. Seeded and bounded.
/// </summary>
public sealed class PackFuzzTests
{
    private static readonly string MartialArts = Path.Combine(AppContext.BaseDirectory, "Packs", "MartialArts");

    private static byte[] Mutate(byte[] input, Random random)
    {
        var bytes = new List<byte>(input);
        for (var edits = random.Next(1, 4); edits > 0 && bytes.Count > 0; edits--)
        {
            var at = random.Next(bytes.Count);
            switch (random.Next(5))
            {
                case 0: bytes[at] ^= (byte)(1 << random.Next(8)); break;
                case 1: bytes[at] = (byte)random.Next(256); break;
                case 2: bytes.Insert(at, (byte)random.Next(256)); break;
                case 3: bytes.RemoveAt(at); break;
                default: bytes.RemoveRange(at, Math.Min(random.Next(1, 64), bytes.Count - at)); break;
            }
        }
        return [.. bytes];
    }

    [Fact]
    public void A_mutated_ampack_loads_only_as_the_signed_pack_and_is_otherwise_refused_with_its_reasons()
    {
        var key = PackAuthoring.CreateKey();
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.PublicKeyPem, "io.agentmemory")));
        var source = PackAuthoring.FromModuleBundle(Directory.EnumerateFiles(MartialArts, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(MartialArts, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal));
        var archive = PackAuthoring.Build(new Dictionary<string, byte[]>(source, StringComparer.Ordinal), key.PrivateKeyPem);
        var original = PackLoader.Load(archive, trust);
        var random = new Random(2026_10_27);
        for (var run = 0; run < 1_500; run++)
        {
            var mutated = Mutate(archive, random);
            try
            {
                var loaded = PackLoader.Load(mutated, trust);
                loaded.Digest.Should().Be(original.Digest, $"run {run}: only the signed content loads");
            }
            catch (PackLoadException e)
            {
                e.Problems.Should().NotBeEmpty($"run {run}");
            }
        }
    }

    [Fact]
    public void A_mutated_module_manifest_parses_or_is_refused_with_a_format_error()
    {
        var manifest = File.ReadAllBytes(Path.Combine(MartialArts, "manifest.json"));
        var random = new Random(0_13);
        for (var run = 0; run < 3_000; run++)
        {
            var text = Encoding.UTF8.GetString(Mutate(manifest, random));
            try { ModuleManifest.Parse(text).Validate(); }
            catch (FormatException) { }
        }
    }
}
