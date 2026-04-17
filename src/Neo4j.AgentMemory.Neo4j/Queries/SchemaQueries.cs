namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher statements for schema bootstrapping (constraints, indexes)
/// and migration tracking.
/// </summary>
public static class SchemaQueries
{
    // ── Constraints ─────────────────────────────────────────────

    /// <summary>Unique constraint on Conversation.id.</summary>
    public const string ConversationIdConstraint = "CREATE CONSTRAINT conversation_id IF NOT EXISTS FOR (c:Conversation) REQUIRE c.id IS UNIQUE";

    /// <summary>Unique constraint on Message.id.</summary>
    public const string MessageIdConstraint = "CREATE CONSTRAINT message_id IF NOT EXISTS FOR (m:Message) REQUIRE m.id IS UNIQUE";

    /// <summary>Unique constraint on Entity.id.</summary>
    public const string EntityIdConstraint = "CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (e:Entity) REQUIRE e.id IS UNIQUE";

    /// <summary>Unique constraint on Fact.id.</summary>
    public const string FactIdConstraint = "CREATE CONSTRAINT fact_id IF NOT EXISTS FOR (f:Fact) REQUIRE f.id IS UNIQUE";

    /// <summary>Unique constraint on Preference.id.</summary>
    public const string PreferenceIdConstraint = "CREATE CONSTRAINT preference_id IF NOT EXISTS FOR (p:Preference) REQUIRE p.id IS UNIQUE";

    /// <summary>Unique constraint on ReasoningTrace.id.</summary>
    public const string ReasoningTraceIdConstraint = "CREATE CONSTRAINT reasoning_trace_id IF NOT EXISTS FOR (t:ReasoningTrace) REQUIRE t.id IS UNIQUE";

    /// <summary>Unique constraint on ReasoningStep.id.</summary>
    public const string ReasoningStepIdConstraint = "CREATE CONSTRAINT reasoning_step_id IF NOT EXISTS FOR (s:ReasoningStep) REQUIRE s.id IS UNIQUE";

    /// <summary>Unique constraint on ToolCall.id.</summary>
    public const string ToolCallIdConstraint = "CREATE CONSTRAINT tool_call_id IF NOT EXISTS FOR (tc:ToolCall) REQUIRE tc.id IS UNIQUE";

    /// <summary>Unique constraint on Tool.name.</summary>
    public const string ToolNameConstraint = "CREATE CONSTRAINT tool_name IF NOT EXISTS FOR (t:Tool) REQUIRE t.name IS UNIQUE";

    /// <summary>Unique constraint on Extractor.name.</summary>
    public const string ExtractorNameConstraint = "CREATE CONSTRAINT extractor_name IF NOT EXISTS FOR (ex:Extractor) REQUIRE ex.name IS UNIQUE";

    /// <summary>All uniqueness constraints in bootstrap order.</summary>
    public static readonly string[] Constraints =
    [
        ConversationIdConstraint,
        MessageIdConstraint,
        EntityIdConstraint,
        FactIdConstraint,
        PreferenceIdConstraint,
        ReasoningTraceIdConstraint,
        ReasoningStepIdConstraint,
        ToolCallIdConstraint,
        ToolNameConstraint,
        ExtractorNameConstraint
    ];

    // ── Fulltext Indexes ────────────────────────────────────────

    /// <summary>Fulltext index on Message.content.</summary>
    public const string MessageContentFulltext = "CREATE FULLTEXT INDEX message_content IF NOT EXISTS FOR (m:Message) ON EACH [m.content]";

    /// <summary>Fulltext index on Entity.name and Entity.description.</summary>
    public const string EntityNameFulltext = "CREATE FULLTEXT INDEX entity_name IF NOT EXISTS FOR (e:Entity) ON EACH [e.name, e.description]";

    /// <summary>Fulltext index on Fact.subject, Fact.predicate, and Fact.object.</summary>
    public const string FactContentFulltext = "CREATE FULLTEXT INDEX fact_content IF NOT EXISTS FOR (f:Fact) ON EACH [f.subject, f.predicate, f.object]";

    /// <summary>All fulltext indexes in bootstrap order.</summary>
    public static readonly string[] FulltextIndexes =
    [
        MessageContentFulltext,
        EntityNameFulltext,
        FactContentFulltext
    ];

    // ── Property Indexes ────────────────────────────────────────

    /// <summary>Index on Conversation.session_id.</summary>
    public const string ConversationSessionIndex = "CREATE INDEX conversation_session_idx IF NOT EXISTS FOR (c:Conversation) ON (c.session_id)";

    /// <summary>Index on Message.timestamp.</summary>
    public const string MessageTimestampIndex = "CREATE INDEX message_timestamp_idx IF NOT EXISTS FOR (m:Message) ON (m.timestamp)";

