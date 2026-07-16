using System.Text.Json;
using FluentAssertions;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Tests.Unit.Domain;

public sealed class MemoryTrustMetadataExtensionsTests
{
    [Fact]
    public void GetTrustLevel_NoKeyPresent_ReturnsUntrusted()
    {
        var metadata = new Dictionary<string, object>();

        metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.Untrusted);
    }

    [Fact]
    public void WithTrustLevel_ThenGetTrustLevel_RoundTripsInProcess()
    {
        var metadata = new Dictionary<string, object> { ["other_key"] = "unrelated" };

        var updated = metadata.WithTrustLevel(MemoryTrustLevel.ApplicationTrusted);

        updated.GetTrustLevel().Should().Be(MemoryTrustLevel.ApplicationTrusted);
        updated.Should().ContainKey("other_key").WhoseValue.Should().Be("unrelated");
    }

    [Fact]
    public void WithTrustLevel_DoesNotMutateTheOriginalDictionary()
    {
        var original = new Dictionary<string, object> { ["a"] = 1 };

        var updated = original.WithTrustLevel(MemoryTrustLevel.VerifiedExternal);

        original.Should().NotContainKey("trust_level");
        updated.Should().ContainKey("trust_level");
    }

    [Theory]
    [InlineData(MemoryTrustLevel.Untrusted)]
    [InlineData(MemoryTrustLevel.UserProvided)]
    [InlineData(MemoryTrustLevel.ModelGenerated)]
    [InlineData(MemoryTrustLevel.ToolDerived)]
    [InlineData(MemoryTrustLevel.VerifiedExternal)]
    [InlineData(MemoryTrustLevel.ApplicationTrusted)]
    public void GetTrustLevel_AfterJsonRoundTrip_ReadsBackCorrectly(MemoryTrustLevel trustLevel)
    {
        // Simulates what Neo4jRecordMapper.SerializeMetadata/DeserializeMetadata actually does: Metadata is
        // persisted as one serialized JSON string property and deserialized into Dictionary<string, object>,
        // where System.Text.Json produces a boxed JsonElement for each value, not the original CLR type.
        var stamped = new Dictionary<string, object>().WithTrustLevel(trustLevel);
        var json = JsonSerializer.Serialize(stamped);
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        roundTripped["trust_level"].Should().BeOfType<JsonElement>();
        roundTripped.GetTrustLevel().Should().Be(trustLevel);
    }

    [Fact]
    public void GetTrustLevel_UnparseableValue_ReturnsUntrusted()
    {
        var metadata = new Dictionary<string, object> { ["trust_level"] = "not-a-real-level" };

        metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.Untrusted);
    }

    [Fact]
    public void GetTrustLevel_NonStringJsonElementValue_ReturnsUntrusted()
    {
        var json = """{"trust_level": 42}""";
        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.Untrusted);
    }

    [Fact]
    public void TrustLevels_AreOrderedForMinimumThresholdComparisons()
    {
        // Consumers (e.g. the admission policy's MinimumTrustForAdmissionBypass gate) rely on ">=" ordering.
        ((int)MemoryTrustLevel.Untrusted).Should().BeLessThan((int)MemoryTrustLevel.UserProvided);
        ((int)MemoryTrustLevel.UserProvided).Should().BeLessThan((int)MemoryTrustLevel.ModelGenerated);
        ((int)MemoryTrustLevel.ModelGenerated).Should().BeLessThan((int)MemoryTrustLevel.ToolDerived);
        ((int)MemoryTrustLevel.ToolDerived).Should().BeLessThan((int)MemoryTrustLevel.VerifiedExternal);
        ((int)MemoryTrustLevel.VerifiedExternal).Should().BeLessThan((int)MemoryTrustLevel.ApplicationTrusted);
    }

    // ── CreateWithTrustLevel ─────────────────────────────────────────────────

    [Fact]
    public void CreateWithTrustLevel_ReturnsDictionaryContainingOnlyTheTrustLevel()
    {
        var metadata = MemoryTrustMetadataExtensions.CreateWithTrustLevel(MemoryTrustLevel.VerifiedExternal);

        metadata.GetTrustLevel().Should().Be(MemoryTrustLevel.VerifiedExternal);
        metadata.Should().HaveCount(1);
    }

    // ── WithoutCallerSuppliedTrustLevel (#92 Phase 3 security fix) ───────────

    [Fact]
    public void WithoutCallerSuppliedTrustLevel_RemovesTheKey()
    {
        var metadata = new Dictionary<string, object> { ["trust_level"] = "ApplicationTrusted", ["source"] = "crm" };

        var sanitized = metadata.WithoutCallerSuppliedTrustLevel();

        sanitized.GetTrustLevel().Should().Be(MemoryTrustLevel.Untrusted);
        sanitized.Should().ContainKey("source");
    }

    [Fact]
    public void WithoutCallerSuppliedTrustLevel_NoTrustLevelPresent_ReturnsEquivalentMetadata()
    {
        var metadata = new Dictionary<string, object> { ["source"] = "crm" };

        var sanitized = metadata.WithoutCallerSuppliedTrustLevel();

        sanitized.Should().ContainKey("source");
        sanitized.Should().NotContainKey("trust_level");
    }

    [Fact]
    public void WithoutCallerSuppliedTrustLevel_DoesNotMutateTheOriginalDictionary()
    {
        var original = new Dictionary<string, object> { ["trust_level"] = "ApplicationTrusted" };

        original.WithoutCallerSuppliedTrustLevel();

        original.Should().ContainKey("trust_level", "the sanitizer must return a new dictionary, never mutate the caller's");
    }

    [Fact]
    public void SanitizeThenStamp_CallerCannotOverrideTheFrameworkAssignedTrustLevel()
    {
        // The exact sequence CoreMemoryTools.MemoryAddFact and ReasoningMemoryService.StartTraceAsync use:
        // strip whatever the caller supplied, then the framework stamps its own value.
        var callerSupplied = new Dictionary<string, object> { ["trust_level"] = "ApplicationTrusted" };

        var result = callerSupplied.WithoutCallerSuppliedTrustLevel().WithTrustLevel(MemoryTrustLevel.ToolDerived);

        result.GetTrustLevel().Should().Be(MemoryTrustLevel.ToolDerived);
    }
}
