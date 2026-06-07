using FluentAssertions;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Tests.Unit.Infrastructure;

public sealed class StoreDatabaseNamingTests
{
    // ── Resolve ──

    [Fact]
    public void Resolve_SharedDatabase_AlwaysReturnsDefault_EvenWithApplicationId()
    {
        var opts = new MemoryStoreOptions { Strategy = MemoryStorageStrategy.SharedDatabase };

        StoreDatabaseNaming.Resolve(opts, "neo4j", "acme").Should().Be("neo4j");
        StoreDatabaseNaming.Resolve(opts, "neo4j", null).Should().Be("neo4j");
    }

    [Fact]
    public void Resolve_BlankDefaultDatabase_InheritsFallback()
    {
        // DefaultDatabase = "" means "inherit Neo4jOptions.Database" (backward compatibility).
        var opts = new MemoryStoreOptions { DefaultDatabase = "" };

        StoreDatabaseNaming.Resolve(opts, "customdb", null).Should().Be("customdb");
    }

    [Fact]
    public void Resolve_ExplicitDefaultDatabase_OverridesFallback()
    {
        var opts = new MemoryStoreOptions { DefaultDatabase = "memories" };

        StoreDatabaseNaming.Resolve(opts, "neo4j", null).Should().Be("memories");
    }

    [Fact]
    public void Resolve_PerApplication_NullApplicationId_ReturnsDefault()
    {
        var opts = new MemoryStoreOptions { Strategy = MemoryStorageStrategy.DatabasePerApplication };

        StoreDatabaseNaming.Resolve(opts, "neo4j", null).Should().Be("neo4j");
        StoreDatabaseNaming.Resolve(opts, "neo4j", "   ").Should().Be("neo4j");
    }

    [Fact]
    public void Resolve_PerApplication_ConcreteId_PrefixesAndSanitizes()
    {
        var opts = new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            DatabasePrefix = "mem-"
        };

        StoreDatabaseNaming.Resolve(opts, "neo4j", "Acme").Should().Be("mem-acme");
        StoreDatabaseNaming.Resolve(opts, "neo4j", "Acme Corp / EU").Should().Be("mem-acme-corp-eu");
    }

    [Fact]
    public void Resolve_PerApplication_ResultIsAValidNeo4jDatabaseName()
    {
        var opts = new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            DatabasePrefix = "mem-"
        };

        var name = StoreDatabaseNaming.Resolve(opts, "neo4j", "Tenant_42!!");

        name.Should().MatchRegex("^[a-z][a-z0-9.-]{2,62}$");
        name.Should().NotContain("_");
        name.Should().NotEndWith(".");
    }

    [Fact]
    public void Resolve_PerApplication_LongId_TruncatesWithDeterministicHash_NoCollision()
    {
        var opts = new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            DatabasePrefix = "mem-"
        };
        var longA = new string('a', 200) + "-one";
        var longB = new string('a', 200) + "-two";

        var nameA = StoreDatabaseNaming.Resolve(opts, "neo4j", longA);
        var nameB = StoreDatabaseNaming.Resolve(opts, "neo4j", longB);

        nameA.Length.Should().BeLessThanOrEqualTo(63);
        nameB.Length.Should().BeLessThanOrEqualTo(63);
        nameA.Should().NotBe(nameB);                                  // distinct ids never collide
        nameA.Should().Be(StoreDatabaseNaming.Resolve(opts, "neo4j", longA)); // deterministic
    }

    // ── Sanitize ──

    [Theory]
    [InlineData("Acme", "acme")]
    [InlineData("acme_corp", "acme-corp")]
    [InlineData("a.b-c", "a.b-c")]
    [InlineData("__leading", "leading")]
    [InlineData("trailing__", "trailing")]
    [InlineData("user@example.com", "user-example.com")]
    public void Sanitize_MapsToLegalCharset(string input, string expected)
    {
        StoreDatabaseNaming.Sanitize(input).Should().Be(expected);
    }

    // ── Edge cases: ids that sanitize to nothing must not collide (rank #6 fix) ──

    [Theory]
    [InlineData("!@#$")]
    [InlineData(";;;;")]
    [InlineData("🎉")]
    [InlineData("...")]
    [InlineData("---")]
    public void Resolve_PerApplication_IdThatSanitizesToEmpty_ProducesValidName(string appId)
    {
        var opts = new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            DatabasePrefix = "mem-"
        };

        var name = StoreDatabaseNaming.Resolve(opts, "neo4j", appId);

        name.Should().MatchRegex("^[a-z][a-z0-9.-]{2,62}$");
        name.Should().NotBe("mem-", "an empty-sanitizing id must not collapse onto the bare prefix");
    }

    [Fact]
    public void Resolve_PerApplication_DistinctEmptySanitizingIds_DoNotCollide()
    {
        var opts = new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication,
            DatabasePrefix = "mem-"
        };

        var a = StoreDatabaseNaming.Resolve(opts, "neo4j", "!@#");
        var b = StoreDatabaseNaming.Resolve(opts, "neo4j", ";;;;");
        var c = StoreDatabaseNaming.Resolve(opts, "neo4j", "🎉");

        new[] { a, b, c }.Distinct().Should().HaveCount(3, "distinct ids must map to distinct databases");
        // deterministic
        a.Should().Be(StoreDatabaseNaming.Resolve(opts, "neo4j", "!@#"));
    }
}
