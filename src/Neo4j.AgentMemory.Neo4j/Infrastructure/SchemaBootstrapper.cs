using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public sealed class SchemaBootstrapper : ISchemaBootstrapper
{
    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<SchemaBootstrapper> _logger;
    private readonly string[] _vectorIndexes;

    private static readonly string[] Constraints =
    [
        "CREATE CONSTRAINT conversation_id IF NOT EXISTS FOR (c:Conversation) REQUIRE c.id IS UNIQUE",
        "CREATE CONSTRAINT message_id IF NOT EXISTS FOR (m:Message) REQUIRE m.id IS UNIQUE",
        "CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (e:Entity) REQUIRE e.id IS UNIQUE",
        "CREATE CONSTRAINT fact_id IF NOT EXISTS FOR (f:Fact) REQUIRE f.id IS UNIQUE",
        "CREATE CONSTRAINT preference_id IF NOT EXISTS FOR (p:Preference) REQUIRE p.id IS UNIQUE",
        "CREATE CONSTRAINT reasoning_trace_id IF NOT EXISTS FOR (t:ReasoningTrace) REQUIRE t.id IS UNIQUE",
        "CREATE CONSTRAINT reasoning_step_id IF NOT EXISTS FOR (s:ReasoningStep) REQUIRE s.id IS UNIQUE",
        "CREATE CONSTRAINT tool_call_id IF NOT EXISTS FOR (tc:ToolCall) REQUIRE tc.id IS UNIQUE",
        "CREATE CONSTRAINT tool_name IF NOT EXISTS FOR (t:Tool) REQUIRE t.name IS UNIQUE",
        "CREATE CONSTRAINT extractor_name IF NOT EXISTS FOR (ex:Extractor) REQUIRE ex.name IS UNIQUE"
    ];

    private static readonly string[] FulltextIndexes =
    [
        "CREATE FULLTEXT INDEX message_content IF NOT EXISTS FOR (m:Message) ON EACH [m.content]",
        "CREATE FULLTEXT INDEX entity_name IF NOT EXISTS FOR (e:Entity) ON EACH [e.name, e.description]",
        "CREATE FULLTEXT INDEX fact_content IF NOT EXISTS FOR (f:Fact) ON EACH [f.subject, f.predicate, f.object]"
    ];

    private static readonly string[] PropertyIndexes =
    [
        "CREATE INDEX conversation_session_idx IF NOT EXISTS FOR (c:Conversation) ON (c.session_id)",
        "CREATE INDEX message_timestamp_idx IF NOT EXISTS FOR (m:Message) ON (m.timestamp)",
        "CREATE INDEX message_role_idx IF NOT EXISTS FOR (m:Message) ON (m.role)",
        "CREATE INDEX entity_type_idx IF NOT EXISTS FOR (e:Entity) ON (e.type)",
        "CREATE INDEX entity_name_idx IF NOT EXISTS FOR (e:Entity) ON (e.name)",
        "CREATE INDEX entity_canonical_idx IF NOT EXISTS FOR (e:Entity) ON (e.canonical_name)",
        "CREATE INDEX fact_category IF NOT EXISTS FOR (f:Fact) ON (f.category)",
        "CREATE INDEX preference_category_idx IF NOT EXISTS FOR (p:Preference) ON (p.category)",
        "CREATE INDEX trace_session_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.session_id)",
        "CREATE INDEX trace_success_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.success)",
        "CREATE INDEX reasoning_step_timestamp IF NOT EXISTS FOR (s:ReasoningStep) ON (s.timestamp)",
        "CREATE INDEX tool_call_status_idx IF NOT EXISTS FOR (tc:ToolCall) ON (tc.status)",
        "CREATE INDEX schema_name_idx IF NOT EXISTS FOR (s:Schema) ON (s.name)",
        "CREATE INDEX schema_version_idx IF NOT EXISTS FOR (s:Schema) ON (s.version)",
        "CREATE POINT INDEX entity_location_idx IF NOT EXISTS FOR (e:Entity) ON (e.location)"
    ];

    public SchemaBootstrapper(
        INeo4jTransactionRunner txRunner,
        IOptions<Neo4jOptions> options,
        ILogger<SchemaBootstrapper> logger)
    {
        _txRunner = txRunner;
        _logger = logger;

        var dims = options.Value.EmbeddingDimensions;
        _vectorIndexes = BuildVectorIndexes(dims);
    }

    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Running schema bootstrap: {ConstraintCount} constraints, {FulltextCount} fulltext indexes, " +
            "{VectorCount} vector indexes, {PropertyCount} property indexes.",
            Constraints.Length, FulltextIndexes.Length, _vectorIndexes.Length, PropertyIndexes.Length);

        foreach (var constraint in Constraints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(constraint, cancellationToken);
        }

        foreach (var index in FulltextIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken);
        }

        foreach (var index in _vectorIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken);
        }

        foreach (var index in PropertyIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken);
        }

        _logger.LogInformation("Schema bootstrap complete.");
    }

    public static string[] BuildVectorIndexes(int dimensions) =>
    [
        $"CREATE VECTOR INDEX message_embedding_idx IF NOT EXISTS FOR (n:Message) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX entity_embedding_idx IF NOT EXISTS FOR (n:Entity) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX preference_embedding_idx IF NOT EXISTS FOR (n:Preference) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX fact_embedding_idx IF NOT EXISTS FOR (n:Fact) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX reasoning_step_embedding_idx IF NOT EXISTS FOR (n:ReasoningStep) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX task_embedding_idx IF NOT EXISTS FOR (n:ReasoningTrace) ON (n.task_embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}"
    ];

    private async Task RunStatementAsync(string cypher, CancellationToken cancellationToken)
    {
        try
        {
            await _txRunner.WriteAsync(
                async tx => { await tx.RunAsync(cypher); },
                cancellationToken);

            _logger.LogDebug("Executed schema statement: {Cypher}", cypher);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute schema statement: {Cypher}", cypher);
            throw;
        }
    }
}
