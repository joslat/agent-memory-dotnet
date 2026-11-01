using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;

namespace Ampack;

/// <summary>
/// Trial aggregates (feature 13): <c>ampack aggregate create</c> builds and signs one from the host's decision for a pack;
/// <c>ampack aggregate verify</c> checks one against the pack, by a key the trust policy holds as evaluator for its id.
/// </summary>
internal static partial class Program
{
    private static int AggregateCommand(Options o) => o.Positional(0, "aggregate action (create, verify)") switch
    {
        "create" => AggregateCreate(o),
        "verify" => AggregateVerify(o),
        var other => throw new UsageException($"Unknown aggregate action '{other}': create, verify."),
    };

    // ampack aggregate verify <pack.ampack> --aggregate <file> --policy <trust.json>
    private static int AggregateVerify(Options o)
    {
        var content = PackReader.Read(o.Positional(1, "pack.ampack"));
        var policy = Policy(o);
        var verification = PackVerifier.VerifyWhole(content, policy, DateTimeOffset.UtcNow);
        if (verification.Pack is not { } pack) return Report(verification.Problems, "");
        var file = o.Value("aggregate") ?? throw new UsageException("aggregate verify needs --aggregate <file>.");
        var (aggregate, problems) = PackAggregate.Verify(PackReader.ReadFile(file, Dsse.MaximumEnvelopeBytes, "The aggregate"), pack.Id, pack.Version, pack.Digest, policy, DateTimeOffset.UtcNow);
        if (aggregate is null) return Report(problems, "");
        Console.WriteLine($"Verified the aggregate of {pack.Id} {aggregate.Version}: {aggregate.Tasks} tasks, {aggregate.Mechanism}, {(aggregate.Passed ? "passed" : "not passed")}.");
        return 0;
    }

    // ampack aggregate create <pack.ampack> --policy <trust.json> --activations <folder> --host-key <host.pub.pem> --key <signer.pem> --out <file>
    private static int AggregateCreate(Options o)
    {
        var loaded = PackLoader.Load(o.Positional(1, "pack.ampack"), Trust(o));
        var folder = o.Value("activations") ?? throw new UsageException("aggregate create needs --activations <folder>, the host's decisions.");
        // The host's public key reads its decisions; the signing key (--key) is one the reader trusts as evaluator.
        var decision = new PackActivationFolder(folder, Text(o.Required("host-key"), "The host key")).Find(loaded.Id, loaded.Manifest.Version);
        var pem = Text(o.Required("key"), "The signing key");
        if (decision is null || decision.PackDigest != loaded.Digest) return Report([$"This host holds no decision for {loaded.Id} {loaded.Version} ({loaded.Digest})."], "");
        var output = o.Value("out") ?? throw new UsageException("aggregate create needs --out <file>.");
        File.WriteAllBytes(output, PackAggregates.Create(loaded, decision, pem));
        Console.WriteLine($"Aggregate of {loaded.Id} {loaded.Version}: exact counts, shared with your consent\n  wrote   {output}");
        return 0;
    }
}
