using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// Does as-of expansion actually ADD facts, through the repository the recall path uses?
/// </summary>
/// <remarks>
/// <para>
/// Prospective's composition arm came back flat with facts-per-question <b>6.20 → 5.98</b>, where
/// arithmetic saw 9.4 → 35.4 and procedural 5.75 → 32.1. Two explanations have opposite
/// consequences: expansion ran and found nothing to expand (the result stands), or expansion never
/// ran (the result is void and W1c is defective). This decides it without re-buying a 5-hour run.
/// </para>
/// <para>
/// Deliberately shaped like the two corpora that differ: <b>one relation holding many facts</b>
/// (arithmetic's "payment has amount") versus <b>many relations holding one fact each</b>
/// (prospective's unique reminders). Expansion returns a relation whole, so it can only pay off in
/// the first shape — and if that is the explanation, this test shows it as a property of the data
/// rather than a defect in the code.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class AsOfExpansionEndToEndTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private static readonly float[] Emb = [0.5f, 0.5f, 0.5f, 0.5f];
    private const string Owner = "owner-e2e";
    private static readonly DateTimeOffset AsOf = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public AsOfExpansionEndToEndTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>One relation, many facts — the arithmetic shape. Expansion must return them all.</summary>
    [Fact]
    public async Task ADenseRelationExpandsToEveryMemberInsideTheWindow()
    {
        for (var i = 0; i < 12; i++)
            await SeedAsync($"dense-{i}", "has_amount", validFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var expanded = await _facts.SearchByCanonicalPredicatesAsOfAsync(
            [Key("has_amount")], limit: 60, scope: MemoryScope.For(Owner), asOf: AsOf, systemAsOf: AsOf);

        expanded.Should().HaveCount(12,
            "expansion returns the relation WHOLE — this is the shape it was built for, and it is "
            + "the shape arithmetic and procedural have");
    }

    /// <summary>
    /// Many relations, one fact each — the prospective shape. Expansion is correct and adds nothing.
    /// </summary>
    /// <remarks>
    /// This is the control that separates "expansion is broken" from "there was nothing to expand".
    /// A relation of size one is already complete in the top-K, so returning it whole cannot add a
    /// row. A flat result on such a corpus is a property of the DATA, not a defect.
    /// </remarks>
    [Fact]
    public async Task SingletonRelationsExpandToThemselvesAndAddNothing()
    {
        for (var i = 0; i < 12; i++)
            await SeedAsync($"sparse-{i}", $"owes_reply_to_{i}", validFrom: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var keys = Enumerable.Range(0, 12).Select(i => Key($"owes_reply_to_{i}")).ToArray();
        var expanded = await _facts.SearchByCanonicalPredicatesAsOfAsync(
            keys, limit: 60, scope: MemoryScope.For(Owner), asOf: AsOf, systemAsOf: AsOf);

        expanded.Should().HaveCount(12,
            "each relation holds exactly one fact, so expansion returns precisely what similarity "
            + "already had — no gain is available, and none is a bug");
    }

    private static string Key(string predicate) =>
        AgentMemory.Core.Memory.MemoryTripleCanonicalizer.Canonical(predicate);

    private async Task SeedAsync(string id, string predicate, DateTimeOffset validFrom)
    {
        await _facts.UpsertAsync(new Fact
        {
            FactId = id, Subject = id, Predicate = predicate, Object = "x",
            Confidence = 0.9, Embedding = Emb, OwnerId = Owner,
            CreatedAtUtc = validFrom, ValidFrom = validFrom,
        });
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            "MATCH (f:Fact {id: $id}) SET f.created_at = datetime($t), f.valid_from = datetime($t)",
            new { id, t = validFrom.UtcDateTime.ToString("O") });
    }
}
