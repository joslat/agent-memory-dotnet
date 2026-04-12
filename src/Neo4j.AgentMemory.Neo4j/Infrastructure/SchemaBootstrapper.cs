using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public sealed class SchemaBootstrapper : ISchemaBootstrapper
{
    private readonly INeo4jTransactionRunner _txRunner;
    private readonly ILogger<SchemaBootstrapper> _logger;

    private static readonly string[] Constraints =
    [
        "CREATE CONSTRAINT conversation_id IF NOT EXISTS FOR (c:Conversation) REQUIRE c.id IS UNIQUE",
        "CREATE CONSTRAINT message_id IF NOT EXISTS FOR (m:Message) REQUIRE m.id IS UNIQUE",
        "CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (e:Entity) REQUIRE e.id IS UNIQUE",
        "CREATE CONSTRAINT fact_id IF NOT EXISTS FOR (f:Fact) REQUIRE f.id IS UNIQUE",
        "CREATE CONSTRAINT preference_id IF NOT EXISTS FOR (p:Preference) REQUIRE p.id IS UNIQUE",
        "CREATE CONSTRAINT relationship_id IF NOT EXISTS FOR (r:MemoryRelationship) REQUIRE r.id IS UNIQUE",
        "CREATE CONSTRAINT reasoning_trace_id IF NOT EXISTS FOR (t:ReasoningTrace) REQUIRE t.id IS UNIQUE",
        "CREATE CONSTRAINT reasoning_step_id IF NOT EXISTS FOR (s:ReasoningStep) REQUIRE s.id IS UNIQUE",
        "CREATE CONSTRAINT tool_call_id IF NOT EXISTS FOR (tc:ToolCall) REQUIRE tc.id IS UNIQUE"
    ];

    private static readonly string[] Indexes =
    [
        "CREATE FULLTEXT INDEX message_content IF NOT EXISTS FOR (m:Message) ON EACH [m.content]",
        "CREATE FULLTEXT INDEX entity_name IF NOT EXISTS FOR (e:Entity) ON EACH [e.name, e.description]",
        "CREATE FULLTEXT INDEX fact_content IF NOT EXISTS FOR (f:Fact) ON EACH [f.subject, f.predicate, f.object]"
    ];

    public SchemaBootstrapper(INeo4jTransactionRunner txRunner, ILogger<SchemaBootstrapper> logger)
    {
        _txRunner = txRunner;
        _logger = logger;
    }

    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Running schema bootstrap: {ConstraintCount} constraints, {IndexCount} indexes.",
            Constraints.Length, Indexes.Length);

        foreach (var constraint in Constraints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(constraint, cancellationToken);
        }

        foreach (var index in Indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunStatementAsync(index, cancellationToken);
        }

        _logger.LogInformation("Schema bootstrap complete.");
    }

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
