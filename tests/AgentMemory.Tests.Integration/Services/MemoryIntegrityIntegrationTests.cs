using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using AgentMemory.Abstractions.Services;
using FluentAssertions;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// G6 (PLAN 40.50): each integrity rule fails on a store where it is broken, by a planted defect, and passes on a clean
/// store; an owner's check sees only that owner's breaks.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class MemoryIntegrityIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    public MemoryIntegrityIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private IMemoryIntegrityService Sut()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        return new Neo4jMemoryIntegrityService(_fixture.TransactionRunner, clock);
    }

    private async Task WriteAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(cypher);
    }

    private const string CleanStore = @"
        CREATE (m:Message {id: 'm1'})
        CREATE (a:Entity {id: 'e-ana', owner_id: 'ana'}), (b:Entity {id: 'e-bilbao', owner_id: 'ana'}), (s:Entity {id: 'e-shared'})
        CREATE (a)-[:RELATED_TO {id: 'r1', owner_id: 'ana'}]->(b), (a)-[:RELATED_TO {id: 'r2', owner_id: 'ana'}]->(s)
        CREATE (old:Fact {id: 'f-old', owner_id: 'ana', source_message_ids: ['m1'], invalidated_at: datetime(), invalidated_reason: 'change',
                          valid_from: datetime('2010-01-01T00:00:00Z'), valid_until: datetime('2026-03-01T00:00:00Z')})
        CREATE (new:Fact {id: 'f-new', owner_id: 'ana', source_message_ids: ['m1'], valid_from: datetime('2026-03-01T00:00:00Z')})
        CREATE (old)-[:SUPERSEDED_BY]->(new), (new)-[:ABOUT]->(a)";

    [Fact]
    public async Task A_clean_store_passes_every_rule()
    {
        await WriteAsync(CleanStore);

        var report = await Sut().CheckAsync();

        report.Rules.Should().OnlyContain(rule => rule.Violations == 0, "the clean store breaks nothing");
        report.Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData("owner.edge-endpoints", "MATCH (f:Fact {id: 'f-new'}) CREATE (f)-[:ABOUT]->(:Entity {id: 'e-bob', owner_id: 'bob'})")]
    [InlineData("owner.edge-owner", "MATCH (a:Entity {id: 'e-ana'}), (b:Entity {id: 'e-bilbao'}) CREATE (a)-[:RELATED_TO {id: 'r-bob', owner_id: 'bob'}]->(b)")]
    [InlineData("supersession.closed-has-successor", "CREATE (:Fact {id: 'f-orphan', owner_id: 'ana', source_message_ids: ['m1'], invalidated_at: datetime(), invalidated_reason: 'correction'})")]
    [InlineData("supersession.live-has-no-successor", "MATCH (n:Fact {id: 'f-new'}) CREATE (:Fact {id: 'f-live', owner_id: 'ana', source_message_ids: ['m1']})-[:SUPERSEDED_BY]->(n)")]
    [InlineData("validity.window-ordered", "CREATE (:Fact {id: 'f-inverted', owner_id: 'ana', source_message_ids: ['m1'], valid_from: datetime('2026-01-01T00:00:00Z'), valid_until: datetime('2025-01-01T00:00:00Z')})")]
    public async Task A_planted_defect_fails_the_rule_that_names_it(string rule, string defect)
    {
        await WriteAsync(CleanStore);
        await WriteAsync(defect);

        var report = await Sut().CheckAsync();

        report.Rules.Single(r => r.Id == rule).Violations.Should().Be(1);
        report.Rules.Where(r => r.Id != rule).Should().OnlyContain(r => r.Violations == 0, "one defect breaks one rule");
        report.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task A_fact_with_no_source_is_a_warning_that_does_not_fail_the_store()
    {
        await WriteAsync(CleanStore);
        await WriteAsync("CREATE (:Fact {id: 'f-api', owner_id: 'ana'})");

        var report = await Sut().CheckAsync();

        var rule = report.Rules.Single(r => r.Id == "provenance.fact-has-source");
        (rule.Violations, rule.Severity, rule.Examples.Single()).Should().Be((1L, MemoryIntegrityRule.Warning, "f-api"));
        report.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task An_owners_check_sees_only_that_owners_breaks()
    {
        await WriteAsync(CleanStore);
        await WriteAsync("CREATE (:Fact {id: 'f-bob-orphan', owner_id: 'bob', source_message_ids: ['m1'], invalidated_at: datetime(), invalidated_reason: 'change'})");

        (await Sut().CheckAsync("ana")).Passed.Should().BeTrue();
        (await Sut().CheckAsync("bob")).Rules.Single(r => r.Id == "supersession.closed-has-successor").Examples.Should().Equal(["f-bob-orphan"]);
    }
}
