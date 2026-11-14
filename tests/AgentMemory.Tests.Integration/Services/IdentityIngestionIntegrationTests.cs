using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Extraction;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Services;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class IdentityIngestionIntegrationTests(Neo4jIntegrationFixture fixture) : IAsyncLifetime
{
    private static readonly MemoryScope Scope = MemoryScope.For("pablito", false);
    public async Task InitializeAsync()
    {
        await fixture.CleanDatabaseAsync();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Schema/Migrations/ext/identity-evidence"), "*.cypher").Order())
            foreach (var statement in (await File.ReadAllTextAsync(file)).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(statement); });
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync("CREATE (c:Conversation {id:'conversation', user_id:'pablito'}), (m:Message {id:'source', conversation_id:'conversation', content:'The Acme go live is on December 12, 2026', session_id:'scenario'}), (c)-[:HAS_MESSAGE]->(m)"); });
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider Host(bool enabled = true, bool extension = true, IIdentityContextProvider? hostContext = null, EntityIdentityPolicy? identityPolicy = null, IIdentityAdjudicator? adjudicator = null)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<INeo4jTransactionRunner>(fixture.TransactionRunner);
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0, 0, 0 });
        services.AddSingleton(embeddings);
        services.AddAgentMemoryCore(_ => { });
        services.Configure<MemoryOptions>(options => options.WorkingMemory.Enabled = false);
        services.Configure<ExtractionOptions>(options => { options.CanonicalFactSubjects = true; options.LinkFactsToEntities = true; options.DeduplicateWithinExtraction = true; });
        services.Configure<IdentityResolutionOptions>(options =>
        { options.Enabled = enabled; options.MaxAdjudicationsPerIngestion = adjudicator is null ? 0 : 2; if (identityPolicy is not null) options.Policies["EVENT"] = identityPolicy; });
        if (hostContext is not null) services.AddSingleton(hostContext);
        if (adjudicator is not null) services.AddSingleton(adjudicator);
        services.AddNeo4jAgentMemory(options =>
        { options.Uri = "bolt://unused.test:7687"; options.Username = "unused"; options.Password = "unused"; if (extension) options.Extensions.Add("identity-evidence"); });
        return services.BuildServiceProvider();
    }
    private static UnifiedExtractionResult Input(string name = "Acme go live", string type = "EVENT") => new()
    {
        Entities = [new ExtractedEntity { Name = name, Type = type, Confidence = 1 }],
        Facts = [new ExtractedFact { Subject = name, Predicate = "is on", Object = "December 12, 2026", Confidence = 1 }]
    };
    private static async Task<PersistenceResult> Ingest(IServiceProvider services, UnifiedExtractionResult input, MemoryScope? requestedScope = null,
        string messageId = "source")
    {
        var effectiveScope = requestedScope ?? Scope;
        var message = new Message { MessageId = messageId, ConversationId = "conversation", SessionId = "scenario", Role = "user", Content = "The Acme go live is on December 12, 2026", TimestampUtc = DateTimeOffset.Parse("2026-11-09T12:00:00Z") };
        var staged = await services.GetRequiredService<IExtractionStage>().ProcessUnifiedAsync([message], input, ExtractionTypes.All, effectiveScope);
        return await services.GetRequiredService<IPersistenceStage>().PersistAsync(staged, effectiveScope.OwnerId);
    }
    private async Task<long> Count(string pattern) => await fixture.TransactionRunner.ReadAsync(async runner =>
    { var cursor = await runner.RunAsync("MATCH " + pattern + " RETURN count(*) AS n"); return ValueExtensions.As<long>((await cursor.SingleAsync())["n"]); });
    private static Task<Entity> Seed(IServiceProvider services, string name, string type = "EVENT") => services.GetRequiredService<IEntityRepository>().UpsertAsync(new()
    { EntityId = "existing", Name = name, Type = type, OwnerId = "pablito", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });

    [Fact]
    public async Task Similarity_preserves_original_fact_and_existing_identity_and_stores_pending_support()
    {
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go-live");
        await Ingest(services, Input());
        (await services.GetRequiredService<IEntityRepository>().GetByIdAsync("existing"))!.Aliases.Should().BeEmpty();
        (await services.GetRequiredService<IFactRepository>().GetBySubjectAsync("Acme go live", Scope)).Should().ContainSingle();
        (await Count("(d:IdentityDecision)")).Should().Be(1);
        (await Count("(:IdentityDecision)-[:POSSIBLE_SAME_AS]->(:Entity {id:'existing'})")).Should().Be(1);
        (await Count("(:Fact)-[:HAS_IDENTITY_BINDING]->(:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity)")).Should().Be(1);
        (await Count("()-[:SAME_AS]->()")).Should().Be(0);
        (await Count("(:Fact)-[:ABOUT]->(:Entity {id:'existing'})")).Should().Be(0);
    }
    [Fact]
    public async Task Confirmed_calendar_identity_keeps_original_surface_and_does_not_upsert_target()
    {
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "2026-12-12", "DATE");
        await services.GetRequiredService<IIdentityEvidenceRepository>().AssertKindAsync("existing",
            new() { Kind = IdentitySubjectKind.CalendarDate, Authority = "fixture:calendar", SourceId = "fixture:date" }, "pablito");
        var original = await services.GetRequiredService<IEntityRepository>().GetByIdAsync("existing");
        var input = Input("12 December 2026", "DATE");
        await Ingest(services, input with { Facts = [input.Facts[0] with { Subject = "Wedding", Object = "12 December 2026" }] });
        (await services.GetRequiredService<IEntityRepository>().GetByIdAsync("existing")).Should().BeEquivalentTo(original);
        (await Count("(:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id:'existing'})")).Should().Be(1);
        var cursor = await fixture.TransactionRunner.ReadAsync(async runner => await (await runner.RunAsync("MATCH (d:IdentityDecision) RETURN d.identity_record_json AS value")).SingleAsync());
        ValueExtensions.As<string>(cursor["value"]).Should().Contain("12 December 2026").And.Contain("existing");
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Retry_and_concurrent_same_root_ingestion_keep_one_decision_and_final_fact_id(int repetition)
    {
        using var host = Host();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => { using var scope = host.CreateScope(); await Ingest(scope.ServiceProvider, Input()); }));
        using (var scope = host.CreateScope()) await Ingest(scope.ServiceProvider, Input());
        (await Count("(:IdentityDecision)")).Should().Be(1, $"cold-start repetition {repetition}");
        (await Count("(:Fact)")).Should().Be(1);
        (await Count("(:IdentityFactBinding)")).Should().Be(1);
        (await Count("(:Entity)")).Should().Be(1);
    }
    [Fact]
    public async Task Same_name_different_mentions_remain_separate_without_ambiguous_fact_binding()
    {
        using var host = Host(); using var scope = host.CreateScope();
        var input = Input("Córdoba", "LOCATION");
        await Ingest(scope.ServiceProvider, input with { Entities = [input.Entities[0] with { Description = "Argentina" }, input.Entities[0] with { Description = "Spain" }] });
        (await Count("(:IdentityDecision)")).Should().Be(2);
        (await Count("(:Entity)")).Should().Be(2);
        (await Count("(:IdentityFactBinding)")).Should().Be(0);
    }
    [Fact]
    public async Task Failed_later_fact_rolls_back_original_observation_decision_and_earlier_fact()
    {
        using var host = Host(); using var scope = host.CreateScope();
        var input = Input();
        var action = () => Ingest(scope.ServiceProvider, input with { Facts = [input.Facts[0], input.Facts[0] with { Object = new string('x', 40000) }] });
        var failure = await action.Should().ThrowAsync<MemoryIngestionException>();
        failure.Which.CompletedOutcomes.Should().NotContain(outcome => outcome.Stage == IngestionStage.Persistence && outcome.Status == IngestionItemStatus.Succeeded);
        (await Count("(:IdentityDecision)")).Should().Be(0);
        (await Count("(:Entity)")).Should().Be(0);
        (await Count("(:Fact)")).Should().Be(0);
    }
    [Fact]
    public async Task Identity_that_runs_brings_its_own_schema()
    {
        // ILC-25: the schema follows the one decision on whether identity runs, so a host can no longer switch identity on
        // and forget its schema (formerly the failure this test asserted).
        using var host = Host(extension: false); using var scope = host.CreateScope();
        host.GetRequiredService<IOptions<Neo4jOptions>>().Value.Extensions.Should().Contain("identity-evidence");
        await Ingest(scope.ServiceProvider, Input());
        (await Count("(:IdentityDecision)")).Should().Be(1);
    }

    [Fact]
    public async Task A_store_built_by_the_previous_resolver_keeps_everything_when_identity_comes_on()
    {
        // ILC-27: identity is on by default from 2.2. What the previous resolver stored stays, and a new mention of a stored
        // name is checked against it rather than silently becoming a second identity.
        string previous;
        using (var before = Host(enabled: false, extension: false))
        {
            using var first = before.CreateScope();
            await Ingest(first.ServiceProvider, Input());
            previous = (await first.ServiceProvider.GetRequiredService<IEntityRepository>().GetByNameAsync("Acme go live", scope: Scope)).Single().EntityId;
        }
        var entities = await Count("(:Entity)"); var facts = await Count("(:Fact)");

        using var after = Host(); using var scope = after.CreateScope();
        await Ingest(scope.ServiceProvider, Input());

        (await Count("(:Entity)")).Should().BeGreaterThanOrEqualTo(entities);
        (await Count("(:Fact)")).Should().BeGreaterThanOrEqualTo(facts);
        (await scope.ServiceProvider.GetRequiredService<IEntityRepository>().GetByIdAsync(previous)).Should().NotBeNull();
        var decision = await StoredDecision();
        IReadOnlyList<string> candidates = decision.ConfirmedEntityId is { } confirmed ? [confirmed] : decision.PendingEntityIds;
        candidates.Should().Contain(previous, "the previous resolver's entity is a candidate for the new mention");
    }
    [Fact]
    public async Task Disabled_mode_creates_no_identity_decisions_bindings_or_scope_locks()
    {
        using var host = Host(enabled: false); using var scope = host.CreateScope(); await Ingest(scope.ServiceProvider, Input());
        (await Count("(:IdentityDecision)")).Should().Be(0);
        (await Count("(:IdentityFactBinding)")).Should().Be(0);
        (await Count("(:IdentityEvidenceScopeLock)")).Should().Be(0);
    }
    [Fact]
    public async Task Identical_undisambiguated_mentions_in_one_batch_do_not_authorize_one_identity()
    {
        using var host = Host(); using var scope = host.CreateScope(); var input = Input("Córdoba", "LOCATION");
        await Ingest(scope.ServiceProvider, input with { Entities = [input.Entities[0], input.Entities[0]] });
        (await Count("(:IdentityDecision)")).Should().Be(2);
        (await Count("(:Entity)")).Should().Be(2);
        (await Count("(:IdentityFactBinding)")).Should().Be(0);
    }
    [Fact]
    public async Task All_original_facts_survive_optional_semantic_within_extraction_deduplication()
    {
        using var host = Host(); using var scope = host.CreateScope(); var input = Input();
        await Ingest(scope.ServiceProvider, input with
        {
            Facts = [input.Facts[0] with { Predicate = "requires", Object = "my physical presence" },
                input.Facts[0] with { Predicate = "requires", Object = "my physical presence in Córdoba" }]
        });
        (await Count("(:Fact)")).Should().Be(2);
        (await Count("(:IdentityFactBinding)")).Should().Be(2);
    }
    [Fact]
    public async Task Display_source_key_collisions_do_not_lose_final_fact_role_bindings()
    {
        using var host = Host(); using var scope = host.CreateScope(); var input = Input();
        await Ingest(scope.ServiceProvider, input with
        {
            Entities = [input.Entities[0] with { Name = "Acme go" }, input.Entities[0] with { Name = "Acme" }],
            Facts = [input.Facts[0] with { Subject = "Acme go", Predicate = "live", Object = "soon" },
                input.Facts[0] with { Subject = "Acme", Predicate = "go live", Object = "soon" }]
        });
        (await Count("(:Fact)")).Should().Be(2);
        (await Count("(:IdentityFactBinding)")).Should().Be(2);
    }
    [Fact]
    public async Task Original_observation_has_graph_provenance_only_to_its_owned_root_message()
    {
        using var host = Host(); using var scope = host.CreateScope(); await Ingest(scope.ServiceProvider, Input());
        (await Count("(:Entity)-[:EXTRACTED_FROM]->(:Message {id:'source'})")).Should().Be(1);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Foreign_source_roots_cannot_create_entities_or_facts_even_without_mentions(bool withMention)
    {
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync("MATCH (c:Conversation {id:'conversation'}) SET c.user_id='other'"); });
        using var host = Host(); using var scope = host.CreateScope(); var input = Input();
        if (!withMention) input = input with { Entities = [] };
        var action = () => Ingest(scope.ServiceProvider, input);
        await action.Should().ThrowAsync<Exception>();
        (await Count("(:IdentityDecision)")).Should().Be(0);
        (await Count("(:Entity)")).Should().Be(0);
        (await Count("(:Fact)")).Should().Be(0);
        (await Count("(:IdentityFactBinding)")).Should().Be(0);
    }

    [Fact]
    public async Task Caller_owned_outer_transactions_are_rejected_without_ingestion_side_effects()
    {
        using var host = Host(); using var scope = host.CreateScope();
        var atomic = (INeo4jAtomicTransactionRunner)fixture.TransactionRunner;
        var action = () => atomic.ExecuteAtomicWriteAsync(async _ => { await Ingest(scope.ServiceProvider, Input()); return true; });
        await action.Should().ThrowAsync<NotSupportedException>();
        atomic.HasActiveWriteTransaction.Should().BeFalse();
        (await Count("(:Entity)")).Should().Be(0);
        (await Count("(:Fact)")).Should().Be(0);
        (await Count("(:IdentityDecision)")).Should().Be(0);
    }

    [Theory]
    [InlineData(true, true)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(false, false)]
    public async Task Explicit_shared_root_permission_is_independent_of_extracted_mentions(bool includeShared, bool withMention)
    {
        await fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (c:Conversation {id:'conversation'}) REMOVE c.user_id"); });
        using var host = Host(); using var scope = host.CreateScope();
        var input = Input(); if (!withMention) input = input with { Entities = [] };
        var action = () => Ingest(scope.ServiceProvider, input, MemoryScope.For("pablito", includeShared));
        if (includeShared)
        {
            await action();
            (await Count("(:Fact {owner_id:'pablito'})")).Should().Be(1);
            (await Count("(:IdentityDecision)")).Should().Be(withMention ? 1 : 0);
        }
        else
        {
            await action.Should().ThrowAsync<Exception>();
            (await Count("(:Fact)")).Should().Be(0);
            (await Count("(:Entity)")).Should().Be(0);
            (await Count("(:IdentityDecision)")).Should().Be(0);
        }
    }

    [Theory]
    [InlineData("pablito", false)] [InlineData("*", false)] [InlineData("**", false)]
    [InlineData("pablito", true)] [InlineData("*", true)] [InlineData("**", true)]
    public async Task Owner_erasure_removes_identity_receipts_and_support_without_erasing_other_scopes(string erasedOwner, bool upgradeVersionTwo)
    {
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var facts = services.GetRequiredService<IFactRepository>();
        foreach (var owner in new string?[] { null, "pablito", "*", "**", "other" })
        {
            var key = owner is null ? "shared" : "owned:" + owner;
            var surface = "Private observation for " + key;
            var root = "external-source:" + key;
            var fact = await facts.UpsertAsync(new() { FactId = "fact:" + key, Subject = surface,
                Predicate = "is", Object = "original evidence", OwnerId = owner, Confidence = 1,
                SourceMessageIds = [root], CreatedAtUtc = DateTimeOffset.UnixEpoch });
            await decisions.SaveAsync(new()
            {
                Decision = new() { DecisionId = "decision:" + key, ObservationKey = "observation:" + key,
                    Observation = new() { EntityId = "entity:" + key, Name = surface, Type = "PERSON", OwnerId = owner,
                        Confidence = 1, SourceMessageIds = [root], CreatedAtUtc = DateTimeOffset.UnixEpoch },
                    Context = new() { SourceMessageIds = [root], Sources = [new() { SourceId = root,
                        Text = "Original sensitive source for " + key, OriginIds = [root] }] },
                    Policy = new(), Status = IdentityDecisionKind.New,
                    CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch },
                Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }]
            }, owner);
            await services.GetRequiredService<IIdentityEvidenceRepository>().AssertNameNamespaceAsync(
                "entity:" + key, "directory", "fixture:host", root, owner);
        }
        if (upgradeVersionTwo)
        {
            // Recreate the previous extension's actual storage shape in this owned disposable fixture.
            await fixture.TransactionRunner.WriteAsync(async runner =>
            { await runner.RunAsync("MATCH (n) WHERE n.owner_scope IS NOT NULL REMOVE n.owner_id"); });
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "Schema/Migrations/ext/identity-evidence/0003_identity_owner_stamps.cypher"));
            for (var repeat = 0; repeat < 2; repeat++)
                foreach (var statement in migration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(statement); });
        }
        await services.GetRequiredService<IMemoryOwnerDataService>().EraseAsync(erasedOwner);
        (await decisions.GetAsync("decision:owned:" + erasedOwner, erasedOwner)).Should().BeNull();
        var remaining = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (n) WHERE n.owner_scope = $ownerScope RETURN count(n) AS n",
                new { ownerScope = "owned:" + erasedOwner });
            return ValueExtensions.As<long>((await cursor.SingleAsync())["n"]);
        });
        remaining.Should().Be(0, "identity receipts, revisions, bindings, assertions and lock rows belong to this owner too");
        foreach (var retained in new string?[] { null, "pablito", "*", "**", "other" }.Where(owner => owner != erasedOwner))
        {
            var key = retained is null ? "shared" : "owned:" + retained;
            (await decisions.GetAsync("decision:" + key, retained)).Should().NotBeNull();
            (await services.GetRequiredService<IEntityRepository>().GetByIdAsync("entity:" + key)).Should().NotBeNull();
            var retainedFacts = await facts.GetBySubjectAsync("Private observation for " + key,
                retained is null ? AgentMemory.Core.Services.SharedScopes.SharedOnly : MemoryScope.For(retained, false));
            retainedFacts.Should().ContainSingle();
        }
    }
    private static readonly EntityIdentityPolicy EventIdentityPolicy = new() { IdentityProperties = ["eventId"] };
    [Theory]
    [InlineData("pablito")] [InlineData("*")] [InlineData("**")]
    public async Task Owner_erasure_includes_reviewed_decisions_and_unresolved_relationship_claims(string erasedOwner)
    {
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var entities = services.GetRequiredService<IEntityRepository>();
        foreach (var owner in new string?[] { erasedOwner, "retained-owner", null })
        {
            var key = owner is null ? "shared" : "owned:" + owner;
            var name = "Original mention for " + key;
            var root = "external-source:" + key;
            var decision = await decisions.SaveAsync(new()
            {
                Decision = new() { DecisionId = "reviewed:" + key, ObservationKey = "reviewed-observation:" + key,
                    Observation = new() { EntityId = "reviewed-entity:" + key, Name = name, Type = "PERSON", OwnerId = owner,
                        Confidence = 1, SourceMessageIds = [root], CreatedAtUtc = DateTimeOffset.UnixEpoch },
                    Context = new() { SourceMessageIds = [root], Sources = [new() { SourceId = root,
                        Text = "Original source for " + key, OriginIds = [root] }] },
                    Policy = new(), Status = IdentityDecisionKind.New,
                    CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch }
            }, owner);
            var target = await entities.UpsertAsync(new() { EntityId = "review-target:" + key, Name = "Reviewed person for " + key,
                Type = "PERSON", OwnerId = owner, Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
            await decisions.ReviewAsync(new() { DecisionId = decision.DecisionId, OperationId = "confirm:" + key,
                ExpectedVersion = decision.Version, Action = IdentityReviewAction.Confirm, CandidateEntityId = target.EntityId,
                Actor = "fixture:owner", Reason = "Explicit owner-reviewed identity" }, owner);
            await decisions.SaveRelationshipObservationAsync(new() { ObservationId = "unresolved-claim:" + key,
                OwnerId = owner, SourceMessageIds = [root], SourceDecisionId = decision.DecisionId,
                Observation = new() { SourceEntity = name, TargetEntity = "Unresolved relative",
                    RelationshipType = "SISTER_OF", Description = "Original unresolved claim for " + key },
                CreatedAtUtc = DateTimeOffset.UnixEpoch }, owner);
            (await decisions.GetHistoryAsync(decision.DecisionId, owner)).Should().HaveCount(2);
        }

        await services.GetRequiredService<IMemoryOwnerDataService>().EraseAsync(erasedOwner);

        (await decisions.GetAsync("reviewed:owned:" + erasedOwner, erasedOwner)).Should().BeNull();
        (await decisions.GetHistoryAsync("reviewed:owned:" + erasedOwner, erasedOwner)).Should().BeEmpty();
        (await decisions.GetRelationshipObservationAsync("unresolved-claim:owned:" + erasedOwner, erasedOwner)).Should().BeNull();
        var remaining = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (n) WHERE n.owner_scope = $ownerScope RETURN count(n) AS n",
                new { ownerScope = "owned:" + erasedOwner });
            return ValueExtensions.As<long>((await cursor.SingleAsync())["n"]);
        });
        remaining.Should().Be(0, "all revisions, original claims, support and scope infrastructure are owner data");
        foreach (var retained in new string?[] { "retained-owner", null })
        {
            var key = retained is null ? "shared" : "owned:" + retained;
            (await decisions.GetAsync("reviewed:" + key, retained))!.Version.Should().Be(2);
            (await decisions.GetHistoryAsync("reviewed:" + key, retained)).Should().HaveCount(2);
            var claim = await decisions.GetRelationshipObservationAsync("unresolved-claim:" + key, retained);
            claim.Should().NotBeNull();
            claim!.TargetDecisionId.Should().BeNull("unresolved support is retained without inventing an identity");
            claim.Observation.Description.Should().Be("Original unresolved claim for " + key);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Dated_event_reaches_undated_location_and_presence_without_similarity(bool remote)
    {
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        var input = Input() with { Facts = [
            new() { Subject = "Acme go live", Predicate = "is scheduled", Object = "December 12, 2026", Confidence = 1,
                OccurredOn = DateTimeOffset.Parse("2026-12-12T00:00:00Z") },
            new() { Subject = "Acme go live", Predicate = "takes place in", Object = "Córdoba", Confidence = 1 },
            new() { Subject = "Acme go live", Predicate = "participation", Object = remote ? "remote is allowed" : "in person is required", Confidence = 1 }
        ] };
        await Ingest(services, input);
        var facts = services.GetRequiredService<IFactRepository>();
        var stored = await facts.GetBySubjectAsync("Acme go live", Scope);
        var dated = stored.Single(f => f.Predicate == "is scheduled");
        var details = await facts.GetEventDetailsAsync([dated.FactId], [dated.FactId], 8, Scope);
        details.Select(f => f.Object).Should().BeEquivalentTo("Córdoba", remote ? "remote is allowed" : "in person is required");
        details.Should().OnlyContain(f => f.OccurredOn == null && f.ValidFrom == null);
        details.Should().OnlyContain(f => f.Embedding == null, "traversal does not fabricate vector evidence");
        (await facts.GetEventDetailsAsync([dated.FactId], [], 1, Scope)).Should().ContainSingle();
        (await facts.GetEventDetailsAsync([dated.FactId], [], 8, MemoryScope.For("other-owner", false))).Should().BeEmpty();
        var eventId = (await StoredDecision()).Observation.EntityId;
        await fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.invalidated_at=datetime()", new { id = eventId }); });
        (await facts.GetEventDetailsAsync([dated.FactId], [], 8, Scope)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_pending_event_candidate_does_not_supply_the_other_events_location()
    {
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go-live");
        var facts = services.GetRequiredService<IFactRepository>();
        var unrelated = await facts.UpsertAsync(new() { FactId = "unrelated-location", Subject = "Acme go-live",
            Predicate = "takes place in", Object = "Wrong city", OwnerId = "pablito", Confidence = 1,
            SourceMessageIds = ["source"], CreatedAtUtc = DateTimeOffset.UnixEpoch });
        await facts.CreateAboutRelationshipAsync(unrelated.FactId, "existing");
        await Ingest(services, Input() with { Facts = [new() { Subject = "Acme go live", Predicate = "is on",
            Object = "December 12, 2026", OccurredOn = DateTimeOffset.Parse("2026-12-12T00:00:00Z"), Confidence = 1 }] });
        (await StoredDecision()).Status.Should().Be(IdentityDecisionKind.Pending);
        var dated = (await facts.GetBySubjectAsync("Acme go live", Scope)).Single();
        (await facts.GetEventDetailsAsync([dated.FactId], [], 8, Scope)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_confirmed_person_reaches_their_bound_facts_and_an_open_proposal_only_names_a_namesake()
    {
        // ILC-33: identity-aware recall for people reads confirmed identities only, owner-scoped, live only.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Tomás Pérez", "PERSON");
        await Ingest(services, new UnifiedExtractionResult
        {
            Entities = [new() { Name = "Valentina", Type = "PERSON", Confidence = 1 }, new() { Name = "Tomás Pérez", Type = "PERSON", Confidence = 1 }],
            Facts = [
                new() { Subject = "Valentina", Predicate = "will monitor", Object = "the invoice queue remotely", Confidence = 1 },
                new() { Subject = "Valentina", Predicate = "will prepare", Object = "a video tutorial for the clerks", Confidence = 1 },
                new() { Subject = "Tomás Pérez", Predicate = "runs", Object = "the regression suite", Confidence = 1 }]
        });
        (await Count("(:IdentityDecision)-[:POSSIBLE_SAME_AS]->(:Entity {id: 'existing'})")).Should().Be(1, "precondition: Tomás Pérez is an open proposal");
        var facts = services.GetRequiredService<IFactRepository>();

        var people = await facts.GetPersonIdentitiesAsync(1000, Scope);
        var valentina = people.Where(p => p.Confirmed).Should().ContainSingle().Subject;
        valentina.Surfaces.Should().Contain("Valentina");
        // The open proposal's observation and the person it may be (seeded, no bound facts) are both listed, unconfirmed.
        people.Where(p => !p.Confirmed).Should().HaveCount(2).And.OnlyContain(p => p.Surfaces.Contains("Tomás Pérez"));
        var proposal = people.Single(p => !p.Confirmed && p.EntityId != "existing");
        (await facts.GetPersonDetailsAsync([proposal.EntityId], [], 8, Scope)).Should().BeEmpty("an open proposal's facts are never read");
        var details = await facts.GetPersonDetailsAsync([valentina.EntityId], [], 8, Scope);
        details.Select(f => f.Object).Should().BeEquivalentTo("the invoice queue remotely", "a video tutorial for the clerks");
        details.Should().OnlyContain(f => f.Embedding == null, "traversal does not fabricate vector evidence");
        (await facts.GetPersonDetailsAsync([valentina.EntityId], [details[0].FactId], 8, Scope)).Should().ContainSingle();
        (await facts.GetPersonDetailsAsync([valentina.EntityId], [], 1, Scope)).Should().ContainSingle();
        // REV2-2: the light first read lists every spelling, a name a bound fact used included, and nothing of another owner's.
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(
            "MATCH (b:IdentityFactBinding {owner_id:'pablito'})-[:RESOLVES_TO]->(:Entity {id:$id}) WITH b LIMIT 1 SET b.identity_observed_surface='Vale'",
            new { id = valentina.EntityId }); });
        (await facts.GetPersonNamesAsync(10_000, Scope)).Should().Contain(["Valentina", "Tomás Pérez", "Vale"]).And.OnlyHaveUniqueItems();
        (await facts.GetPersonNamesAsync(1, Scope)).Should().ContainSingle();
        var other = MemoryScope.For("other-owner", false);
        (await facts.GetPersonNamesAsync(10_000, other)).Should().BeEmpty();
        (await facts.GetPersonIdentitiesAsync(1000, other)).Should().BeEmpty();
        (await facts.GetPersonDetailsAsync([valentina.EntityId], [], 8, other)).Should().BeEmpty();
        await fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.invalidated_at=datetime()", new { id = valentina.EntityId }); });
        (await facts.GetPersonIdentitiesAsync(1000, Scope)).Should().NotContain(p => p.EntityId == valentina.EntityId);
        (await facts.GetPersonDetailsAsync([valentina.EntityId], [], 8, Scope)).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_live_person_makes_a_name_ambiguous_but_a_reviewed_proposal_s_observation_does_not()
    {
        // PR260-F4: a namesake with no bound facts (here an entity-only open proposal) still makes the name ambiguous; once
        // the proposal is confirmed by review, its leftover observation is not a person of its own.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync("MATCH (c:Conversation {id:'conversation'}) CREATE (m:Message {id:'source-2', conversation_id:'conversation', content:'Alex Kim called', session_id:'scenario'}), (c)-[:HAS_MESSAGE]->(m)"); });
        await Ingest(services, new UnifiedExtractionResult
        {
            Entities = [new() { Name = "Alex Kim", Type = "PERSON", Confidence = 1 }],
            Facts = [new() { Subject = "Alex Kim", Predicate = "is moving to", Object = "Buenos Aires", Confidence = 1 }]
        });
        await Ingest(services, new UnifiedExtractionResult { Entities = [new() { Name = "Alex Kim", Type = "PERSON", Confidence = 1 }] },
            messageId: "source-2");
        var facts = services.GetRequiredService<IFactRepository>();
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var proposal = (await decisions.ListPendingAsync("pablito", 10)).Should().ContainSingle("precondition: the second Alex Kim is an open proposal").Subject;

        var people = (await facts.GetPersonIdentitiesAsync(1000, Scope)).Where(p => p.Surfaces.Contains("Alex Kim")).ToList();
        people.Should().HaveCount(2, "the entity-only proposal has no bound facts but is a person all the same");
        var cousin = people.Should().ContainSingle(p => p.Confirmed).Subject;

        await decisions.ReviewAsync(new() { DecisionId = proposal.DecisionId, OperationId = "confirm-alex", ExpectedVersion = proposal.Version,
            Action = IdentityReviewAction.Confirm, CandidateEntityId = cousin.EntityId, Actor = "fixture:owner", Reason = "the same cousin" }, "pablito");

        (await facts.GetPersonIdentitiesAsync(1000, Scope)).Where(p => p.Surfaces.Contains("Alex Kim"))
            .Should().ContainSingle("a confirmed proposal's observation is the person it was confirmed as").Which.EntityId.Should().Be(cousin.EntityId);
    }

    [Fact]
    public async Task Owner_portability_brings_an_identity_store_back_whole()
    {
        // ILC-30: decisions, open proposals, fact bindings and evidence come back for the new owner, every reference
        // rewritten, through a JSON file as an export travels; the source owner is untouched and a second copy is possible.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var original = await StoredDecision();
        original.Status.Should().Be(IdentityDecisionKind.Pending, "the seeded entity leaves an open proposal to carry over");
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var exported = await owners.ExportAsync("pablito");
        exported.Format.Should().Be(OwnerMemoryExport.IdentityFormat);
        exported.IdentityState!.Nodes.Should().Contain(node => node.Label == "IdentityDecision");
        var file = JsonSerializer.Deserialize<OwnerMemoryExport>(JsonSerializer.Serialize(exported))!;

        var imported = await owners.ImportAsync(file, "copy-owner");

        foreach (var label in new[] { "IdentityDecision", "IdentityDecisionRevision", "IdentityFactBinding", "IdentityEvidenceAssertion", "IdentityRelationshipObservation" })
            (await Count($"(:{label} {{owner_id: 'copy-owner'}})")).Should().Be(await Count($"(:{label} {{owner_id: 'pablito'}})"), label);
        foreach (var type in new[] { "RESOLVES_TO", "POSSIBLE_SAME_AS", "HAS_IDENTITY_BINDING", "DECIDED_BY", "HAS_IDENTITY_REVISION" })
            (await Count($"(a)-[:{type}]->(b) WHERE coalesce(a.owner_id, b.owner_id) = 'copy-owner'"))
                .Should().Be(await Count($"(a)-[:{type}]->(b) WHERE coalesce(a.owner_id, b.owner_id) = 'pablito'"), type);
        (await decisions.ListPendingAsync("copy-owner", 50)).Should().HaveCount((await decisions.ListPendingAsync("pablito", 50)).Count);
        var copy = await decisions.GetAsync(imported.Ids[original.DecisionId], "copy-owner");
        copy.Should().NotBeNull();
        copy!.Status.Should().Be(original.Status);
        copy.ObservationKey.Should().NotBe(original.ObservationKey);
        copy.DecisionId.Should().Be("identity-decision-" + copy.ObservationKey);
        copy.Observation.EntityId.Should().Be("identity-observation-" + copy.ObservationKey);
        copy.PendingEntityIds.Should().Equal(original.PendingEntityIds.Select(id => imported.Ids.GetValueOrDefault(id, id)));
        var source = await decisions.GetAsync(original.DecisionId, "pablito");
        source.Should().NotBeNull("the source owner is untouched");
        (source!.Status, source.ObservationKey, source.Version).Should().Be((original.Status, original.ObservationKey, original.Version));
        source.PendingEntityIds.Should().Equal(original.PendingEntityIds);
        await owners.Invoking(o => o.ImportAsync(file, "third-owner")).Should().NotThrowAsync("each copy gets its own keys");
        var empty = await owners.ExportAsync("empty-owner");
        empty.IdentityState.Should().BeNull("REV2-13: with no identity records the export stays format 1, readable by 2.2");
        empty.Format.Should().Be(OwnerMemoryExport.CurrentFormat); empty.Entities.Should().BeEmpty();
    }

    /// <summary>Links between one owner's nodes and another's, either way: an import must never make one.</summary>
    private Task<long> CrossOwnerLinks(string ownerA, string ownerB) =>
        Count($"(a)-[r]-(b) WHERE a.owner_id = '{ownerA}' AND b.owner_id = '{ownerB}'");

    [Fact]
    public async Task An_imported_store_can_be_imported_twice_reviewed_and_revoked_in_the_same_database()
    {
        // PR260-F5: a second import of the same file into the same owner is a second copy, not a failure half-way.
        // PR260-F8: an imported proposal can be reviewed: its source messages stayed behind, and the original owner's
        // messages in this database are not its review roots.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var file = JsonSerializer.Deserialize<OwnerMemoryExport>(JsonSerializer.Serialize(await owners.ExportAsync("pablito")))!;

        var first = await owners.ImportAsync(file, "copy-owner");
        var second = await owners.ImportAsync(file, "copy-owner");

        var pending = await decisions.ListPendingAsync("copy-owner", 10);
        pending.Should().HaveCount(2, "each import is its own copy");
        var sourceDecisionId = (await StoredDecision()).DecisionId;
        var proposal = pending.Single(d => d.DecisionId == first.Ids[sourceDecisionId]);
        var reviewed = await decisions.ReviewAsync(new() { DecisionId = proposal.DecisionId, OperationId = "confirm-copy",
            ExpectedVersion = proposal.Version, Action = IdentityReviewAction.Confirm, CandidateEntityId = first.Ids["existing"],
            Actor = "fixture:owner", Reason = "the same go-live" }, "copy-owner");
        reviewed.CurrentDecision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        await decisions.ReviewAsync(new() { DecisionId = proposal.DecisionId, OperationId = "revoke-copy",
            ExpectedVersion = reviewed.CurrentDecision.Version, Action = IdentityReviewAction.Revoke,
            Actor = "fixture:owner", Reason = "not the same after all" }, "copy-owner");
        second.Ids["existing"].Should().NotBe(first.Ids["existing"]);
        (await CrossOwnerLinks("copy-owner", "pablito")).Should().Be(0);
        // REV1-S1: a binding's id follows its decision, fact and role, as binding makes it.
        var bindings = await fixture.TransactionRunner.ReadAsync(async runner => (await (await runner.RunAsync(
            "MATCH (f:Fact)-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding {owner_id:'copy-owner'}) " +
            "RETURN b.id AS id, b.identity_decision_id AS decision, f.id AS fact, b.identity_fact_role AS role")).ToListAsync())
            .Select(row => (Id: ValueExtensions.As<string>(row["id"]), Decision: ValueExtensions.As<string>(row["decision"]),
                Fact: ValueExtensions.As<string>(row["fact"]), Role: ValueExtensions.As<string>(row["role"]))).ToList());
        bindings.Should().NotBeEmpty().And.OnlyContain(b => b.Id == Neo4jIdentityDecisionRepository.BindingId(b.Decision, b.Fact, b.Role));
    }

    [Fact]
    public async Task An_imported_confirmation_keeps_its_own_reserved_observation_target_fingerprint_and_receipt()
    {
        // PR260-F6: a confirmed decision reserves its observation's entity id without creating it; the copy reserves its
        // own, so revoking both never collides. PR260-F7: the target fingerprint and the receipt describe the imported
        // target, not the source's.
        using var provider = Host(hostContext: HostProvider(HostProof()), identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme billing cutover");
        await services.GetRequiredService<IIdentityEvidenceRepository>().AssertKeyAsync("existing", HostProof().Keys[0], ["eventId"], "pablito");
        await Ingest(services, Input());
        var original = await StoredDecision();
        original.Status.Should().Be(IdentityDecisionKind.Confirmed, "precondition: a host key confirms the existing event");
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var file = JsonSerializer.Deserialize<OwnerMemoryExport>(JsonSerializer.Serialize(await owners.ExportAsync("pablito")))!;

        file.IdentityState!.CurrentConfirmations.Should().Equal([original.DecisionId], "the target still matches its fingerprint");
        var imported = await owners.ImportAsync(file, "copy-owner");

        var copy = (await decisions.GetAsync(imported.Ids[original.DecisionId], "copy-owner"))!;
        copy.ConfirmedEntityId.Should().Be(imported.Ids["existing"]);
        copy.Observation.EntityId.Should().Be("identity-observation-" + copy.ObservationKey);
        copy.EvidenceJson.Should().NotContain("\"existing\"").And.Contain(imported.Ids["existing"]);
        var stored = await fixture.TransactionRunner.ReadAsync(async runner => ValueExtensions.As<string>((await (await runner.RunAsync(
            "MATCH (d:IdentityDecision {id:$id}) RETURN d.identity_target_snapshot AS s", new { id = copy.DecisionId })).SingleAsync())["s"]));
        var current = await fixture.TransactionRunner.ReadAsync(runner =>
            Neo4jIdentityDecisionRepository.TargetSnapshotAsync(runner, copy, copy.ConfirmedEntityId!, CancellationToken.None));
        stored.Should().Be(current, "the copy's fingerprint is its imported target's");

        foreach (var (decision, owner) in new[] { (copy, "copy-owner"), (original, "pablito") })
            await decisions.ReviewAsync(new() { DecisionId = decision.DecisionId, OperationId = "revoke-" + owner, ExpectedVersion = decision.Version,
                Action = IdentityReviewAction.Revoke, Actor = "fixture:owner", Reason = "revoke" }, owner);
        (await Count("(e:Entity {owner_id:'copy-owner'}) WHERE e.id = 'identity-observation-' + e.identity_observation_key")).Should().Be(1);
        (await CrossOwnerLinks("copy-owner", "pablito")).Should().Be(0);
    }

    [Fact]
    public async Task An_identity_link_to_a_retired_entity_comes_back_to_a_retired_copy_never_to_the_source()
    {
        // PR260-F1: the open proposal's candidate was retired after the proposal; the export carries it as retired, and the
        // copy's proposal points at the copy's retired entity, never at the source owner's.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync("MATCH (e:Entity {id:'existing'}) SET e.invalidated_at = datetime()"); });
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var file = JsonSerializer.Deserialize<OwnerMemoryExport>(JsonSerializer.Serialize(await owners.ExportAsync("pablito")))!;

        var imported = await owners.ImportAsync(file, "copy-owner");

        (await CrossOwnerLinks("copy-owner", "pablito")).Should().Be(0);
        (await Count("(:IdentityDecision {owner_id:'copy-owner'})-[:POSSIBLE_SAME_AS]->(e:Entity {owner_id:'copy-owner'}) WHERE e.invalidated_at IS NOT NULL"))
            .Should().Be(1, "the proposal keeps its retired candidate, copied");
        imported.Ids.Should().ContainKey("existing");
    }

    [Fact]
    public async Task A_crafted_export_cannot_reach_another_owner()
    {
        // PR260-F9 and F10: import trusts no owner field, property or endpoint in the file. Owner fields are the
        // destination's, entities take identity properties only, and an endpoint that is neither imported nor shared stops
        // the import before anything is written, in either format.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        await services.GetRequiredService<IEntityRepository>().UpsertAsync(new()
        { EntityId = "victim-entity", Name = "Victim", Type = "PERSON", OwnerId = "victim", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var honest = JsonSerializer.Deserialize<OwnerMemoryExport>(JsonSerializer.Serialize(await owners.ExportAsync("pablito")))!;
        var state = honest.IdentityState!;
        static OwnerGraphValue Text(string value) => new() { Kind = OwnerGraphValue.String, Text = value };

        var forged = honest with { IdentityState = state with
        {
            EntityProperties = [.. state.EntityProperties.Select(node => node with { Properties = new Dictionary<string, OwnerGraphValue>(node.Properties)
                { ["owner_id"] = Text("victim"), ["name"] = Text("Hijacked") } })],
            Nodes = [.. state.Nodes.Select(node => node.Label != "IdentityDecision" ? node : node with { Properties =
                new Dictionary<string, OwnerGraphValue>(node.Properties) { ["owner_id"] = Text("victim"), ["owner_scope"] = Text("owned:victim") } })],
        } };
        state.EntityProperties.Should().NotBeEmpty("precondition: an entity carries identity properties");
        await owners.ImportAsync(forged, "crafted-owner");
        (await Count("(e:Entity) WHERE e.name = 'Hijacked' OR (e.owner_id = 'victim' AND e.id <> 'victim-entity')")).Should().Be(0);
        (await Count("(d:IdentityDecision {owner_id:'victim'})")).Should().Be(0);
        (await Count("(d:IdentityDecision {owner_id:'crafted-owner', owner_scope:'owned:crafted-owner'})")).Should().Be(1);

        var decision = state.Nodes.First(node => node.Label == "IdentityDecision").Key;
        var linked = honest with { IdentityState = state with { Edges = [.. state.Edges,
            new() { Type = "POSSIBLE_SAME_AS", FromLabel = "IdentityDecision", From = decision, ToLabel = "Entity", To = "victim-entity",
                Properties = new Dictionary<string, OwnerGraphValue>() }] } };
        await owners.Invoking(o => o.ImportAsync(linked, "crafted-owner-2")).Should().ThrowAsync<ArgumentException>();
        var plain = honest with
        {
            IdentityState = null, Format = OwnerMemoryExport.CurrentFormat,
            Relationships = [.. honest.Relationships, new() { RelationshipId = "forged", SourceEntityId = "existing", TargetEntityId = "victim-entity",
                RelationshipType = "KNOWS", Confidence = 1, OwnerId = "pablito", CreatedAtUtc = DateTimeOffset.UnixEpoch }],
        };
        await owners.Invoking(o => o.ImportAsync(plain, "crafted-owner-3")).Should().ThrowAsync<ArgumentException>();
        var about = honest with { IdentityState = null, Format = OwnerMemoryExport.CurrentFormat,
            About = [.. honest.About, new MemoryLink(honest.Facts[0].FactId, "victim-entity")] };
        await owners.Invoking(o => o.ImportAsync(about, "crafted-owner-4")).Should().ThrowAsync<ArgumentException>();
        // A decision whose own record names another owner would make the copy's reviews read that owner's scope.
        var renamed = honest with { IdentityState = state with { Nodes = [.. state.Nodes.Select(node => node.Label != "IdentityDecision" ? node : node with
        {
            Properties = new Dictionary<string, OwnerGraphValue>(node.Properties)
            { ["identity_record_json"] = Text(node.Properties["identity_record_json"].Text!.Replace("\"OwnerId\":\"pablito\"", "\"OwnerId\":\"victim\"")) },
        })] } };
        renamed.IdentityState!.Nodes.First(node => node.Label == "IdentityDecision").Properties["identity_record_json"].Text
            .Should().Contain("\"OwnerId\":\"victim\"", "precondition: the record names its owner");
        await owners.Invoking(o => o.ImportAsync(renamed, "crafted-owner-5")).Should().ThrowAsync<ArgumentException>();
        // An entity named only inside a decision's record, with no link to it, is checked like a link's end.
        var named = honest with { IdentityState = state with { Nodes = [.. state.Nodes.Select(node => node.Label != "IdentityDecision" ? node : node with
        {
            Properties = new Dictionary<string, OwnerGraphValue>(node.Properties)
            { ["identity_record_json"] = Text(node.Properties["identity_record_json"].Text!.Replace("\"existing\"", "\"victim-entity\"")) },
        })] } };
        named.IdentityState!.Nodes.First(node => node.Label == "IdentityDecision").Properties["identity_record_json"].Text
            .Should().Contain("\"victim-entity\"", "precondition: the record names its pending candidate");
        await owners.Invoking(o => o.ImportAsync(named, "crafted-owner-6")).Should().ThrowAsync<ArgumentException>();
        // REV1-M5: a malformed value anywhere stops the import before anything is written.
        var malformed = honest with { IdentityState = state with { Nodes = [.. state.Nodes.Select(node => node.Label != "IdentityDecision" ? node : node with
        {
            Properties = new Dictionary<string, OwnerGraphValue>(node.Properties) { ["created_at"] = new() { Kind = OwnerGraphValue.DateTime, Text = "not a date" } },
        })] } };
        await owners.Invoking(o => o.ImportAsync(malformed, "crafted-owner-7")).Should().ThrowAsync<ArgumentException>();
        // REV2-8: a record carried twice, or one that is not an identity record, stops the import before anything is written.
        var twice = honest with { IdentityState = state with { Nodes = [.. state.Nodes, state.Nodes.First(node => node.Label == "IdentityDecisionRevision")] } };
        await owners.Invoking(o => o.ImportAsync(twice, "crafted-owner-8")).Should().ThrowAsync<ArgumentException>();
        var notARecord = honest with { IdentityState = state with { Nodes = [.. state.Nodes,
            new OwnerGraphNode { Label = "Entity", Key = "smuggled-entity", Properties = new Dictionary<string, OwnerGraphValue>() }] } };
        await owners.Invoking(o => o.ImportAsync(notARecord, "crafted-owner-9")).Should().ThrowAsync<ArgumentException>();
        foreach (var owner in new[] { "crafted-owner-2", "crafted-owner-3", "crafted-owner-4", "crafted-owner-5", "crafted-owner-6", "crafted-owner-7",
                     "crafted-owner-8", "crafted-owner-9" })
            (await Count($"(n {{owner_id:'{owner}'}})")).Should().Be(0, $"{owner}: nothing is written before the endpoints are checked");
        (await Count("(a)-[r]-(:Entity {id:'victim-entity'})")).Should().Be(0);
    }

    private static OwnerGraphValue GraphText(string value) => new() { Kind = OwnerGraphValue.String, Text = value };
    private async Task<OwnerMemoryExport> ExportFile(IServiceProvider services) => JsonSerializer.Deserialize<OwnerMemoryExport>(
        JsonSerializer.Serialize(await services.GetRequiredService<IMemoryOwnerDataService>().ExportAsync("pablito")))!;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_merged_candidate_comes_back_merged_into_its_copy_or_into_itself(bool targetExported)
    {
        // PR260-F1: a retired copy keeps how it was retired; merged into something the file does not carry, it is merged
        // into itself rather than into the source owner's entity or into nothing. A malformed date stops the import first.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        if (targetExported)
            await services.GetRequiredService<IEntityRepository>().UpsertAsync(new()
            { EntityId = "winner", Name = "Acme launch", Type = "EVENT", OwnerId = "pablito", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
        await fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (e:Entity {id:'existing'}) SET e.merged_into = $into", new { into = targetExported ? "winner" : "gone" }); });
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var file = await ExportFile(services);

        var imported = await owners.ImportAsync(file, "copy-owner");

        var copy = imported.Ids["existing"];
        var mergedInto = await fixture.TransactionRunner.ReadAsync(async runner => ValueExtensions.As<string>((await (await runner.RunAsync(
            "MATCH (e:Entity {id:$id, owner_id:'copy-owner'}) RETURN e.merged_into AS m", new { id = copy })).SingleAsync())["m"]));
        mergedInto.Should().Be(targetExported ? imported.Ids["winner"] : copy);
        (await CrossOwnerLinks("copy-owner", "pablito")).Should().Be(0);

        var broken = file with { IdentityState = file.IdentityState! with { Retirements = [.. file.IdentityState.Retirements.Select(node => node with
        {
            Properties = new Dictionary<string, OwnerGraphValue>(node.Properties)
            { ["invalidated_at"] = new() { Kind = OwnerGraphValue.DateTime, Text = "not a date" } },
        })] } };
        await owners.Invoking(o => o.ImportAsync(broken, "broken-owner")).Should().ThrowAsync<ArgumentException>();
        (await Count("(n {owner_id:'broken-owner'})")).Should().Be(0, "the date is checked before anything is written");
    }

    [Fact]
    public async Task A_link_to_an_entity_this_store_does_not_hold_is_left_out_and_an_undeclared_property_dropped()
    {
        // PR260-F10/F9: 2.2.0 made such a link to a bare node it created; now it is left out and counted, in either format.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var honest = await ExportFile(services);
        var state = honest.IdentityState!;

        var plain = honest with
        {
            IdentityState = null, Format = OwnerMemoryExport.CurrentFormat,
            Relationships = [.. honest.Relationships, new() { RelationshipId = "to-nowhere", SourceEntityId = "existing", TargetEntityId = "nowhere-entity",
                RelationshipType = "KNOWS", Confidence = 1, OwnerId = "pablito", CreatedAtUtc = DateTimeOffset.UnixEpoch }],
        };
        (await owners.ImportAsync(plain, "copy-owner")).Written["skipped:relationships"].Should().Be(1);

        var decision = state.Nodes.First(node => node.Label == "IdentityDecision").Key;
        var full = honest with { IdentityState = state with
        {
            Edges = [.. state.Edges, new() { Type = "POSSIBLE_SAME_AS", FromLabel = "IdentityDecision", From = decision, ToLabel = "Entity",
                To = "nowhere-entity", Properties = new Dictionary<string, OwnerGraphValue>() }],
            Nodes = [.. state.Nodes.Select(node => node.Label != "IdentityDecision" ? node : node with
                { Properties = new Dictionary<string, OwnerGraphValue>(node.Properties) { ["smuggled"] = GraphText("x") } })],
        } };
        var written = (await owners.ImportAsync(full, "copy-owner-2")).Written;
        written["skipped:identity-links"].Should().Be(1);
        written["identity:properties-dropped"].Should().Be(1);
        (await Count("(n {id:'nowhere-entity'})")).Should().Be(0, "no bare node is made for a missing end");
        (await Count("(d:IdentityDecision) WHERE d.smuggled IS NOT NULL")).Should().Be(0);
    }

    [Fact]
    public async Task A_confirmation_stale_at_export_stays_stale_in_the_copy()
    {
        // PR260-F7: only a confirmation whose target still matched at export gets its imported target's fingerprint; a
        // stale one needs re-evaluation in the copy, as in the source.
        using var provider = Host(hostContext: HostProvider(HostProof()), identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme billing cutover");
        await services.GetRequiredService<IIdentityEvidenceRepository>().AssertKeyAsync("existing", HostProof().Keys[0], ["eventId"], "pablito");
        await Ingest(services, Input());
        var original = await StoredDecision();
        original.Status.Should().Be(IdentityDecisionKind.Confirmed, "precondition");
        await fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (e:Entity {id:'existing'}) SET e.description = 'changed after the confirmation'"); });
        var file = await ExportFile(services);
        file.IdentityState!.CurrentConfirmations.Should().BeEmpty();

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(file, "copy-owner");

        var copy = (await services.GetRequiredService<IIdentityDecisionRepository>().GetAsync(imported.Ids[original.DecisionId], "copy-owner"))!;
        var stored = await fixture.TransactionRunner.ReadAsync(async runner => ValueExtensions.As<string?>((await (await runner.RunAsync(
            "MATCH (d:IdentityDecision {id:$id}) RETURN d.identity_target_snapshot AS s", new { id = copy.DecisionId })).SingleAsync())["s"]));
        var current = await fixture.TransactionRunner.ReadAsync(runner =>
            Neo4jIdentityDecisionRepository.TargetSnapshotAsync(runner, copy, copy.ConfirmedEntityId!, CancellationToken.None));
        stored.Should().NotBe(current);
    }

    [Fact]
    public async Task A_contextual_confirmation_comes_back_with_its_imported_candidates_fingerprints()
    {
        // PR260-F7: a confirmation from context keeps its candidates' fingerprints in its receipt, keyed by candidate id;
        // the copy's are keyed by the imported candidates and match them.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        var target = await services.GetRequiredService<IEntityRepository>().UpsertAsync(new()
        { EntityId = "pepe", Name = "Pepe", Type = "PERSON", OwnerId = "pablito", Description = "Directory card", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
        var candidates = await services.GetRequiredService<Neo4jIdentityEvidenceRepository>().FindExactCandidatesAsync(new()
        { Name = "Pepe", Type = "PERSON", OwnerId = "pablito", Limit = 12 });
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(
            "MATCH (c:Conversation {id:'conversation'}) CREATE (m:Message {id:'pepe-message', conversation_id:'conversation', content:'Pepe is arriving.'}), (c)-[:HAS_MESSAGE]->(m)"); });
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var saved = await decisions.SaveAsync(new IdentityDecisionWrite { ExpectedCandidates = candidates.Candidates, Decision = new()
        {
            DecisionId = Guid.NewGuid().ToString("N"), ObservationKey = Guid.NewGuid().ToString("N"),
            Status = IdentityDecisionKind.Confirmed, ConfirmedEntityId = target.EntityId, Policy = new() { AllowContextualConfirmation = true },
            ContextualEvidence = new() { Verdict = IdentityAdjudicationVerdict.Same, CandidateId = target.EntityId,
                Citations = [new() { SourceId = "pepe-message", Quote = "Pepe is arriving." }] },
            Observation = new() { EntityId = Guid.NewGuid().ToString("N"), OwnerId = "pablito", Name = "Pepe", Type = "PERSON", Confidence = 1,
                SourceMessageIds = ["pepe-message"], CreatedAtUtc = DateTimeOffset.UnixEpoch },
            Context = new() { SourceMessageIds = ["pepe-message"], Sources = [new() { SourceId = "pepe-message", Text = "Pepe is arriving.", OriginIds = ["pepe-message"] }] },
            CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch
        } }, "pablito");
        saved.Status.Should().Be(IdentityDecisionKind.Confirmed, "precondition: confirmed from context");
        Neo4jIdentityDecisionRepository.ReadContextualSnapshots((await decisions.GetAsync(saved.DecisionId, "pablito"))!)
            .Should().ContainKey(target.EntityId, "precondition: the receipt keeps the candidates' fingerprints");
        var file = await ExportFile(services);
        file.IdentityState!.CurrentConfirmations.Should().Contain(saved.DecisionId);

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(file, "copy-owner");

        var copy = (await decisions.GetAsync(imported.Ids[saved.DecisionId], "copy-owner"))!;
        var snapshots = Neo4jIdentityDecisionRepository.ReadContextualSnapshots(copy);
        snapshots.Should().NotBeNull().And.ContainKey(imported.Ids["pepe"]);
        snapshots!.Keys.Should().OnlyContain(id => imported.Ids.Values.Contains(id), "every fingerprint is keyed by an imported candidate");
        var now = await fixture.TransactionRunner.ReadAsync(runner =>
            Neo4jIdentityDecisionRepository.CandidateSnapshotsAsync(runner, copy, snapshots.Keys, CancellationToken.None));
        now.Should().BeEquivalentTo(snapshots);
    }

    [Fact]
    public async Task A_decision_can_be_reviewed_after_its_fact_is_said_again_or_imported_into_its_own_owner()
    {
        // PR260-N1: a fact said again keeps every message it was said in. Replacing them dropped the message an earlier
        // decision binds through, so reviewing that decision failed; so did importing a store into its own owner.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync("MATCH (c:Conversation {id:'conversation'}) CREATE (m:Message {id:'source-2', conversation_id:'conversation', content:'The Acme go live is on December 12, 2026', session_id:'scenario'}), (c)-[:HAS_MESSAGE]->(m)"); });
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        await Ingest(services, Input(), messageId: "source-2");
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var original = (await decisions.ListPendingAsync("pablito", 10)).Single(d => d.Context.SourceMessageIds.Contains("source"));
        await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(await ExportFile(services), "pablito");

        var reviewed = await decisions.ReviewAsync(new() { DecisionId = original.DecisionId, OperationId = "confirm-original",
            ExpectedVersion = original.Version, Action = IdentityReviewAction.Confirm, CandidateEntityId = "existing",
            Actor = "fixture:owner", Reason = "the same go-live" }, "pablito");

        reviewed.CurrentDecision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        (await services.GetRequiredService<IFactRepository>().GetBySubjectAsync("Acme go live", Scope)).Single().SourceMessageIds
            .Should().Equal(["source-2"], "REV1-M6: a fact keeps its latest source, as dates and edge ending expect; review binds through the binding");
    }

    [Fact]
    public async Task An_imported_decision_s_observation_carries_the_destination_model_s_vector_and_its_history_none()
    {
        // PR260-N2: the export's vectors are the source model's; a revocation in the copy writes the observation's vector.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var file = await ExportFile(services);
        var state = file.IdentityState!;
        var tampered = file with { IdentityState = state with { Nodes = [.. state.Nodes.Select(node =>
            node.Label is not ("IdentityDecision" or "IdentityDecisionRevision") ? node : node with
            {
                Properties = new Dictionary<string, OwnerGraphValue>(node.Properties) { ["identity_record_json"] = GraphText(
                    System.Text.RegularExpressions.Regex.Replace(node.Properties["identity_record_json"].Text!, "\"Embedding\":(null|\\[[^\\]]*\\])", "\"Embedding\":[9,9,9,9]")) },
            })] } };
        var decisionKey = state.Nodes.First(node => node.Label == "IdentityDecision").Key;
        tampered.IdentityState!.Nodes.First(node => node.Key == decisionKey).Properties["identity_record_json"].Text
            .Should().Contain("[9,9,9,9]", "precondition: the source vector is a different model's");

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(tampered, "copy-owner");

        var copy = (await services.GetRequiredService<IIdentityDecisionRepository>().GetAsync(imported.Ids[decisionKey], "copy-owner"))!;
        copy.Observation.Embedding.Should().Equal([1f, 0f, 0f, 0f], "the destination model embeds the observation again");
        var history = await fixture.TransactionRunner.ReadAsync(async runner => (await (await runner.RunAsync(
            "MATCH (r:IdentityDecisionRevision {owner_id:'copy-owner'}) RETURN r.identity_record_json AS json")).ToListAsync())
            .Select(row => ValueExtensions.As<string>(row["json"])).ToList());
        history.Should().NotBeEmpty().And.OnlyContain(json => JsonSerializer.Deserialize<IdentityDecision>(json)!.Observation.Embedding == null);
    }

    [Fact]
    public async Task Asserting_an_imported_key_again_is_the_same_evidence()
    {
        // PR260-N5: an imported assertion's id follows its content, as asserting makes it, so asserting the same key again
        // neither duplicates it nor moves the entity's evidence version (which a confirmation's fingerprint includes).
        using var provider = Host(hostContext: HostProvider(HostProof()), identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme billing cutover");
        var evidence = services.GetRequiredService<IIdentityEvidenceRepository>();
        await evidence.AssertKeyAsync("existing", HostProof().Keys[0], ["eventId"], "pablito");
        var alias = new VerifiedIdentityAlias { Surface = "Acme billing cutover", Namespace = "fixture:events", SourceId = "fixture:José+1" };
        await evidence.AssertAliasAsync("existing", alias, "fixture:directory", "pablito");
        await Ingest(services, Input());
        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(await ExportFile(services), "copy-owner");
        var copy = imported.Ids["existing"];
        async Task<(long Assertions, long Version)> Evidence() => await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var row = await (await runner.RunAsync("MATCH (e:Entity {id:$id}) RETURN COUNT { (e)-[:HAS_IDENTITY_EVIDENCE]->() } AS n, " +
                "coalesce(e.identity_evidence_version, 0) AS v", new { id = copy })).SingleAsync();
            return (ValueExtensions.As<long>(row["n"]), ValueExtensions.As<long>(row["v"]));
        });
        var before = await Evidence();
        before.Assertions.Should().BeGreaterThan(0, "precondition: the key came with the copy");

        await evidence.AssertKeyAsync(copy, HostProof().Keys[0], ["eventId"], "copy-owner");
        await evidence.AssertAliasAsync(copy, alias, "fixture:directory", "copy-owner");

        (await Evidence()).Should().Be(before, "REV1-L7: a value with escaping (José, +) is the same evidence too");
    }

    [Fact]
    public async Task An_imported_store_answers_every_identity_operation_as_its_source_does()
    {
        // Holding the same records is not enough: the copy must behave as the original. Each identity operation runs on the
        // source owner and on an imported copy in the same store, and must give the same answer.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, new UnifiedExtractionResult
        {
            Entities = [new() { Name = "Acme go live", Type = "EVENT", Confidence = 1 }, new() { Name = "Valentina", Type = "PERSON", Confidence = 1 }],
            Facts = [new() { Subject = "Acme go live", Predicate = "is on", Object = "December 12, 2026", Confidence = 1 },
                new() { Subject = "Valentina", Predicate = "will monitor", Object = "the invoice queue", Confidence = 1 }]
        });
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var evidence = services.GetRequiredService<IIdentityEvidenceRepository>();
        var facts = services.GetRequiredService<IFactRepository>();
        var imported = await owners.ImportAsync(await ExportFile(services), "copy-owner");

        async Task<List<string>> Run(string owner, string target)
        {
            var scope = MemoryScope.For(owner, false);
            var answers = new List<string>();
            var pending = await decisions.ListPendingAsync(owner, 10);
            answers.Add("pending: " + string.Join(",", pending.Select(d => d.Observation.Name + "/" + d.PendingEntityIds.Count)));
            var proposal = pending.Single();
            answers.Add("history: " + (await decisions.GetHistoryAsync(proposal.DecisionId, owner)).Count);
            var people = await facts.GetPersonIdentitiesAsync(1000, scope);
            answers.Add("people: " + string.Join(",", people.Select(p => $"{p.Surfaces[0]}/{p.Confirmed}/{p.Complete}").Order()));
            var details = await facts.GetPersonDetailsAsync([.. people.Where(p => p.Confirmed).Select(p => p.EntityId)], [], 8, scope);
            answers.Add("details: " + string.Join(",", details.Select(f => f.Object).Order()));
            var dated = (await facts.GetBySubjectAsync("Acme go live", scope)).Single();
            answers.Add("event details: " + (await facts.GetEventDetailsAsync([dated.FactId], [], 8, scope)).Count);
            var version = await EvidenceVersion(target);
            await evidence.AssertKeyAsync(target, HostProof().Keys[0], ["eventId"], owner);
            await evidence.AssertKeyAsync(target, HostProof().Keys[0], ["eventId"], owner);
            answers.Add("evidence versions added: " + (await EvidenceVersion(target) - version));
            var rejected = await decisions.ReviewAsync(new() { DecisionId = proposal.DecisionId, OperationId = "reject-" + owner,
                ExpectedVersion = proposal.Version, Action = IdentityReviewAction.Reject, CandidateEntityId = proposal.PendingEntityIds.Single(),
                Actor = "fixture:owner", Reason = "not the same" }, owner);
            answers.Add("after reject: " + rejected.CurrentDecision.Status + ", pending " + (await decisions.ListPendingAsync(owner, 10)).Count);
            var again = await owners.ExportAsync(owner);
            answers.Add("re-export: " + string.Join(",", again.IdentityState!.Nodes.GroupBy(n => n.Label).Select(g => g.Key + "=" + g.Count()).Order()));
            return answers;
        }
        async Task<long> EvidenceVersion(string id) => await fixture.TransactionRunner.ReadAsync(async runner => ValueExtensions.As<long>((await (await runner.RunAsync(
            "MATCH (e:Entity {id:$id}) RETURN coalesce(e.identity_evidence_version, 0) AS v", new { id })).SingleAsync())["v"]));

        var source = await Run("pablito", "existing");
        var copy = await Run("copy-owner", imported.Ids["existing"]);

        copy.Should().Equal(source);
        (await CrossOwnerLinks("copy-owner", "pablito")).Should().Be(0);
    }

    [Fact]
    public async Task A_decision_record_s_own_ids_and_owner_are_the_copy_s_whatever_the_file_says()
    {
        // REV1-H1: the file's record may name another key's reserved observation or another decision id; the copy's record
        // names its own, so revoking it can never create an entity at an id another owner reserved.
        using var provider = Host(hostContext: HostProvider(HostProof()), identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme billing cutover");
        await services.GetRequiredService<IIdentityEvidenceRepository>().AssertKeyAsync("existing", HostProof().Keys[0], ["eventId"], "pablito");
        await Ingest(services, Input());
        var original = await StoredDecision();
        var file = await ExportFile(services);
        var squatted = "identity-observation-" + new string('b', 64);
        var forged = file with { IdentityState = file.IdentityState! with { Nodes = [.. file.IdentityState.Nodes.Select(node =>
            node.Label != "IdentityDecision" ? node : node with
            {
                Properties = new Dictionary<string, OwnerGraphValue>(node.Properties) { ["identity_record_json"] = GraphText(
                    node.Properties["identity_record_json"].Text!.Replace(original.Observation.EntityId, squatted).Replace(original.DecisionId, "identity-decision-forged")) },
            })] } };

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(forged, "copy-owner");

        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        var copy = (await decisions.GetAsync(imported.Ids[original.DecisionId], "copy-owner"))!;
        copy.DecisionId.Should().Be(imported.Ids[original.DecisionId]);
        copy.Observation.EntityId.Should().Be("identity-observation-" + copy.ObservationKey);
        copy.Observation.OwnerId.Should().Be("copy-owner");
        await decisions.ReviewAsync(new() { DecisionId = copy.DecisionId, OperationId = "revoke-copy", ExpectedVersion = copy.Version,
            Action = IdentityReviewAction.Revoke, Actor = "fixture:owner", Reason = "revoke" }, "copy-owner");
        (await Count($"(e:Entity {{id:'{squatted}'}})")).Should().Be(0, "no entity is made at the reserved id the file named");
    }

    [Fact]
    public async Task Imported_memories_point_only_at_the_importing_owner_s_own_messages()
    {
        // REV1-H2: in every format, a source message id stays only when it is the importing owner's own message here; any
        // other (another owner's, or one this store does not hold) becomes the importing owner's marker, so recall never
        // quotes, dates or links to another owner's messages.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var honest = await ExportFile(services);
        var plain = honest with { IdentityState = null, Format = OwnerMemoryExport.CurrentFormat };
        var facts = services.GetRequiredService<IFactRepository>();

        await owners.ImportAsync(plain, "copy-owner");
        (await facts.GetBySubjectAsync("Acme go live", MemoryScope.For("copy-owner", false))).Single().SourceMessageIds
            .Should().OnlyContain(id => id.StartsWith("imported-message-"), "'source' is another owner's message");
        (await Count("(:Fact {owner_id:'copy-owner'})-[:EXTRACTED_FROM]->(:Message)")).Should().Be(0);

        await owners.ImportAsync(plain, "pablito");
        (await facts.GetBySubjectAsync("Acme go live", Scope)).Single().SourceMessageIds
            .Should().Equal(["source"], "a restore into the same owner keeps its own messages");
    }

    [Fact]
    public async Task A_failed_embedding_during_import_leaves_no_vector_rather_than_an_empty_one()
    {
        // REV1-L9: the embedding service answers a failure with an empty vector; the copy's observation then has none.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var file = await ExportFile(services);
        services.GetRequiredService<IEmbeddingOrchestrator>().EmbedAsync("Acme go live", Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());
        var decisionKey = file.IdentityState!.Nodes.First(node => node.Label == "IdentityDecision").Key;

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(file, "copy-owner");

        (await services.GetRequiredService<IIdentityDecisionRepository>().GetAsync(imported.Ids[decisionKey], "copy-owner"))!
            .Observation.Embedding.Should().BeNull();
    }

    [Fact]
    public async Task Spellings_of_one_name_that_differ_only_in_case_count_once()
    {
        // REV1-L12: 64 case spellings of one name are one name, so the person's names are complete and recall still works.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        var spellings = Enumerable.Range(0, 64).Select(mask => new string("Valeri".Select((c, i) =>
            (mask >> i & 1) == 1 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)).ToArray())).ToList();
        await services.GetRequiredService<IEntityRepository>().UpsertAsync(new()
        { EntityId = "valeri", Name = "Valeri", Type = "PERSON", OwnerId = "pablito", Aliases = spellings, Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });

        var person = (await services.GetRequiredService<IFactRepository>().GetPersonIdentitiesAsync(1000, Scope)).Single(p => p.EntityId == "valeri");

        person.Complete.Should().BeTrue();
        person.Surfaces.Should().ContainSingle();
    }

    [Fact]
    public async Task An_older_export_whose_identity_was_merged_away_later_still_imports()
    {
        // REV2-1: a 2.2 export folds a mention into the identity it was confirmed as. Merged away since, that identity is
        // not exported; the mention comes back as its own entity instead of the whole import being refused.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        await services.GetRequiredService<IEntityRepository>().UpsertAsync(new()
        { EntityId = "merged-identity", Name = "Acme go-live", Type = "EVENT", OwnerId = "pablito", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync("MATCH (e:Entity {id:'merged-identity'}) SET e.merged_into = 'existing'"); });
        var honest = await ExportFile(services);
        var older = honest with { IdentityState = null, Format = OwnerMemoryExport.CurrentFormat, IdentityResolutions = [new MemoryLink("existing", "merged-identity")] };

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(older, "copy-owner");

        imported.Ids["existing"].Should().NotBe("merged-identity", "the mention is its own entity, not folded into another owner's");
        (await Count($"(e:Entity {{id:'{imported.Ids["existing"]}', owner_id:'copy-owner'}})")).Should().Be(1);
        (await CrossOwnerLinks("copy-owner", "pablito")).Should().Be(0);
    }

    [Fact]
    public async Task A_proposal_whose_candidates_stay_behind_stands_on_its_own_in_the_copy()
    {
        // REV2-3: moved to a store that does not hold its candidate (here a shared entity it lacks), the proposal cannot be
        // proposed there; it becomes New, as identity decides when no candidate exists, and the result says so.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var file = await ExportFile(services);
        var state = file.IdentityState!;
        static string Moved(string id) => id == "existing" ? "gone-shared" : id;
        var moved = file with
        {
            Entities = [.. file.Entities.Where(entity => entity.EntityId != "existing")],
            About = [.. file.About.Select(link => new MemoryLink(link.From, Moved(link.To)))],
            IdentityState = state with
            {
                EntityProperties = [.. state.EntityProperties.Where(node => node.Key != "existing")],
                Edges = [.. state.Edges.Select(edge => edge with { From = Moved(edge.From), To = Moved(edge.To) })],
                Nodes = [.. state.Nodes.Select(node => node.Label is not ("IdentityDecision" or "IdentityDecisionRevision") ? node : node with
                {
                    Properties = new Dictionary<string, OwnerGraphValue>(node.Properties)
                    { ["identity_record_json"] = GraphText(node.Properties["identity_record_json"].Text!.Replace("\"existing\"", "\"gone-shared\"")) },
                })],
            },
        };
        var decisionKey = state.Nodes.First(node => node.Label == "IdentityDecision").Key;

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(moved, "copy-owner");

        imported.Written.Should().Contain("identity:proposals-without-candidates", 1);
        var decisions = services.GetRequiredService<IIdentityDecisionRepository>();
        (await decisions.ListPendingAsync("copy-owner", 10)).Should().BeEmpty();
        var copy = (await decisions.GetAsync(imported.Ids[decisionKey], "copy-owner"))!;
        copy.Status.Should().Be(IdentityDecisionKind.New);
        copy.Diagnostics.Should().Contain("identity_import_candidates_not_in_store");
    }

    [Fact]
    public async Task A_restore_into_the_same_owner_keeps_its_keys_so_ingesting_the_message_again_replays()
    {
        // REV2-4: restored after an erasure, with the transcript back first, a decision keeps its key; ingesting the same
        // message again then replays it instead of making a second decision.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var original = await StoredDecision();
        var owners = services.GetRequiredService<IMemoryOwnerDataService>();
        var file = await ExportFile(services);
        await owners.EraseAsync("pablito");
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync("CREATE (c:Conversation {id:'conversation', user_id:'pablito'}), (m:Message {id:'source', conversation_id:'conversation', content:'The Acme go live is on December 12, 2026', session_id:'scenario'}), (c)-[:HAS_MESSAGE]->(m)"); });

        var imported = await owners.ImportAsync(file, "pablito");

        imported.Ids[original.DecisionId].Should().Be(original.DecisionId, "the key is free here and its sources are the owner's own");
        await Ingest(services, Input());
        (await Count("(d:IdentityDecision {owner_id:'pablito'})")).Should().Be(1, "ingesting the restored message again replays its decision");
    }

    [Fact]
    public async Task A_binding_that_does_not_fit_its_fact_or_decision_is_left_out()
    {
        // REV2-9: a binding ties a fact said about its observed name to a decision whose sources it shares; one that does
        // not (here, a different name) is dropped and counted, not imported as the file says.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var file = await ExportFile(services);
        var state = file.IdentityState!;
        var bindings = state.Nodes.Count(node => node.Label == "IdentityFactBinding");
        bindings.Should().BePositive("precondition");
        var mismatched = file with { IdentityState = state with { Nodes = [.. state.Nodes.Select(node => node.Label != "IdentityFactBinding" ? node : node with
        {
            Properties = new Dictionary<string, OwnerGraphValue>(node.Properties) { ["identity_observed_surface"] = GraphText("Someone else") },
        })] } };

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(mismatched, "copy-owner");

        imported.Written.Should().Contain("identity:bindings-dropped", bindings);
        (await Count("(:IdentityFactBinding {owner_id:'copy-owner'})")).Should().Be(0);
    }

    [Fact]
    public async Task A_message_planted_at_an_import_marker_refuses_the_import()
    {
        // REV2-10: markers are predictable; a message already at one would take the copy's facts into someone else's
        // conversation, so the import stops before writing anything.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var file = await ExportFile(services);
        var marker = OwnerIdentityPortability.NewMessageId("copy-owner", "source");
        await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(
            "CREATE (c:Conversation {id:'victim-conversation', user_id:'victim'}), (m:Message {id:$marker, conversation_id:'victim-conversation', content:'planted'}), (c)-[:HAS_MESSAGE]->(m)",
            new { marker }); });

        await services.GetRequiredService<IMemoryOwnerDataService>().Invoking(o => o.ImportAsync(file, "copy-owner")).Should().ThrowAsync<ArgumentException>();
        (await Count("(n {owner_id:'copy-owner'})")).Should().Be(0);
    }

    [Fact]
    public async Task Importing_a_fact_the_owner_already_holds_leaves_the_owners_fact_as_it_is()
    {
        // REV2-14: the owner holds "Acme go live is on December 12, 2026", live, said in its own message; the file holds the
        // same fact closed, from elsewhere. Merging would reopen, re-source or close the owner's fact; the import leaves it.
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await Ingest(services, Input());
        var file = await ExportFile(services);
        var closed = file with { IdentityState = null, Format = OwnerMemoryExport.CurrentFormat,
            Facts = [.. file.Facts.Select(fact => fact with { InvalidatedAtUtc = DateTimeOffset.Parse("2026-11-20T00:00:00Z"), SourceMessageIds = ["elsewhere"] })] };

        var imported = await services.GetRequiredService<IMemoryOwnerDataService>().ImportAsync(closed, "pablito");

        imported.Written.Should().Contain("facts:already-held", 1);
        var fact = (await services.GetRequiredService<IFactRepository>().GetBySubjectAsync("Acme go live", Scope)).Single();
        fact.InvalidatedAtUtc.Should().BeNull();
        fact.SourceMessageIds.Should().Equal(["source"]);
    }

    private static IdentityHostEvidence HostProof(string eventId = "ACME-2026") => new()
    {
        Keys = [new() { Namespace = "fixture:events", Authority = "fixture:directory", SourceId = "fixture:event:" + eventId,
            Values = new Dictionary<string, string> { ["eventId"] = eventId } }],
        Kind = new() { Kind = IdentitySubjectKind.Reference, Authority = "fixture:directory", SourceId = "fixture:event-kind" },
        NameNamespace = new() { Namespace = "fixture:events", Authority = "fixture:directory", SourceId = "fixture:event-names" }
    };
    private static IIdentityContextProvider HostProvider(IdentityHostEvidence evidence)
    {
        var host = Substitute.For<IIdentityContextProvider>();
        host.GetContextAsync(Arg.Any<ExtractedEntity>(), Arg.Any<EntityIdentityResolutionContext>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(evidence);
        return host;
    }
    private async Task<IdentityDecision> StoredDecision() => await fixture.TransactionRunner.ReadAsync(async runner =>
    {
        var cursor = await runner.RunAsync("MATCH (d:IdentityDecision {owner_id:$owner}) RETURN d.identity_record_json AS value", new { owner = "pablito" });
        return JsonSerializer.Deserialize<IdentityDecision>(ValueExtensions.As<string>((await cursor.SingleAsync())["value"]))!;
    });

    [Fact]
    public async Task Verified_host_key_confirms_an_existing_target_outside_the_write_transaction()
    {
        var outside = false; var host = Substitute.For<IIdentityContextProvider>();
        host.GetContextAsync(Arg.Any<ExtractedEntity>(), Arg.Any<EntityIdentityResolutionContext>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                outside = !((INeo4jAtomicTransactionRunner)fixture.TransactionRunner).HasActiveWriteTransaction;
                call.ArgAt<MemoryScope?>(2).Should().BeEquivalentTo(Scope);
                return HostProof();
            });
        using var provider = Host(hostContext: host, identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme billing cutover");
        await services.GetRequiredService<IIdentityEvidenceRepository>().AssertKeyAsync("existing", HostProof().Keys[0], ["eventId"], "pablito");
        var original = await services.GetRequiredService<IEntityRepository>().GetByIdAsync("existing");
        var input = Input();
        await Ingest(services, input with { Entities = [input.Entities[0] with { Attributes = new Dictionary<string, object> { ["eventId"] = "model-guess" } }] });
        outside.Should().BeTrue();
        var decision = await StoredDecision();
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed); decision.ConfirmedEntityId.Should().Be("existing");
        decision.Context.VerifiedKeys.Should().ContainSingle().Which.Values["eventId"].Should().Be("ACME-2026");
        decision.Observation.Attributes["eventId"].ToString().Should().Be("model-guess");
        (await services.GetRequiredService<IEntityRepository>().GetByIdAsync("existing")).Should().BeEquivalentTo(original);
        (await Count("(:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id:'existing'})")).Should().Be(1);
    }

    [Fact]
    public async Task A_host_key_conflict_prevents_even_an_identical_name_from_merging()
    {
        using var provider = Host(hostContext: HostProvider(HostProof()), identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
        await Seed(services, "Acme go live");
        await services.GetRequiredService<IIdentityEvidenceRepository>().AssertKeyAsync("existing", HostProof("OTHER").Keys[0], ["eventId"], "pablito");
        await Ingest(services, Input());
        var decision = await StoredDecision();
        decision.Status.Should().Be(IdentityDecisionKind.New); decision.ConfirmedEntityId.Should().BeNull();
        (await Count("(:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id:'existing'})")).Should().Be(0);
        (await Count("(:IdentityDecision)-[:POSSIBLE_SAME_AS]->(:Entity {id:'existing'})")).Should().Be(0);
        (await Count("(:Fact)")).Should().Be(1);
    }

    [Fact]
    public async Task New_observation_reloads_its_host_namespace_authority_and_sources_after_a_new_host()
    {
        string decisionId;
        using (var provider = Host(hostContext: HostProvider(HostProof()), identityPolicy: EventIdentityPolicy))
        using (var scope = provider.CreateScope())
        {
            await Ingest(scope.ServiceProvider, Input()); decisionId = (await StoredDecision()).DecisionId;
        }
        using var reopened = Host(); using var reopenedScope = reopened.CreateScope();
        var decision = await reopenedScope.ServiceProvider.GetRequiredService<IIdentityDecisionRepository>().GetAsync(decisionId, "pablito");
        decision.Should().NotBeNull();
        decision!.Context.NameNamespaceEvidence.Should().BeEquivalentTo(HostProof().NameNamespace);
        decision.Context.VerifiedKind.Should().BeEquivalentTo(HostProof().Kind);
        decision.Context.SourceMessageIds.Should().Equal("source");
        decision.Context.VerifiedKeys.Should().ContainSingle().Which.Values["eventId"].Should().Be("ACME-2026");
        decision.Status.Should().Be(IdentityDecisionKind.New);
    }

    [Fact]
    public async Task Host_failure_keeps_ingestion_unacknowledged_and_its_source_replayable()
    {
        var host = Substitute.For<IIdentityContextProvider>();
        host.GetContextAsync(Arg.Any<ExtractedEntity>(), Arg.Any<EntityIdentityResolutionContext>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IdentityHostEvidence?>(new InvalidOperationException("directory unavailable")));
        using var provider = Host(hostContext: host, identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope();
        var action = () => Ingest(scope.ServiceProvider, Input());
        await action.Should().ThrowAsync<MemoryIngestionException>();
        (await Count("(:IdentityDecision)")).Should().Be(0); (await Count("(:Fact)")).Should().Be(0);
        (await Count("(:Message {id:'source'})")).Should().Be(1);
    }

    [Fact]
    public async Task A_new_host_identifier_is_discoverable_by_a_later_different_mention()
    {
        using var provider = Host(hostContext: HostProvider(HostProof()), identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
        await Ingest(services, Input()); var first = await StoredDecision();
        await Ingest(services, Input("Acme cutover"));
        var second = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (d:IdentityDecision {owner_id:$owner}) WHERE d.id <> $first RETURN d.identity_record_json AS value",
                new { owner = "pablito", first = first.DecisionId });
            return JsonSerializer.Deserialize<IdentityDecision>(ValueExtensions.As<string>((await cursor.SingleAsync())["value"]))!;
        });
        second.Status.Should().Be(IdentityDecisionKind.Confirmed);
        second.ConfirmedEntityId.Should().Be(first.Observation.EntityId);
        second.Observation.Name.Should().Be("Acme cutover");
        (await Count("(:Entity)")).Should().Be(1);
    }

    [Fact]
    public async Task Host_assertions_roll_back_with_the_observation_and_decision()
    {
        using var provider = Host(); using var scope = provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IIdentityDecisionRepository>();
        var proof = HostProof();
        var request = new IdentityDecisionWrite { Decision = new()
        {
            DecisionId = "host-rollback", ObservationKey = "host-rollback", Status = IdentityDecisionKind.New,
            Policy = EventIdentityPolicy,
            Observation = new() { EntityId = "host-observation", Name = "Acme go live", Type = "EVENT", OwnerId = "pablito",
                Confidence = 1, SourceMessageIds = ["source"], CreatedAtUtc = DateTimeOffset.UnixEpoch },
            Context = new() { SourceMessageIds = ["source"], VerifiedKeys = proof.Keys, VerifiedKind = proof.Kind,
                NameNamespace = proof.NameNamespace!.Namespace, NameNamespaceEvidence = proof.NameNamespace },
            CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch
        } };
        var action = () => repository.ExecuteScopedAsync<bool>("pablito", false, ["source"], async ct =>
        {
            await repository.SaveAsync(request, "pablito", ct);
            var asserted = await fixture.TransactionRunner.WriteAsync(async runner =>
            {
                var cursor = await runner.RunAsync("MATCH (:Entity {id:$id})-[:HAS_IDENTITY_EVIDENCE]->(a) RETURN count(a) AS n", new { id = "host-observation" });
                return ValueExtensions.As<long>((await cursor.SingleAsync())["n"]);
            }, ct);
            asserted.Should().Be(3);
            throw new InvalidOperationException("rollback after host assertions");
        });
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("rollback after host assertions");
        (await Count("(:IdentityEvidenceAssertion)")).Should().Be(0);
        (await Count("(:IdentityDecision)")).Should().Be(0); (await Count("(:Entity)")).Should().Be(0);
    }

    [Fact]
    public async Task Host_assertion_replay_does_not_duplicate_or_restamp_active_evidence()
    {
        using var provider = Host(hostContext: HostProvider(HostProof()), identityPolicy: EventIdentityPolicy);
        using var scope = provider.CreateScope(); var services = scope.ServiceProvider;
        await Ingest(services, Input()); var first = await StoredDecision();
        var version = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (e:Entity {id:$id}) RETURN e.identity_evidence_version AS v", new { id = first.Observation.EntityId });
            return ValueExtensions.As<long>((await cursor.SingleAsync())["v"]);
        });
        (await Count("(:IdentityEvidenceAssertion)")).Should().Be(3);
        await Ingest(services, Input());
        var again = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (e:Entity {id:$id}) RETURN e.identity_evidence_version AS v", new { id = first.Observation.EntityId });
            return ValueExtensions.As<long>((await cursor.SingleAsync())["v"]);
        });
        again.Should().Be(version);
        (await Count("(:IdentityEvidenceAssertion)")).Should().Be(3);
        (await Count("(:IdentityDecision)")).Should().Be(1);
    }

    [Fact]
    public async Task Incomplete_host_calendar_kind_stays_unindexed_without_losing_the_observation()
    {
        var host = HostProvider(new() { Kind = new() { Kind = IdentitySubjectKind.CalendarDate,
            Authority = "fixture:calendar", SourceId = "fixture:partial" } });
        using var provider = Host(hostContext: host); using var scope = provider.CreateScope();
        var input = Input("December 12", "DATE");
        await Ingest(scope.ServiceProvider, input with { Facts = [input.Facts[0] with { Subject = "Wedding", Object = "December 12" }] });
        var decision = await StoredDecision();
        decision.Status.Should().Be(IdentityDecisionKind.New); decision.Observation.Name.Should().Be("December 12");
        decision.Context.VerifiedKind!.Kind.Should().Be(IdentitySubjectKind.CalendarDate);
        (await Count("(e:Entity) WHERE e.identity_calendar_key IS NOT NULL")).Should().Be(0);
        (await Count("(:Fact)")).Should().Be(1);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Optional_model_runs_outside_the_actual_ingestion_transaction_and_persists_typed_evidence(bool allow)
    {
        var model = Substitute.For<IIdentityAdjudicator>(); var states = new List<bool>();
        model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            states.Add(((INeo4jAtomicTransactionRunner)fixture.TransactionRunner).HasActiveWriteTransaction);
            return "{\"decision\":\"same\",\"candidateId\":\"existing\",\"evidence\":[{\"sourceId\":\"source\",\"quote\":\"Acme go live\"}]}";
        });
        using var host = Host(identityPolicy: new() { AllowContextualConfirmation = allow }, adjudicator: model);
        using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        var original = await Seed(services, "Acme go-live");
        await Ingest(services, Input());
        states.Should().ContainSingle().Which.Should().BeFalse();
        var decision = await StoredDecision();
        decision.Status.Should().Be(allow ? IdentityDecisionKind.Confirmed : IdentityDecisionKind.Pending);
        decision.ContextualEvidence!.Verdict.Should().Be(IdentityAdjudicationVerdict.Same);
        decision.ContextualEvidence.Citations.Should().ContainSingle().Which.Quote.Should().Be("Acme go live");
        decision.Observation.Name.Should().Be("Acme go live");
        (await services.GetRequiredService<IEntityRepository>().GetByIdAsync("existing")).Should().BeEquivalentTo(original);
        (await services.GetRequiredService<IFactRepository>().GetBySubjectAsync("Acme go live", Scope)).Should().ContainSingle();
        (await Count("()-[:SAME_AS|ABOUT]->()")).Should().Be(0);
        (await Count("(:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id:'existing'})")).Should().Be(allow ? 1 : 0);
    }

    [Fact]
    public async Task Relationship_claims_keep_their_original_record_and_decision_owned_endpoint_support()
    {
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        var input = new UnifiedExtractionResult
        {
            Entities = [new() { Name = "Acme go live", Type = "EVENT", Confidence = 1 },
                new() { Name = "Córdoba", Type = "LOCATION", Confidence = 1 }],
            Relationships = [new() { SourceEntity = "Acme go live", RelationshipType = "LOCATED_IN", TargetEntity = "Córdoba",
                Description = "The original on-site event location", Confidence = 1 }]
        };
        await Ingest(services, input);
        (await Count("(:IdentityRelationshipObservation)")).Should().Be(1,
            "the original relationship must remain independently inspectable when an endpoint identity is reviewed");
        (await Count("(:IdentityRelationshipObservation)-[:SOURCE_IDENTITY]->(:IdentityDecision)")).Should().Be(1);
        (await Count("(:IdentityRelationshipObservation)-[:TARGET_IDENTITY]->(:IdentityDecision)")).Should().Be(1);
        (await Count("()-[:RELATED_TO]->()")).Should().Be(0,
            "an identity interpretation must not be embedded irreversibly in an ordinary shared edge");
        (await services.GetRequiredService<IRelationshipRepository>().GetLiveAroundAsync([], ["Acme go live"], 16,
            DateTimeOffset.UtcNow, Scope)).Should().ContainSingle();
        await Ingest(services, input);
        (await Count("(:IdentityRelationshipObservation)")).Should().Be(1, "the stable source observation is idempotent");
    }

    [Fact]
    public async Task Ambiguous_relationship_endpoint_preserves_the_claim_without_guessing_identity_support()
    {
        using var host = Host(); using var scope = host.CreateScope(); var services = scope.ServiceProvider;
        var input = new UnifiedExtractionResult
        {
            Entities = [new() { Name = "Alex", Type = "PERSON", Description = "Argentina", Confidence = 1 },
                new() { Name = "Alex", Type = "PERSON", Description = "Spain", Confidence = 1 },
                new() { Name = "Acme", Type = "ORGANIZATION", Confidence = 1 }],
            Relationships = [new() { SourceEntity = "Alex", RelationshipType = "WORKS_FOR", TargetEntity = "Acme", Confidence = 1 }]
        };
        await Ingest(services, input);
        (await Count("(:IdentityRelationshipObservation)")).Should().Be(1,
            "an unresolved identity must not discard the original relationship assertion");
        (await Count("(:IdentityRelationshipObservation)-[:SOURCE_IDENTITY]->(:IdentityDecision)")).Should().Be(0);
        (await Count("(:IdentityRelationshipObservation)-[:TARGET_IDENTITY]->(:IdentityDecision)")).Should().Be(1);
        (await services.GetRequiredService<IRelationshipRepository>().GetLiveAroundAsync([], ["Acme"], 16,
            DateTimeOffset.UtcNow, Scope)).Should().BeEmpty();
    }

}
