using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Exceptions;

namespace Neo4j.AgentMemory.Tests.Unit.Exceptions;

public class MemoryErrorBuilderTests
{
    // ── MemoryErrorCodes ──────────────────────────────────────────────────────────

    [Fact]
    public void AllErrorCodes_FollowNamingConvention()
    {
        var codeFields = typeof(MemoryErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string));

        foreach (var field in codeFields)
        {
            var value = (string)field.GetValue(null)!;
            value.Should().StartWith("MEMORY_", because: $"{field.Name} must start with MEMORY_");
            value.Should().MatchRegex("^[A-Z_]+$", because: $"{field.Name} must be UPPER_SNAKE_CASE");
        }
    }

    [Fact]
    public void ErrorCodes_HaveExpectedValues()
    {
        MemoryErrorCodes.EntityNotFound.Should().Be("MEMORY_ENTITY_NOT_FOUND");
        MemoryErrorCodes.FactNotFound.Should().Be("MEMORY_FACT_NOT_FOUND");
        MemoryErrorCodes.PreferenceNotFound.Should().Be("MEMORY_PREFERENCE_NOT_FOUND");
        MemoryErrorCodes.TraceNotFound.Should().Be("MEMORY_TRACE_NOT_FOUND");
        MemoryErrorCodes.DuplicateEntity.Should().Be("MEMORY_DUPLICATE_ENTITY");
        MemoryErrorCodes.EmbeddingFailed.Should().Be("MEMORY_EMBEDDING_FAILED");
        MemoryErrorCodes.ExtractionFailed.Should().Be("MEMORY_EXTRACTION_FAILED");
        MemoryErrorCodes.SchemaBootstrapFailed.Should().Be("MEMORY_SCHEMA_BOOTSTRAP_FAILED");
        MemoryErrorCodes.TransactionFailed.Should().Be("MEMORY_TRANSACTION_FAILED");
        MemoryErrorCodes.QueryFailed.Should().Be("MEMORY_QUERY_FAILED");
        MemoryErrorCodes.ValidationFailed.Should().Be("MEMORY_VALIDATION_FAILED");
        MemoryErrorCodes.ContextBudgetExceeded.Should().Be("MEMORY_CONTEXT_BUDGET_EXCEEDED");
        MemoryErrorCodes.ConfigurationInvalid.Should().Be("MEMORY_CONFIGURATION_INVALID");
        MemoryErrorCodes.ResolutionFailed.Should().Be("MEMORY_RESOLUTION_FAILED");
        MemoryErrorCodes.PersistenceFailed.Should().Be("MEMORY_PERSISTENCE_FAILED");
    }

    // ── MemoryException (builder-created) ────────────────────────────────────────

    [Fact]
    public void MemoryException_ViaBuilder_SetsMessage()
    {
        var ex = MemoryError.Create("something went wrong").Build();

        ex.Message.Should().Be("something went wrong");
        ex.Should().BeAssignableTo<MemoryException>();
        ex.Should().BeAssignableTo<Exception>();
    }

    [Fact]
    public void MemoryException_ViaBuilder_CodeIsNullWhenNotSet()
    {
        var ex = MemoryError.Create("no code").Build();
        ex.Code.Should().BeNull();
    }

    [Fact]
    public void MemoryException_ViaBuilder_MetadataIsEmptyWhenNoneAdded()
    {
        var ex = MemoryError.Create("no metadata").Build();
        ex.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void MemoryException_DirectConstructor_CodeIsNull()
    {
        var ex = new MemoryException("direct");
        ex.Code.Should().BeNull();
        ex.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void MemoryException_DirectConstructor_WithInner_CodeIsNull()
    {
        var inner = new Exception("inner");
        var ex = new MemoryException("outer", inner);
        ex.Code.Should().BeNull();
        ex.InnerException.Should().BeSameAs(inner);
        ex.Metadata.Should().BeEmpty();
    }

    // ── MemoryErrorBuilder fluent API ─────────────────────────────────────────────

    [Fact]
    public void WithCode_SetsCode()
    {
        var ex = MemoryError.Create("error")
            .WithCode(MemoryErrorCodes.EntityNotFound)
            .Build();

        ex.Code.Should().Be(MemoryErrorCodes.EntityNotFound);
    }

    [Fact]
    public void WithEntityId_AddsEntityIdToMetadata()
    {
        var ex = MemoryError.Create("not found")
            .WithEntityId("ent-42")
            .Build();

        ex.Metadata.Should().ContainKey("entityId").WhoseValue.Should().Be("ent-42");
    }

    [Fact]
    public void WithQuery_AddsQueryToMetadata()
    {
        const string cypher = "MATCH (n:Entity {id: $id}) RETURN n";
        var ex = MemoryError.Create("query failed")
            .WithQuery(cypher)
            .Build();

        ex.Metadata.Should().ContainKey("query").WhoseValue.Should().Be(cypher);
    }

    [Fact]
    public void WithSessionId_AddsSessionIdToMetadata()
    {
        var ex = MemoryError.Create("session error")
            .WithSessionId("session-99")
            .Build();

        ex.Metadata.Should().ContainKey("sessionId").WhoseValue.Should().Be("session-99");
    }

    [Fact]
    public void WithInner_SetsInnerException()
    {
        var inner = new TimeoutException("db timeout");
        var ex = MemoryError.Create("operation failed")
            .WithInner(inner)
            .Build();

        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void WithMetadata_AddsArbitraryKeyValue()
    {
        var ex = MemoryError.Create("enriched error")
            .WithMetadata("batchSize", 100)
            .WithMetadata("nodeLabel", "Entity")
            .WithMetadata("nullValue", (object?)null)
            .Build();

        ex.Metadata.Should().HaveCount(3);
        ex.Metadata["batchSize"].Should().Be(100);
        ex.Metadata["nodeLabel"].Should().Be("Entity");
        ex.Metadata["nullValue"].Should().BeNull();
    }

    [Fact]
    public void WithMetadata_LastWriteWins_WhenKeyDuplicated()
    {
        var ex = MemoryError.Create("overwrite test")
            .WithMetadata("key", "first")
            .WithMetadata("key", "second")
            .Build();

        ex.Metadata["key"].Should().Be("second");
    }

    [Fact]
    public void FullChain_AllWithMethods_ProducesCorrectException()
    {
        var inner = new Exception("root cause");

        var ex = MemoryError.Create("Entity not found in graph.")
            .WithCode(MemoryErrorCodes.EntityNotFound)
            .WithEntityId("ent-007")
            .WithSessionId("sess-abc")
            .WithQuery("MATCH (e:Entity {id: $id}) RETURN e")
            .WithInner(inner)
            .WithMetadata("retries", 3)
            .Build();

        ex.Message.Should().Be("Entity not found in graph.");
        ex.Code.Should().Be(MemoryErrorCodes.EntityNotFound);
        ex.InnerException.Should().BeSameAs(inner);
        ex.Metadata.Should().ContainKey("entityId").WhoseValue.Should().Be("ent-007");
        ex.Metadata.Should().ContainKey("sessionId").WhoseValue.Should().Be("sess-abc");
        ex.Metadata.Should().ContainKey("query");
        ex.Metadata.Should().ContainKey("retries").WhoseValue.Should().Be(3);
    }

    [Fact]
    public void Build_ReturnsDifferentInstanceEachCall()
    {
        var builder = MemoryError.Create("same message").WithCode(MemoryErrorCodes.QueryFailed);
        var ex1 = builder.Build();
        var ex2 = builder.Build();

        ex1.Should().NotBeSameAs(ex2);
        ex1.Code.Should().Be(ex2.Code);
        ex1.Message.Should().Be(ex2.Message);
    }

    [Fact]
    public void Metadata_IsImmutableFromOutside()
    {
        var ex = MemoryError.Create("test").WithMetadata("k", "v").Build();

        // IReadOnlyDictionary: should not expose Add/Remove methods to callers
        ex.Metadata.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>();
    }

    // ── Throw() helper ────────────────────────────────────────────────────────────

    [Fact]
    public void Throw_ThrowsMemoryException()
    {
        var act = () => MemoryError.Create("thrown directly")
            .WithCode(MemoryErrorCodes.ExtractionFailed)
            .Throw();

        act.Should().Throw<MemoryException>()
            .WithMessage("thrown directly")
            .Which.Code.Should().Be(MemoryErrorCodes.ExtractionFailed);
    }

    // ── MemoryError.Create factory ────────────────────────────────────────────────

    [Fact]
    public void Create_ReturnsNewBuilderEachTime()
    {
        var b1 = MemoryError.Create("msg1");
        var b2 = MemoryError.Create("msg2");

        b1.Should().NotBeSameAs(b2);
        b1.Build().Message.Should().Be("msg1");
        b2.Build().Message.Should().Be("msg2");
    }
}
