using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// Phase 1.1 wiring. The backfill queries existed for a commit without anything calling them — the
/// failure mode that shipped twice this session (the fused write path, and BuildSystemPrompt). This
/// asserts the call site and its ordering, not the capability.
/// </summary>
public sealed class CanonicalKeyBackfillWiringTests
{
    [Fact]
    public void BootstrapInvokesTheBackfill()
    {
        Source().Should().Contain("BackfillCanonicalFactKeysAsync(CanonicalKeyBackfillBatchSize");
    }

    [Fact]
    public void TheBackfillRunsBeforeBootstrapReportsCompletion()
    {
        // Ordering is the defect's whole substance: a fact written between upgrade and backfill
        // MERGEs onto a fresh node and duplicates regardless.
        var source = Source();
        var call = source.IndexOf("await BackfillCanonicalFactKeysAsync", StringComparison.Ordinal);
        var complete = source.IndexOf("Schema bootstrap complete.", StringComparison.Ordinal);

        call.Should().BeGreaterThan(0);
        call.Should().BeLessThan(complete, "the backfill must precede any repository write");
    }

    [Fact]
    public void TheBackfillIsBounded()
    {
        // An unbounded migration would attempt one transaction over an entire store.
        Source().Should().Contain("CanonicalKeyBackfillBatchSize = ");
    }

    [Fact]
    public void CanonicalFormsAreComputedInDotNetNotInCypher()
    {
        // toLower() and ToLowerInvariant() disagree on U+0130, so a Cypher-side computation would
        // write keys the write path never matches — silently reintroducing the duplication.
        var source = Source();
        var start = source.IndexOf("BackfillCanonicalFactKeysAsync", StringComparison.Ordinal);
        var body = source[start..];

        body.Should().Contain("MemoryTripleCanonicalizer.Canonical(");
        body.Should().Contain("MemoryTripleCanonicalizer.CanonicalValue(");
    }

    private static string Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        directory.Should().NotBeNull();
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "AgentMemory.Neo4j", "Infrastructure", "SchemaBootstrapper.cs"));
    }
}
