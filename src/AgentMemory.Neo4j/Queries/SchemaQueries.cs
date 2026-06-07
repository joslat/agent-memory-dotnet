namespace AgentMemory.Neo4j.Queries;

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

    /// <summary>Unique constraint on ConsolidationRun.id (memory-hygiene audit, PR #113).</summary>
    public const string ConsolidationRunIdConstraint = "CREATE CONSTRAINT consolidation_run_id IF NOT EXISTS FOR (r:ConsolidationRun) REQUIRE r.id IS UNIQUE";

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
        ExtractorNameConstraint,
        ConsolidationRunIdConstraint
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

    // ── Owner-scope property indexes (R1, multi-user isolation) ──
    // owner_id is nullable; NULL = shared/global. These indexes accelerate the owner filter
    // applied during scoped vector recall (see {Fact,Entity,Preference}Queries.SearchByVector).

    /// <summary>Index on Fact.owner_id (multi-user scope).</summary>
    public const string FactOwnerIndex = "CREATE INDEX fact_owner_idx IF NOT EXISTS FOR (f:Fact) ON (f.owner_id)";

    /// <summary>Index on Entity.owner_id (multi-user scope).</summary>
    public const string EntityOwnerIndex = "CREATE INDEX entity_owner_idx IF NOT EXISTS FOR (e:Entity) ON (e.owner_id)";

    /// <summary>Index on Preference.owner_id (multi-user scope).</summary>
    public const string PreferenceOwnerIndex = "CREATE INDEX preference_owner_idx IF NOT EXISTS FOR (p:Preference) ON (p.owner_id)";

    /// <summary>Index on ReasoningTrace.owner_id (multi-user scope; trace owner-write + read-filter shipped in R2).</summary>
    public const string TraceOwnerIndex = "CREATE INDEX trace_owner_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.owner_id)";

    /// <summary>Relationship-property index on the RELATED_TO edge's owner_id (multi-user scope).</summary>
    public const string RelationshipOwnerIndex = "CREATE INDEX rel_owner_idx IF NOT EXISTS FOR ()-[r:RELATED_TO]-() ON (r.owner_id)";

    /// <summary>Index on Conversation.archived (memory-hygiene / consolidation, PR #113).</summary>
    public const string ConversationArchivedIndex = "CREATE INDEX conversation_archived_idx IF NOT EXISTS FOR (c:Conversation) ON (c.archived)";

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
        EntityLocationIndex,
        FactOwnerIndex,
        EntityOwnerIndex,
        PreferenceOwnerIndex,
        TraceOwnerIndex,
        RelationshipOwnerIndex,
        ConversationArchivedIndex
    ];

    // ── Vector Indexes (parameterized by dimensions) ────────────

    /// <summary>
    /// Builds the set of vector index CREATE statements for the given embedding dimensions.
    /// </summary>
    public static string[] BuildVectorIndexes(int dimensions) =>
        dimensions > 0
        ?
        [
        $"CREATE VECTOR INDEX message_embedding_idx IF NOT EXISTS FOR (n:Message) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX entity_embedding_idx IF NOT EXISTS FOR (n:Entity) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX preference_embedding_idx IF NOT EXISTS FOR (n:Preference) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX fact_embedding_idx IF NOT EXISTS FOR (n:Fact) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX reasoning_step_embedding_idx IF NOT EXISTS FOR (n:ReasoningStep) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}",
        $"CREATE VECTOR INDEX task_embedding_idx IF NOT EXISTS FOR (n:ReasoningTrace) ON (n.task_embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: {dimensions}, `vector.similarity_function`: 'cosine'}}}}"
        ]
        : throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Embedding dimensions must be a positive integer.");

    /// <summary>
    /// The full ordered set of schema statements (constraints → fulltext → vector → property indexes)
    /// for a given embedding dimensionality. Used to bootstrap a freshly provisioned per-application
    /// database (R1b). Not a query constant, so it is excluded from the Cypher snapshot inventory.
    /// </summary>
    public static IReadOnlyList<string> BootstrapStatements(int dimensions)
    {
        var statements = new List<string>(
            Constraints.Length + FulltextIndexes.Length + PropertyIndexes.Length + 6);
        statements.AddRange(Constraints);
        statements.AddRange(FulltextIndexes);
        statements.AddRange(BuildVectorIndexes(dimensions));
        statements.AddRange(PropertyIndexes);
        return statements;
    }

    // ── Vector-index validation ─────────────────────────────────

    /// <summary>
    /// Lists every vector index with the dimensionality it was created with. Used at bootstrap to
    /// fail-fast when an existing index's dimensions no longer match the configured embedder
    /// (<c>CREATE VECTOR INDEX ... IF NOT EXISTS</c> never alters an existing index).
    /// </summary>
    public const string ShowVectorIndexDimensions =
        "SHOW VECTOR INDEXES YIELD name, options " +
        "RETURN name AS name, options['indexConfig']['vector.dimensions'] AS dimensions";

    // ── Migration ───────────────────────────────────────────────

    /// <summary>Unique constraint on Migration.version for tracking applied migrations.</summary>
    public const string MigrationVersionConstraint = "CREATE CONSTRAINT migration_version IF NOT EXISTS FOR (m:Migration) REQUIRE m.version IS UNIQUE";

    /// <summary>Check whether a migration has already been applied.</summary>
    public const string IsMigrationApplied = "MATCH (m:Migration {version: $version}) RETURN m LIMIT 1";

    /// <summary>Record a migration as applied.</summary>
    public const string RecordMigration = "MERGE (m:Migration {version: $version}) SET m.appliedAtUtc = $appliedAtUtc";
}
