using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class FactRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _repo;

    private static readonly float[] TestEmbedding = [0.3f, 0.1f, 0.4f, 0.2f];
    private static readonly float[] QueryEmbedding = [0.3f, 0.1f, 0.4f, 0.2f];

    public FactRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jFactRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertAsync_CreatesFact_WithSubjectPredicateObject()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Alice",
            Predicate = "works_at",
            Object = "Acme Corp",
            Confidence = 0.9,
            CreatedAtUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var result = await _repo.UpsertAsync(fact);

        result.FactId.Should().Be(fact.FactId);
        result.Subject.Should().Be("Alice");
        result.Predicate.Should().Be("works_at");
        result.Object.Should().Be("Acme Corp");
        result.Confidence.Should().Be(0.9);
    }

    [Fact]
    public async Task UpsertAsync_ReUpsertSameTriple_KeepsStableId_SoByIdHandleStillResolves()
    {
        var idA = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact
        {
            FactId = idA, Subject = "Alice", Predicate = "works_at", Object = "Acme",
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow
        });

        // Re-extract the SAME triple with a DIFFERENT freshly-generated id (the common re-extraction case).
        var idB = $"fact-{Guid.NewGuid():N}";
        var reUpserted = await _repo.UpsertAsync(new Fact
        {
            FactId = idB, Subject = "Alice", Predicate = "works_at", Object = "Acme",
            Confidence = 0.95, CreatedAtUtc = DateTimeOffset.UtcNow
        });

        // The node must KEEP its original id; the triple MERGE must not rewrite the stable primary key.
        reUpserted.FactId.Should().Be(idA, "re-upsert of the same triple must not clobber the stable id");
        (await _repo.GetByIdAsync(idA)).Should().NotBeNull("the original id must still resolve after re-upsert");
        (await _repo.GetByIdAsync(idB)).Should().BeNull("the discarded second id must never become the node's id");
        // The by-id handle the caller holds must still work for invalidate.
        (await _repo.InvalidateAsync(idA, scope: null)).Should().BeTrue("invalidate by the original id must succeed");
    }

    [Fact]
    public async Task UpsertAsync_ReUpsertSupersededTriple_DoesNotClearValidUntil()
    {
        var loserId = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = loserId, Subject = "Alice", Predicate = "lives_in", Object = "Paris", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        var winnerId = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = winnerId, Subject = "Alice", Predicate = "lives_in", Object = "London", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        // Supersede stamps the loser's valid_until (closes its valid-time window).
        (await _repo.SupersedeAsync(loserId, winnerId, scope: null)).Should().BeTrue();
        (await HasValidUntilAsync(loserId)).Should().BeTrue("supersede must close the loser's valid-time window");

        // Re-extract the loser's triple with NO explicit validUntil (the common extraction case).
        await _repo.UpsertAsync(new Fact { FactId = $"fact-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "lives_in", Object = "Paris", Confidence = 0.95, CreatedAtUtc = DateTimeOffset.UtcNow });

        (await HasValidUntilAsync(loserId)).Should().BeTrue(
            "re-extracting a superseded triple must NOT clear the valid_until that supersession stamped");
    }

    private async Task<bool> HasValidUntilAsync(string factId) =>
        await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (f:Fact {id: $id}) RETURN f.valid_until IS NOT NULL AS hasVu", new { id = factId });
            var records = await cursor.ToListAsync();
            return records.Count > 0 && global::Neo4j.Driver.ValueExtensions.As<bool>(records[0]["hasVu"]);
        });

    [Fact]
    public async Task UpsertAsync_ReAssertSupersededTriple_RestoresToLiveRecall_KeepsValidUntil()
    {
        // R5 HIGH: re-asserting a previously superseded triple is a present-time positive assertion; it must
        // become visible to live recall again. Before the fix, the triple-MERGE re-matched the dead node but
        // ON MATCH never cleared invalidated_at, so the fact stayed permanently invisible (write vanished).
        var idA = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = idA, Subject = "Alice", Predicate = "lives_in", Object = "Paris", Confidence = 0.9, Embedding = TestEmbedding, CreatedAtUtc = DateTimeOffset.UtcNow });
        var idB = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = idB, Subject = "Alice", Predicate = "lives_in", Object = "London", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        (await _repo.SupersedeAsync(idA, idB, scope: null)).Should().BeTrue();
        (await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5)).Select(r => r.Fact.FactId)
            .Should().NotContain(idA, "a superseded fact is invisible to live recall");

        // Re-assert the SAME triple (fresh id, no explicit ValidUntil) WITH an embedding.
        await _repo.UpsertAsync(new Fact { FactId = $"fact-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "lives_in", Object = "Paris", Confidence = 0.95, Embedding = TestEmbedding, CreatedAtUtc = DateTimeOffset.UtcNow });

        (await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5)).Select(r => r.Fact.FactId)
            .Should().Contain(idA, "re-asserting the triple must clear invalidated_at and restore the fact to live recall");
        (await HasValidUntilAsync(idA)).Should().BeTrue(
            "the fix clears only the transaction clock (invalidated_at); the valid-time clock (valid_until) stays as supersession stamped it");
    }

    [Fact]
    public async Task UpsertAsync_ReExtractSameTriple_WritesEmbeddingAndProvenance_OnMergedNodeId()
    {
        // R5 MED: the triple-MERGE keeps the original node id, but the embedding + EXTRACTED_FROM sub-writes
        // were keyed on the discarded caller id, so on re-extraction they targeted a non-existent node and
        // the new embedding/provenance was silently lost. They must follow the merged node id.
        var convRepo = new Neo4jConversationRepository(_fixture.TransactionRunner, NullLogger<Neo4jConversationRepository>.Instance);
        var msgRepo = new Neo4jMessageRepository(_fixture.TransactionRunner, NullLogger<Neo4jMessageRepository>.Instance);
        var conv = new Conversation { ConversationId = $"conv-{Guid.NewGuid():N}", SessionId = $"session-{Guid.NewGuid():N}", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await convRepo.UpsertAsync(conv);
        var msg = new Message { MessageId = $"msg-{Guid.NewGuid():N}", ConversationId = conv.ConversationId, SessionId = conv.SessionId, Role = "user", Content = "src", TimestampUtc = DateTimeOffset.UtcNow };
        await msgRepo.AddAsync(msg);

        // First extraction: degraded (no embedding, no sources).
        var idA = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = idA, Subject = "Alice", Predicate = "works_at", Object = "Acme", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        // Re-extraction of the SAME triple (fresh id) now carrying a real embedding + a source message.
        var idB = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = idB, Subject = "Alice", Predicate = "works_at", Object = "Acme", Confidence = 0.95, Embedding = TestEmbedding, SourceMessageIds = [msg.MessageId], CreatedAtUtc = DateTimeOffset.UtcNow });

        // Embedding landed on the surviving node (idA), so vector search finds it.
        (await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5)).Select(r => r.Fact.FactId)
            .Should().Contain(idA, "the re-extracted embedding must be written to the merged node, not the discarded id");

        // Provenance edge was created on the surviving node, not the orphan idB.
        var edgeCount = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $fid})-[:EXTRACTED_FROM]->(m:Message {id: $mid}) RETURN count(*) AS c",
                new { fid = idA, mid = msg.MessageId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });
        edgeCount.Should().Be(1, "EXTRACTED_FROM provenance must be created on the merged node id");
        (await _repo.GetByIdAsync(idB)).Should().BeNull("the discarded second id must never become a node");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsFact_WhenExists()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Bob",
            Predicate = "likes",
            Object = "Python",
            Confidence = 0.75,
            CreatedAtUtc = new DateTimeOffset(2025, 3, 10, 8, 0, 0, TimeSpan.Zero)
        };
        await _repo.UpsertAsync(fact);

        var result = await _repo.GetByIdAsync(fact.FactId);

        result.Should().NotBeNull();
        result!.FactId.Should().Be(fact.FactId);
        result.Subject.Should().Be("Bob");
        result.Predicate.Should().Be("likes");
        result.Object.Should().Be("Python");
        result.CreatedAtUtc.Should().BeCloseTo(fact.CreatedAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync("fact-does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_PersistsAndReadsBackCategory()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Dana",
            Predicate = "specializes_in",
            Object = "graph databases",
            Category = "professional",
            Confidence = 0.9,
            CreatedAtUtc = new DateTimeOffset(2025, 2, 2, 0, 0, 0, TimeSpan.Zero)
        };
        await _repo.UpsertAsync(fact);

        var result = await _repo.GetByIdAsync(fact.FactId);

        result.Should().NotBeNull();
        result!.Category.Should().Be("professional");
    }

    [Fact]
    public async Task UpsertBatchAsync_PersistsAndReadsBackCategory()
    {
        var subject = $"Subject-{Guid.NewGuid():N}";
        var facts = new[]
        {
            new Fact
            {
                FactId = $"fact-{Guid.NewGuid():N}",
                Subject = subject,
                Predicate = "born_in",
                Object = "Madrid",
                Category = "personal",
                Confidence = 0.8,
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };
        await _repo.UpsertBatchAsync(facts);

        var results = await _repo.GetBySubjectAsync(subject);

        results.Should().ContainSingle()
            .Which.Category.Should().Be("personal");
    }

    // ── R5 #10: batch path collapses duplicate triples like the single path ──

    private async Task<long> CountTripleAsync(string subject, string predicate, string @object) =>
        await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {subject: $s, predicate: $p, object: $o}) RETURN count(f) AS c",
                new { s = subject, p = predicate, o = @object });
            var records = await cursor.ToListAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(records[0]["c"]);
        });

    [Fact]
    public async Task UpsertBatchAsync_SameTripleDifferentIds_CollapsesToOneNode()
    {
        var subject = $"Subj-{Guid.NewGuid():N}";
        await _repo.UpsertBatchAsync(new[]
        {
            new Fact { FactId = $"fact-{Guid.NewGuid():N}", Subject = subject, Predicate = "works_at", Object = "Neo4j", Confidence = 0.8, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Fact { FactId = $"fact-{Guid.NewGuid():N}", Subject = subject, Predicate = "works_at", Object = "Neo4j", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow },
        });

        (await CountTripleAsync(subject, "works_at", "Neo4j")).Should().Be(1,
            "the batch path must merge same-triple inputs onto one node, like the single path");
    }

    [Fact]
    public async Task UpsertBatchAsync_SameTripleDifferentOwners_ProducesDistinctNodes()
    {
        var subject = $"Subj-{Guid.NewGuid():N}";
        await _repo.UpsertBatchAsync(new[]
        {
            new Fact { FactId = $"fact-{Guid.NewGuid():N}", Subject = subject, Predicate = "works_at", Object = "Neo4j", OwnerId = "alice", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Fact { FactId = $"fact-{Guid.NewGuid():N}", Subject = subject, Predicate = "works_at", Object = "Neo4j", OwnerId = "bob",   Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow },
            new Fact { FactId = $"fact-{Guid.NewGuid():N}", Subject = subject, Predicate = "works_at", Object = "Neo4j", OwnerId = null,    Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow },
        });

        (await CountTripleAsync(subject, "works_at", "Neo4j")).Should().Be(3,
            "owner_key keeps the same triple distinct per owner (and for shared/null), parity with the single path");
    }

    [Fact]
    public async Task CrossPath_SingleThenBatch_SameTriple_NoDuplicate_KeepsOriginalId()
    {
        var subject = $"Subj-{Guid.NewGuid():N}";
        var idA = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = idA, Subject = subject, Predicate = "works_at", Object = "Neo4j", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        // Re-extract the SAME triple via the BATCH surface with a fresh id.
        var idB = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertBatchAsync(new[]
        {
            new Fact { FactId = idB, Subject = subject, Predicate = "works_at", Object = "Neo4j", Confidence = 0.95, CreatedAtUtc = DateTimeOffset.UtcNow },
        });

        (await CountTripleAsync(subject, "works_at", "Neo4j")).Should().Be(1, "no duplicate across single+batch surfaces");
        (await _repo.GetByIdAsync(idA)).Should().NotBeNull("the original node id must survive the batch re-upsert");
        (await _repo.GetByIdAsync(idB)).Should().BeNull("the batch's fresh id must never become the node's id");
    }

    [Fact]
    public async Task UpsertBatchAsync_ReExtractSupersededTriple_DoesNotClearValidUntil()
    {
        // Mirror the single-path bitemporal test via the batch surface: the ON MATCH must COALESCE
        // valid_until (preserve the supersession window), not overwrite it to null.
        var loserId = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = loserId, Subject = "Carol", Predicate = "lives_in", Object = "Paris", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        var winnerId = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = winnerId, Subject = "Carol", Predicate = "lives_in", Object = "London", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        (await _repo.SupersedeAsync(loserId, winnerId, scope: null)).Should().BeTrue();
        (await HasValidUntilAsync(loserId)).Should().BeTrue();

        await _repo.UpsertBatchAsync(new[]
        {
            new Fact { FactId = $"fact-{Guid.NewGuid():N}", Subject = "Carol", Predicate = "lives_in", Object = "Paris", Confidence = 0.95, CreatedAtUtc = DateTimeOffset.UtcNow },
        });

        (await HasValidUntilAsync(loserId)).Should().BeTrue(
            "re-extracting a superseded triple via the batch path must NOT clear the valid_until supersession stamped");
    }

    [Fact]
    public async Task UpsertBatchAsync_ReExtractWithEmbedding_LandsOnSurvivingNode_NotDiscardedId()
    {
        // The key R5-A-class proof for the batch path: when the triple already exists (id A, no vector),
        // a batch re-upsert carrying a fresh id B AND a real embedding must write the vector onto node A —
        // not the discarded id B. Keying the sub-write on the caller id would silently no-op here.
        var subject = $"Subj-{Guid.NewGuid():N}";
        var idA = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertAsync(new Fact { FactId = idA, Subject = subject, Predicate = "works_at", Object = "Neo4j", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        var idB = $"fact-{Guid.NewGuid():N}";
        await _repo.UpsertBatchAsync(new[]
        {
            new Fact { FactId = idB, Subject = subject, Predicate = "works_at", Object = "Neo4j", Confidence = 0.95, Embedding = TestEmbedding, CreatedAtUtc = DateTimeOffset.UtcNow },
        });

        var hits = await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5);
        hits.Select(h => h.Fact.FactId).Should().Contain(idA,
            "the batch re-upsert's embedding must land on the surviving node so the fact becomes vector-searchable");
    }

    [Fact]
    public async Task UpsertAsync_NullCategory_RoundTripsAsNull()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Erin",
            Predicate = "drinks",
            Object = "tea",
            Category = null,
            Confidence = 0.7,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        var result = await _repo.GetByIdAsync(fact.FactId);

        result.Should().NotBeNull();
        result!.Category.Should().BeNull();
    }

    [Fact]
    public async Task GetBySubjectAsync_ReturnsFactsForSubject()
    {
        var subject = $"Subject-{Guid.NewGuid():N}";
        var fact1 = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = subject,
            Predicate = "works_at",
            Object = "Company A",
            Confidence = 0.8,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var fact2 = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = subject,
            Predicate = "lives_in",
            Object = "City B",
            Confidence = 0.7,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var other = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Someone Else",
            Predicate = "likes",
            Object = "Coffee",
            Confidence = 0.6,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await _repo.UpsertAsync(fact1);
        await _repo.UpsertAsync(fact2);
        await _repo.UpsertAsync(other);

        var results = await _repo.GetBySubjectAsync(subject);

        results.Should().HaveCount(2);
        results.Select(f => f.FactId).Should().BeEquivalentTo([fact1.FactId, fact2.FactId]);
    }

    [Fact]
    public async Task FindByTripleAsync_FindsFactBySPO_CaseInsensitive()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Charlie",
            Predicate = "knows",
            Object = "Diana",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        // Case-insensitive lookup
        var result = await _repo.FindByTripleAsync("CHARLIE", "KNOWS", "DIANA");

        result.Should().NotBeNull();
        result!.FactId.Should().Be(fact.FactId);
    }

    [Fact]
    public async Task FindByTripleAsync_ReturnsNull_WhenNoMatch()
    {
        var result = await _repo.FindByTripleAsync("Nobody", "does", "nothing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchByVectorAsync_ReturnsFacts_WhenEmbeddingMatches()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Eve",
            Predicate = "prefers",
            Object = "dark mode",
            Confidence = 0.85,
            Embedding = TestEmbedding,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        var results = await _repo.SearchByVectorAsync(QueryEmbedding, limit: 5);

        results.Should().NotBeEmpty();
        results[0].Fact.FactId.Should().Be(fact.FactId);
        results[0].Score.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFact()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Temp",
            Predicate = "to_be",
            Object = "deleted",
            Confidence = 0.5,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        var deleted = await _repo.DeleteAsync(fact.FactId);

        deleted.Should().BeTrue();
        var fetched = await _repo.GetByIdAsync(fact.FactId);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ForeignOwnerScope_DoesNotDeleteOtherOwnersFact()
    {
        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Alice", Predicate = "owns", Object = "Secret",
            OwnerId = "alice", Confidence = 0.8, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _repo.UpsertAsync(fact);

        (await _repo.DeleteAsync(fact.FactId, MemoryScope.For("bob"))).Should().BeFalse();
        (await _repo.GetByIdAsync(fact.FactId)).Should().NotBeNull("bob's scope must not delete alice's fact");

        (await _repo.DeleteAsync(fact.FactId, MemoryScope.For("alice"))).Should().BeTrue();
        (await _repo.GetByIdAsync(fact.FactId)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAboutRelationshipAsync_LinksFactToEntity()
    {
        var entityRepo = new Neo4jEntityRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jEntityRepository>.Instance);

        var entity = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = "Frank",
            Type = "Person",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await entityRepo.UpsertAsync(entity);

        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Frank",
            Predicate = "is_a",
            Object = "developer",
            Confidence = 0.8,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        await _repo.CreateAboutRelationshipAsync(fact.FactId, entity.EntityId);

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $fid})-[:ABOUT]->(e:Entity {id: $eid}) RETURN count(*) AS c",
                new { fid = fact.FactId, eid = entity.EntityId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1);
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_LinksFactToMessage()
    {
        var convRepo = new Neo4jConversationRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jConversationRepository>.Instance);
        var msgRepo = new Neo4jMessageRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jMessageRepository>.Instance);

        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await convRepo.UpsertAsync(conv);

        var msg = new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = conv.SessionId,
            Role = "user",
            Content = "Source for fact",
            TimestampUtc = DateTimeOffset.UtcNow
        };
        await msgRepo.AddAsync(msg);

        var fact = new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = "Grace",
            Predicate = "uses",
            Object = "Neo4j",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.UpsertAsync(fact);

        await _repo.CreateExtractedFromRelationshipAsync(fact.FactId, msg.MessageId);

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $fid})-[:EXTRACTED_FROM]->(m:Message {id: $mid}) RETURN count(*) AS c",
                new { fid = fact.FactId, mid = msg.MessageId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1);
    }
}
