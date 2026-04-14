using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Exceptions;

namespace Neo4j.AgentMemory.Tests.Unit.Exceptions;

public class MemoryExceptionTests
{
    // --- MemoryException (base) ---

    [Fact]
    public void MemoryException_MessageIsSet()
    {
        var ex = new MemoryException("test error");
        ex.Message.Should().Be("test error");
    }

    [Fact]
    public void MemoryException_InnerExceptionIsSet()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new MemoryException("outer", inner);
        ex.InnerException.Should().BeSameAs(inner);
        ex.Message.Should().Be("outer");
    }

    [Fact]
    public void MemoryException_InheritsFromException()
    {
        var ex = new MemoryException("test");
        ex.Should().BeAssignableTo<Exception>();
    }

    // --- EntityNotFoundException ---

    [Fact]
    public void EntityNotFoundException_SetsEntityId()
    {
        var ex = new EntityNotFoundException("ent-123");
        ex.EntityId.Should().Be("ent-123");
        ex.Message.Should().Contain("ent-123");
    }

    [Fact]
    public void EntityNotFoundException_InheritsFromMemoryException()
    {
        var ex = new EntityNotFoundException("ent-1");
        ex.Should().BeAssignableTo<MemoryException>();
    }

    [Fact]
    public void EntityNotFoundException_WithInnerException()
    {
        var inner = new Exception("db error");
        var ex = new EntityNotFoundException("ent-1", inner);
        ex.InnerException.Should().BeSameAs(inner);
        ex.EntityId.Should().Be("ent-1");
    }

    // --- FactNotFoundException ---

    [Fact]
    public void FactNotFoundException_SetsFactId()
    {
        var ex = new FactNotFoundException("fact-456");
        ex.FactId.Should().Be("fact-456");
        ex.Message.Should().Contain("fact-456");
    }

    [Fact]
    public void FactNotFoundException_InheritsFromMemoryException()
    {
        var ex = new FactNotFoundException("f1");
        ex.Should().BeAssignableTo<MemoryException>();
    }

    [Fact]
    public void FactNotFoundException_WithInnerException()
    {
        var inner = new Exception("not found");
        var ex = new FactNotFoundException("f1", inner);
        ex.InnerException.Should().BeSameAs(inner);
        ex.FactId.Should().Be("f1");
    }

    // --- SchemaInitializationException ---

    [Fact]
    public void SchemaInitializationException_MessageOnly()
    {
        var ex = new SchemaInitializationException("schema failed");
        ex.Message.Should().Be("schema failed");
        ex.SchemaOperation.Should().BeNull();
    }

    [Fact]
    public void SchemaInitializationException_WithSchemaOperation()
    {
        var ex = new SchemaInitializationException("schema failed", "CREATE INDEX");
        ex.SchemaOperation.Should().Be("CREATE INDEX");
    }

    [Fact]
    public void SchemaInitializationException_InheritsFromMemoryException()
    {
        var ex = new SchemaInitializationException("fail");
        ex.Should().BeAssignableTo<MemoryException>();
    }

    // --- ExtractionException ---

    [Fact]
    public void ExtractionException_WithExtractionStep()
    {
        var ex = new ExtractionException("extraction failed", "entity-parse");
        ex.ExtractionStep.Should().Be("entity-parse");
    }

    [Fact]
    public void ExtractionException_InheritsFromMemoryException()
    {
        var ex = new ExtractionException("fail");
        ex.Should().BeAssignableTo<MemoryException>();
    }

    // --- EmbeddingGenerationException ---

    [Fact]
    public void EmbeddingGenerationException_WithInputText()
    {
        var ex = new EmbeddingGenerationException("embed failed", "some text");
        ex.InputText.Should().Be("some text");
    }

    [Fact]
    public void EmbeddingGenerationException_InheritsFromMemoryException()
    {
        var ex = new EmbeddingGenerationException("fail");
        ex.Should().BeAssignableTo<MemoryException>();
    }

    [Fact]
    public void EmbeddingGenerationException_WithInnerException()
    {
        var inner = new HttpRequestException("timeout");
        var ex = new EmbeddingGenerationException("fail", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    // --- EntityResolutionException ---

    [Fact]
    public void EntityResolutionException_WithEntityName()
    {
        var ex = new EntityResolutionException("resolve failed", "John Doe");
        ex.EntityName.Should().Be("John Doe");
    }

    [Fact]
    public void EntityResolutionException_InheritsFromMemoryException()
    {
        var ex = new EntityResolutionException("fail");
        ex.Should().BeAssignableTo<MemoryException>();
    }

    // --- MemoryConfigurationException ---

    [Fact]
    public void MemoryConfigurationException_WithOptionName()
    {
        var ex = new MemoryConfigurationException("invalid config", "MaxTokens");
        ex.OptionName.Should().Be("MaxTokens");
    }

    [Fact]
    public void MemoryConfigurationException_InheritsFromMemoryException()
    {
        var ex = new MemoryConfigurationException("fail");
        ex.Should().BeAssignableTo<MemoryException>();
    }

    // --- GraphQueryException ---

    [Fact]
    public void GraphQueryException_WithCypherQuery()
    {
        var ex = new GraphQueryException("query failed", "MATCH (n) RETURN n");
        ex.CypherQuery.Should().Be("MATCH (n) RETURN n");
    }

    [Fact]
    public void GraphQueryException_InheritsFromMemoryException()
    {
        var ex = new GraphQueryException("fail");
        ex.Should().BeAssignableTo<MemoryException>();
    }

    [Fact]
    public void GraphQueryException_WithInnerException()
    {
        var inner = new Exception("connection lost");
        var ex = new GraphQueryException("fail", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }

    // --- All exceptions are catchable as MemoryException ---

    [Fact]
    public void AllExceptions_CatchableAsMemoryException()
    {
        var exceptions = new MemoryException[]
        {
            new EntityNotFoundException("e1"),
            new FactNotFoundException("f1"),
            new SchemaInitializationException("s1"),
            new ExtractionException("x1"),
            new EmbeddingGenerationException("g1"),
            new EntityResolutionException("r1"),
            new MemoryConfigurationException("c1"),
            new GraphQueryException("q1")
        };

        foreach (var ex in exceptions)
        {
            ex.Should().BeAssignableTo<MemoryException>();
            ex.Should().BeAssignableTo<Exception>();
        }
    }
}
