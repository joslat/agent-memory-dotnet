using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class IdentityOwnerCompatibilityTests
{
    private sealed class Fixture
    {
        public readonly INeo4jTransactionRunner Tx = Substitute.For<INeo4jTransactionRunner>();
        public readonly IEmbeddingOrchestrator Embeddings = Substitute.For<IEmbeddingOrchestrator>();
        public readonly List<string> Queries = [];
        public readonly IEntityRepository Entities = Substitute.For<IEntityRepository>();
        public readonly IFactRepository Facts = Substitute.For<IFactRepository>();
        public readonly IRelationshipRepository Relationships = Substitute.For<IRelationshipRepository>();
        public readonly List<Entity> EntityWrites = [];
        public readonly List<Relationship> RelationshipWrites = [];
        public readonly Neo4jMemoryOwnerDataService Service;
        public Fixture(bool enabled)
        {
            var options = new Neo4jOptions(); if (enabled) options.Extensions.Add("identity-evidence");
            var ids = Substitute.For<IIdGenerator>(); var next = 0;
            ids.GenerateId().Returns(_ => $"new-{++next}");
            Entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(c => { EntityWrites.Add(c.Arg<Entity>()); return c.Arg<Entity>(); });
            Facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(c => c.Arg<Fact>());
            Relationships.UpsertAsync(Arg.Any<Relationship>(), Arg.Any<CancellationToken>())
                .Returns(c => { RelationshipWrites.Add(c.Arg<Relationship>()); return c.Arg<Relationship>(); });
            Service = new(Tx, Entities, Facts, Substitute.For<IPreferenceRepository>(), Relationships, Embeddings,
                ids, TimeProvider.System, Options.Create(options));
            IAsyncQueryRunner Runner()
            {
                var runner = Substitute.For<IAsyncQueryRunner>();
                IResultCursor Read(string query)
                {
                    Queries.Add(query); var cursor = Substitute.For<IResultCursor>();
                    cursor.FetchAsync().Returns(false);
                    cursor.Current.Returns(Substitute.For<IRecord>()); return cursor;
                }
                runner.RunAsync(Arg.Any<string>(), Arg.Any<object>()).Returns(c => Read(c.Arg<string>()));
                runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>()).Returns(c => Read(c.Arg<string>()));
                return runner;
            }
            Tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<OwnerMemoryExport>>>(), Arg.Any<CancellationToken>())
                .Returns(c => c.Arg<Func<IAsyncQueryRunner, Task<OwnerMemoryExport>>>()(Runner()));
            Tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
                .Returns(c => c.Arg<Func<IAsyncQueryRunner, Task>>()(Runner()));
        }
    }

    private static Entity Entity(string id, string name) =>
        new() { EntityId = id, Name = name, Type = "EVENT", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };

    /// <summary>
    /// ILC-26: with identity on by default, import must work into an identity store. Each mention's own entity is folded
    /// into the identity it was confirmed as, so a confirmed identity comes back as one entity carrying the mention's
    /// name as an alias, and what pointed at the mention points at the identity.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_identity_export_imports_each_confirmed_identity_as_one_entity(bool destinationHasIdentity)
    {
        var f = new Fixture(destinationHasIdentity);
        var fact = new Fact { FactId = "f1", Subject = "the cutover", Predicate = "is on", Object = "December 12", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };
        var export = new OwnerMemoryExport
        {
            OwnerId = "source", ExportedAtUtc = DateTimeOffset.UnixEpoch, IdentityStateOmitted = true,
            Entities = [Entity("golive", "Acme go-live"), Entity("cutover", "the cutover"), Entity("bariloche", "Bariloche")],
            Facts = [fact],
            About = [new("f1", "cutover")],
            Relationships = [new() { RelationshipId = "r1", SourceEntityId = "cutover", TargetEntityId = "bariloche", RelationshipType = "LOCATED_IN", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch }],
            IdentityResolutions = [new("cutover", "golive")],
        };

        var result = await f.Service.ImportAsync(export, "owner");

        result.Written["entities"].Should().Be(2, "the cutover was confirmed as the go-live");
        result.Ids["cutover"].Should().Be(result.Ids["golive"]);
        f.EntityWrites.Single(e => e.Name == "Acme go-live").Aliases.Should().Contain("the cutover");
        f.RelationshipWrites.Single().SourceEntityId.Should().Be(result.Ids["golive"]);
        await f.Facts.Received(1).CreateAboutRelationshipAsync(result.Ids["f1"], result.Ids["golive"], Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_identity_store_imports_a_plain_export_without_joining_it_to_what_is_stored()
    {
        var f = new Fixture(true);
        var result = await f.Service.ImportAsync(new()
        {
            OwnerId = "source", ExportedAtUtc = DateTimeOffset.UnixEpoch, Entities = [Entity("a", "Alex Kim"), Entity("b", "Alex Kim")],
        }, "owner");
        result.Written["entities"].Should().Be(2, "two entities with one name stay two: import merges nothing");
        f.EntityWrites.Select(e => e.EntityId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Disabled_identity_keeps_the_six_legacy_export_queries_and_empty_import()
    {
        var f = new Fixture(false);
        var export = await f.Service.ExportAsync("owner");
        export.Format.Should().Be(OwnerMemoryExport.CurrentFormat); export.Facts.Should().BeEmpty();
        f.Queries.Count.Should().Be(6, "disabled identity adds no query");
        (await f.Service.ImportAsync(export, "new-owner")).Written.Should().BeEmpty();
        f.Queries.Count.Should().Be(6);
    }

    [Fact]
    public async Task Existing_identity_exports_for_reading_with_its_resolutions_and_an_omission_flag()
    {
        // ILC-18 interim: an owner's memory stays readable with identity enabled; the export says what it leaves out.
        var f = new Fixture(true);
        var export = await f.Service.ExportAsync("owner");
        export.IdentityStateOmitted.Should().BeTrue();
        export.IdentityResolutions.Should().NotBeNull();
        f.Queries.Count.Should().Be(7, "the six memory queries plus one identity-resolution read");
        f.Embeddings.ReceivedCalls().Should().BeEmpty();
    }


    [Fact]
    public async Task Disabled_identity_export_serializes_without_any_identity_field()
    {
        var f = new Fixture(false);
        var json = System.Text.Json.JsonSerializer.Serialize(await f.Service.ExportAsync("owner"));
        json.Should().NotContain("IdentityResolutions").And.NotContain("IdentityStateOmitted");
    }

    [Fact]
    public async Task Cancellation_and_unknown_formats_fail_without_any_portability_query()
    {
        var f = new Fixture(true);
        var cancelled = () => f.Service.ImportAsync(new() { OwnerId = "source", ExportedAtUtc = DateTimeOffset.UnixEpoch }, "owner", new CancellationToken(true));
        var unknown = () => f.Service.ImportAsync(new() { Format = "unsupported/2", OwnerId = "source", ExportedAtUtc = DateTimeOffset.UnixEpoch }, "owner");
        await cancelled.Should().ThrowAsync<OperationCanceledException>(); await unknown.Should().ThrowAsync<ArgumentException>();
        f.Queries.Should().BeEmpty();
    }
}