    /// <summary>Index on Message.role.</summary>
    public const string MessageRoleIndex = "CREATE INDEX message_role_idx IF NOT EXISTS FOR (m:Message) ON (m.role)";

    /// <summary>Index on Entity.type.</summary>
    public const string EntityTypeIndex = "CREATE INDEX entity_type_idx IF NOT EXISTS FOR (e:Entity) ON (e.type)";

    /// <summary>Index on Entity.name.</summary>
    public const string EntityNameIndex = "CREATE INDEX entity_name_idx IF NOT EXISTS FOR (e:Entity) ON (e.name)";

    /// <summary>Index on Entity.canonical_name.</summary>
    public const string EntityCanonicalIndex = "CREATE INDEX entity_canonical_idx IF NOT EXISTS FOR (e:Entity) ON (e.canonical_name)";

    /// <summary>Index on Fact.category.</summary>
    public const string FactCategoryIndex = "CREATE INDEX fact_category IF NOT EXISTS FOR (f:Fact) ON (f.category)";

    /// <summary>Index on Preference.category.</summary>
    public const string PreferenceCategoryIndex = "CREATE INDEX preference_category_idx IF NOT EXISTS FOR (p:Preference) ON (p.category)";

    /// <summary>Index on ReasoningTrace.session_id.</summary>
    public const string TraceSessionIndex = "CREATE INDEX trace_session_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.session_id)";

    /// <summary>Index on ReasoningTrace.success.</summary>
    public const string TraceSuccessIndex = "CREATE INDEX trace_success_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.success)";

    /// <summary>Index on ReasoningStep.timestamp.</summary>
    public const string ReasoningStepTimestampIndex = "CREATE INDEX reasoning_step_timestamp IF NOT EXISTS FOR (s:ReasoningStep) ON (s.timestamp)";

    /// <summary>Index on ToolCall.status.</summary>
    public const string ToolCallStatusIndex = "CREATE INDEX tool_call_status_idx IF NOT EXISTS FOR (tc:ToolCall) ON (tc.status)";

    /// <summary>Index on Schema.name.</summary>
    public const string SchemaNameIndex = "CREATE INDEX schema_name_idx IF NOT EXISTS FOR (s:Schema) ON (s.name)";

    /// <summary>Index on Schema.version.</summary>
    public const string SchemaVersionIndex = "CREATE INDEX schema_version_idx IF NOT EXISTS FOR (s:Schema) ON (s.version)";

    /// <summary>Point index on Entity.location.</summary>
    public const string EntityLocationIndex = "CREATE POINT INDEX entity_location_idx IF NOT EXISTS FOR (e:Entity) ON (e.location)";

    /// <summary>All property indexes in bootstrap order.</summary>
    public static readonly string[] PropertyIndexes =
    [
        ConversationSessionIndex,
        MessageTimestampIndex,
        MessageRoleIndex,
        EntityTypeIndex,
        EntityNameIndex,
        EntityCanonicalIndex,
        FactCategoryIndex,
        PreferenceCategoryIndex,
        TraceSessionIndex,
        TraceSuccessIndex,
        ReasoningStepTimestampIndex,
        ToolCallStatusIndex,
        SchemaNameIndex,
        SchemaVersionIndex,
        EntityLocationIndex
    ];

    // ── Vector Indexes (parameterized by dimensions) ────────────

    /// <summary>
    /// Builds the set of vector index CREATE statements for the given embedding dimensions.
    /// </summary>
    public static string[] BuildVectorIndexes(int dimensions) =>
    [
        $"CREATE VECTOR INDEX message_embedding_idx IF NOT EXISTS FOR (n:Message) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX entity_embedding_idx IF NOT EXISTS FOR (n:Entity) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX preference_embedding_idx IF NOT EXISTS FOR (n:Preference) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX fact_embedding_idx IF NOT EXISTS FOR (n:Fact) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX reasoning_step_embedding_idx IF NOT EXISTS FOR (n:ReasoningStep) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX task_embedding_idx IF NOT EXISTS FOR (n:ReasoningTrace) ON (n.task_embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}"
    ];

    // ── Migration ───────────────────────────────────────────────

    /// <summary>Unique constraint on Migration.version for tracking applied migrations.</summary>
    public const string MigrationVersionConstraint = "CREATE CONSTRAINT migration_version IF NOT EXISTS FOR (m:Migration) REQUIRE m.version IS UNIQUE";

    /// <summary>Check whether a migration has already been applied.</summary>
    public const string IsMigrationApplied = "MATCH (m:Migration {version: $version}) RETURN m LIMIT 1";

    /// <summary>Record a migration as applied.</summary>
    public const string RecordMigration = "MERGE (m:Migration {version: $version}) SET m.appliedAtUtc = $appliedAtUtc";
}
