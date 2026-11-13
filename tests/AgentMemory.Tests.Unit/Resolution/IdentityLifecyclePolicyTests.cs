using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>Typed policy slice of the lifecycle corpus. Native storage and retrieval have independent gates.</summary>
public sealed class IdentityLifecyclePolicyTests
{
    public static string CorpusPath([CallerFilePath] string? source = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source!)!, "../TestData/Identity/lifecycle-cases.json"));

    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CorpusPath()));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "identity-pair")
            .Select(item => new object[] { item.GetProperty("id").GetString()!, Configuration(item), "typed-pair", "policy-decision", item.GetRawText() }).ToArray();
    }

    /// <summary>The ILCS configuration a pair runs in: host evidence only where its fixtures declare keys or a kind.</summary>
    private static string Configuration(JsonElement item) =>
        new[] { "seed", "mention" }.Any(role => item.GetProperty(role).GetProperty("keys").EnumerateObject().Any() ||
            item.GetProperty(role).GetProperty("kind").ValueKind == JsonValueKind.String) ? "host-evidence" : "evidence-deterministic";

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("IdentityStage", "identity")]
    public void Identity_typed_policy_respects_independent_truth_and_host_authority(string id, string configuration, string variant, string check, string json)
    {
        _ = configuration; _ = variant; _ = check;
        using var document = JsonDocument.Parse(json); var item = document.RootElement;
        var seed = item.GetProperty("seed"); var mention = item.GetProperty("mention");
        var definition = item.GetProperty("policy");
        static string Text(JsonElement e, string key) => e.GetProperty(key).GetString()!;
        static IReadOnlyList<VerifiedIdentityKey> Keys(JsonElement e) => e.GetProperty("keys").EnumerateObject().Any()
            ? [new() { Namespace = "lifecycle-fixture", Authority = "synthetic-host", SourceId = Text(e, "sourceId"),
                Values = e.GetProperty("keys").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal) }] : [];
        static VerifiedIdentityKind? Kind(JsonElement e) => e.GetProperty("kind").ValueKind == JsonValueKind.String
            ? new() { Kind = Enum.Parse<IdentitySubjectKind>(Text(e, "kind")), Authority = "synthetic-host", SourceId = Text(e, "sourceId") } : null;
        static string? Subtype(JsonElement e) => e.GetProperty("subtype").GetString();
        var context = new EntityIdentityResolutionContext { VerifiedKeys = Keys(mention), VerifiedKind = Kind(mention),
            SourceMessageIds = [Text(mention,"sourceId")] };
        var policy = new EntityIdentityPolicy { IdentityProperties = definition.GetProperty("identityProperties").EnumerateArray().Select(p => p.GetString()!).ToArray(),
            CompatibleTypes = definition.GetProperty("compatibleTypes").EnumerateArray().Select(p => p.GetString()!).ToArray() };
        var observation = new ExtractedEntity { Name = Text(mention,"name"), Type = Text(mention,"type"), Subtype = Subtype(mention), Confidence = 1 };
        var candidate = new IdentityCandidate { Entity = new() { EntityId = Text(seed,"id"), Name = Text(seed,"name"),
                Type = Text(seed,"type"), Subtype = Subtype(seed), OwnerId = Text(item,"owner"), Confidence = 1,
                SourceMessageIds = [Text(seed,"sourceId")], CreatedAtUtc = DateTimeOffset.UnixEpoch },
            VerifiedKeys = Keys(seed), VerifiedKind = Kind(seed), Signals = new() { FuzzySimilarity = 1, SemanticSimilarity = 1, ContextSimilarity = 1 } };
        var result = IdentityPolicy.Evaluate(observation, [candidate], context, policy, MemoryScope.For(Text(item,"owner"), false));
        var expected = item.GetProperty("expected");
        result.Decision.Should().Be(Enum.Parse<IdentityDecisionKind>(Text(expected,"policyDecision")), id);
        result.ConfirmedEntityId.Should().Be(expected.GetProperty("confirmedTarget").GetString(), id);
        if (Text(item,"truth") == "different") result.ConfirmedEntityId.Should().BeNull(id);
        observation.Name.Should().Be(Text(mention,"name"), "policy cannot replace the original observation surface");
        observation.Subtype.Should().Be(Subtype(mention));
    }
}
