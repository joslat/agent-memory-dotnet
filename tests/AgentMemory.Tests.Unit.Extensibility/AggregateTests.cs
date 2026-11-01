#pragma warning disable AMEXT001
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Feature 13: a host's trial aggregate (R-CORE-140).</summary>
public sealed class AggregateTests
{
    [Fact]
    public void An_aggregate_carries_counts_and_verdicts_and_no_text_and_noise_hides_the_counts()
    {
        var publisher = PackAuthoring.CreateKey();
        var evaluator = PackAuthoring.CreateKey();
        var module = """{"format":"agentmemory.module/v1alpha1","id":"com.example.notes","version":"1.0.0","sdk":"[0.1,0.2)","seed":["records/notes.jsonl"],"trial":{"measure":"retrieval","tasks":10,"targets":{"hitRateMicro":500000}}}""";
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(module),
            ["records/notes.jsonl"] = Encoding.UTF8.GetBytes("""{"type":"Note","key":"lyon-review","properties":{"text":"The Lyon review is on Friday"}}""" + "\n"),
        }), StringComparer.Ordinal)
        {
            ["ontology.json"] = Encoding.UTF8.GetBytes("""{"labelPrefix":"Note","types":[{"name":"Note","scope":"shared","properties":{"text":{"type":"string"}}}]}"""),
        };
        var policy = JsonNode.Parse(PackAuthoring.TrustPolicy(publisher.PublicKeyPem, "com.example"))!;
        var asEvaluator = JsonNode.Parse(PackAuthoring.TrustPolicy(evaluator.PublicKeyPem, "com.example"))!["keys"]![0]!.DeepClone();
        asEvaluator["roles"] = new JsonArray("evaluator");
        policy["keys"]!.AsArray().Add(asEvaluator);
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(policy.ToJsonString()));
        var pack = PackLoader.Load(PackAuthoring.Build(source, publisher.PrivateKeyPem), trust);
        var arm = new PackTrialMeasurement { Tasks = 10, Hits = 7, Offered = 9, Correct = 7, P95Ms = 3, PerTask = [.. Enumerable.Range(0, 10).Select(i => new PackTrialTask(i < 9 ? 1 : 0, i < 7 ? 1 : 0))] };
        var none = new PackTrialMeasurement { Tasks = 10, Hits = 0, Offered = 0, Correct = 0, P95Ms = 1, PerTask = [.. Enumerable.Range(0, 10).Select(_ => new PackTrialTask(0, 0))] };
        var decision = PackTrialGate.Decide(pack.Manifest, arm, none, DateTimeOffset.UtcNow, pack.Digest) with { Reason = "a reason that names src/secret/Plan.cs" };

        var exact = PackAggregates.Create(pack, decision, evaluator.PrivateKeyPem);
        var (read, problems) = PackAggregate.Verify(exact, pack.Digest, trust.Policy, DateTimeOffset.UtcNow);
        problems.Should().BeEmpty();
        read!.Checks.Should().ContainSingle().Which.Should().Be(new AggregateCheck("hitRate", 7, 10, 500000, read.Checks[0].BoundMicro, true));
        read.Mechanism.Should().Be("none");
        var payload = Encoding.UTF8.GetString(Dsse.FromBase64(JsonNode.Parse(exact)!["payload"]!.GetValue<string>())!);
        payload.Should().NotContain("secret").And.NotContain("Lyon").And.NotContain("lyon-review", "an aggregate copies no text from the decision or the pack");

        var noisy = PackAggregate.Verify(PackAggregates.Create(pack, decision, evaluator.PrivateKeyPem, epsilonMilli: 500), pack.Digest, trust.Policy, DateTimeOffset.UtcNow).Aggregate!;
        noisy.Mechanism.Should().Be("discrete-laplace");
        noisy.Checks[0].BoundMicro.Should().BeNull("noise makes a bound approximate, so none is stated");
        noisy.Checks[0].Successes.Should().BeGreaterThanOrEqualTo(0);

        PackAggregate.Verify(PackAggregates.Create(pack, decision, publisher.PrivateKeyPem), pack.Digest, trust.Policy, DateTimeOffset.UtcNow)
            .Aggregate.Should().BeNull("a publisher key is not trusted as evaluator");
    }
}
