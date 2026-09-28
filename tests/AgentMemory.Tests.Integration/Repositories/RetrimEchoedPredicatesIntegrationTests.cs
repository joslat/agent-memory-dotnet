using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// J-8a, on a real store: facts written before predicate-echo trimming ("Daniel | is a chef | chef", rendered "is a chef
/// chef") are repaired on demand: rewritten as they read once trimmed, or superseded by the trimmed fact when it is
/// already stored. The repository writes triples as given, so it stores them the way the old write path did.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class RetrimEchoedPredicatesIntegrationTests : IAsyncLifetime
{
    private const string Owner = "retrim-probe";
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    public RetrimEchoedPredicatesIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Fact> Store(string id, string subject, string predicate, string @object) =>
        _facts.UpsertAsync(new Fact
        {
            FactId = id, Subject = subject, Predicate = predicate, Object = @object, Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = Owner,
        });

    private SchemaBootstrapper Bootstrapper() => new(_fixture.TransactionRunner,
        Options.Create(new Neo4jOptions { EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions }),
        NullLogger<SchemaBootstrapper>.Instance);

    [Fact]
    public async Task Echoed_facts_are_rewritten_or_superseded_and_the_rest_is_left_alone()
    {
        await Store("chef", "Daniel", "is a chef", "chef");
        await Store("chimney-old", "Bill", "is to go down the chimney", "the chimney");
        await Store("chimney-new", "Bill", "is to go down", "the chimney");
        await Store("acme", "Anna", "works at", "Acme");
        var scope = MemoryScope.For(Owner, includeShared: false);

        (await Bootstrapper().RetrimEchoedPredicatesAsync(apply: false)).Should().Be(2, "a dry run counts");
        (await _facts.FindByTripleAsync("Daniel", "is a chef", "chef", scope)).Should().NotBeNull("a dry run writes nothing");

        (await Bootstrapper().RetrimEchoedPredicatesAsync(apply: true)).Should().Be(2);

        var chef = await _facts.GetByIdAsync("chef");
        (chef!.Predicate, chef.Object).Should().Be(("is", "a chef"), "rewritten as it reads, the article with the object");
        (await _facts.FindByTripleAsync("Daniel", "is", "a chef", scope))!.FactId.Should().Be("chef", "its keys moved too");
        (await _facts.GetByIdAsync("chimney-old"))!.InvalidatedAtUtc.Should().NotBeNull("the trimmed fact already existed");
        (await _facts.GetByIdAsync("chimney-new"))!.InvalidatedAtUtc.Should().BeNull();
        var acme = await _facts.GetByIdAsync("acme");
        (acme!.Predicate, acme.Object, acme.InvalidatedAtUtc).Should().Be(("works at", "Acme", (DateTimeOffset?)null));

        (await Bootstrapper().RetrimEchoedPredicatesAsync(apply: true)).Should().Be(0, "nothing is left to repair");
    }
}
