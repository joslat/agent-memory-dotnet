using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Extraction;
using AgentMemory.Neo4j.Infrastructure;
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
    private static async Task<PersistenceResult> Ingest(IServiceProvider services, UnifiedExtractionResult input, MemoryScope? requestedScope = null)
    {
        var effectiveScope = requestedScope ?? Scope;
        var message = new Message { MessageId = "source", ConversationId = "conversation", SessionId = "scenario", Role = "user", Content = "The Acme go live is on December 12, 2026", TimestampUtc = DateTimeOffset.Parse("2026-11-09T12:00:00Z") };
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
        empty.IdentityState!.Nodes.Should().BeEmpty(); empty.Entities.Should().BeEmpty();
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
