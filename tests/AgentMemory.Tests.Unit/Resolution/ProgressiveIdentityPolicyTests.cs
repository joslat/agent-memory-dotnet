using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>The first five source-controlled Pablito-inspired policy oracles; event-retrieval cases have their own gate.</summary>
public sealed class ProgressiveIdentityPolicyTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CorpusPath()));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("level").GetInt32() <= 4)
            .Select(item => new object[] { item.GetProperty("id").GetString()!, item.GetRawText() }).ToArray();
    }
    private static string CorpusPath([CallerFilePath] string? source = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source!)!, "../TestData/Identity/progressive-cases.json"));
    [Theory]
    [MemberData(nameof(Cases))]
    public void Policy_oracle_preserves_identity_boundaries(string id, string json)
    {
        using var document = JsonDocument.Parse(json); var item = document.RootElement;
        var mention = item.GetProperty("mention"); var seed = item.GetProperty("seed");
        static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;
        static VerifiedIdentityKind? Kind(JsonElement element) => element.TryGetProperty("verifiedKind", out var kind)
            ? new() { Kind = Enum.Parse<IdentitySubjectKind>(kind.GetString()!), Authority = "synthetic-fixture", SourceId = "fixture:kind" } : null;
        static IReadOnlyList<VerifiedIdentityKey> Keys(JsonElement element) => element.TryGetProperty("key", out var key)
            ? [new() { Namespace = Text(key, "namespace"), Authority = "synthetic-fixture", SourceId = "fixture:key",
                Values = key.GetProperty("values").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal) }]
            : [];
        var sources = item.GetProperty("sources").EnumerateArray().Select(source => new IdentitySourceFragment
        { SourceId = Text(source, "id"), Text = Text(source, "text"), OriginIds = [Text(source, "id")] }).ToArray();
        var context = new EntityIdentityResolutionContext
        { VerifiedKind = Kind(mention), VerifiedKeys = Keys(mention), SourceMessageIds = mention.GetProperty("sources").EnumerateArray().Select(source => source.GetString()!).ToArray(), Sources = sources };
        var policy = item.TryGetProperty("policy", out var definition) ? new EntityIdentityPolicy
        {
            IdentityProperties = definition.TryGetProperty("identityProperties", out var properties) ? properties.EnumerateArray().Select(property => property.GetString()!).ToArray() : [],
            AllowContextualConfirmation = definition.TryGetProperty("allowContextualConfirmation", out var allow) && allow.GetBoolean()
        } : new();
        var observation = new ExtractedEntity { Name = Text(mention, "name"), Type = Text(mention, "type"), Confidence = 1 };
        var candidate = new IdentityCandidate
        {
            Entity = new() { EntityId = Text(seed, "id"), Name = Text(seed, "name"), Type = Text(seed, "type"), OwnerId = "pablito", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch },
            VerifiedKind = Kind(seed), VerifiedKeys = Keys(seed), Signals = new() { FuzzySimilarity = 1, SemanticSimilarity = 1 }
        };
        var result = IdentityPolicy.Evaluate(observation, [candidate], context, policy, MemoryScope.For("pablito", false));
        var expected = item.GetProperty("expected");
        result.Decision.Should().Be(Enum.Parse<IdentityDecisionKind>(Text(expected, "decision")), id);
        if (expected.TryGetProperty("target", out var target)) result.ConfirmedEntityId.Should().Be(target.GetString(), id);
        if (expected.TryGetProperty("differentFrom", out var other)) result.ConfirmedEntityId.Should().NotBe(other.GetString(), id);
        if (expected.TryGetProperty("reason", out var reason)) result.Reasons.Values.Should().Contain(reason.GetString()!, id);
        var literal = IdentityPolicy.IsCalendarLiteral(observation, context, policy, out var calendar);
        if (expected.TryGetProperty("calendarLiteral", out var isLiteral)) literal.Should().Be(isLiteral.GetBoolean(), id);
        if (expected.TryGetProperty("calendarKey", out var calendarKey))
            (calendar?.IsComplete == true ? calendar.CanonicalKey : null).Should().Be(calendarKey.ValueKind == JsonValueKind.Null ? null : calendarKey.GetString(), id);
        // Identity policy must not mutate extracted words to the candidate's spelling.
        observation.Name.Should().Be(Text(mention, "name"), id);
    }
}
