namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher statements for schema bootstrapping (constraints, indexes)
/// and migration tracking.
/// </summary>
internal static class SchemaQueries
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

    /// <summary>Unique constraint on MemoryReadAudit.id (read/privacy audit, upstream v0.5-compatible).</summary>
    public const string MemoryReadAuditIdConstraint = "CREATE CONSTRAINT memory_read_audit_id IF NOT EXISTS FOR (a:MemoryReadAudit) REQUIRE a.id IS UNIQUE";

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
        ConsolidationRunIdConstraint,
        MemoryReadAuditIdConstraint
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

    /// <summary>
    /// Index on Message.session_id — the property the hot session reads actually filter.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not assumed.</b> The composite <c>(session_id, timestamp)</c> below does NOT serve
    /// these queries. Neo4j will not seek a composite from a leading-column predicate alone — asking
    /// for it by hint returns <i>"Must use the properties session_id, timestamp … but only session_id
    /// was found"</i>, and <c>ORDER BY timestamp</c> is not a predicate. Verified on 5.26 with 20,000
    /// messages across 200 sessions: with only the composite present the planner chose
    /// <c>NodeByLabelScan</c> over all 20,000 rows; with this index it chose <c>NodeIndexSeek</c>
    /// estimating 100.
    /// <para>
    /// So both indexes are needed and neither is redundant: this one serves
    /// <c>GetRecentBySession</c> (every turn), <c>GetAllBySession</c> and <c>DeleteBySession</c>, which
    /// filter <c>session_id</c> only; the composite serves <c>GetRecentMessagesAsOf</c>, which filters
    /// both columns and was measured to seek it.
    /// </para>
    /// </remarks>
    public const string MessageSessionIndex =
        "CREATE INDEX message_session_idx IF NOT EXISTS FOR (m:Message) ON (m.session_id)";

    /// <summary>
    /// Composite index on the session-scoped message reads — the primary short-term recall path.
    /// </summary>
    /// <remarks>
    /// <c>Message.session_id</c> is the property every session-scoped message query filters on, and
    /// nothing indexed it. There are exactly four predicate sites, all of them this property:
    /// <c>MessageQueries.cs:201</c> (<c>GetRecentBySession</c>), <c>:212</c> (<c>GetAllBySession</c>),
    /// <c>:258</c> (<c>DeleteBySession</c>) and <c>TemporalQueries.cs:99</c>
    /// (<c>GetRecentMessagesAsOf</c>). <c>:Message</c> carried <c>message_timestamp_idx</c>,
    /// <c>message_role_idx</c>, a content fulltext and a vector index — everything except the
    /// property the hot query actually filters.
    /// <para>
    /// <b>This is the hottest read in the library.</b> <c>GetRecentBySession</c> runs on essentially
    /// every turn: <c>MemoryContextAssembler.cs:220</c> → <c>ShortTermMemoryService.cs:190</c> →
    /// <c>Neo4jMessageRepository.cs:193</c>, entered from the MAF facade, from
    /// <c>Neo4jChatMessageStore.GetMessagesAsync</c> and from MCP <c>ObservationTools.cs:33</c>.
    /// </para>
    /// <para>
    /// Without it the planner had two options, both proportional to the total number of messages in
    /// the store rather than to the session: a <c>NodeByLabelScan(:Message)</c> plus filter, or a
    /// backwards <c>message_timestamp_idx</c> scan reading newest-first until <c>$limit</c> matches
    /// accumulated. The second is what makes the defect <b>bimodal and easy to miss in testing</b> —
    /// fast for the session just written to, degrading without bound for an idle session in a busy
    /// store. <c>GetAllBySession</c> has no <c>LIMIT</c> at all, so early termination never applied:
    /// unconditional full label scan plus sort.
    /// </para>
    /// <para>
    /// <b>Composite, not bare.</b> <c>TemporalQueries.cs:99-103</c> filters <c>session_id</c> equality
    /// <em>and</em> <c>m.timestamp &lt;= datetime($asOf)</c> — exact-prefix plus trailing range, the
    /// canonical composite seek, with the range pushed into the index. <c>session_id</c> leads, so the
    /// same index serves all four sites via its prefix; a separate bare <c>(session_id)</c> index would
    /// be pure duplicate write cost on the hottest write path. Both properties are <c>ON CREATE SET</c>
    /// only (<c>MessageQueries.cs:34-37, :101-104, :126-129</c>), so there is no update churn.
    /// </para>
    /// <para>
    /// The seek is unconditional; whether it <em>also</em> eliminates the sort for
    /// <c>ORDER BY m.timestamp DESC LIMIT</c> depends on the deployed Neo4j version's index-backed
    /// ordering for composite indexes, and is worth a <c>PROFILE</c> rather than an assumption.
    /// </para>
    /// <para>
    /// <c>message_timestamp_idx</c> is kept despite having no remaining standalone reader.
    /// <b>Measured on 5.26:</b> dropping it produced byte-identical plans for every query in this
    /// assembly, so it is dead weight on the hottest write path. It is retained anyway because the
    /// only benefit of removing it is write throughput, which is unmeasured — and removing a shipped
    /// index to claim an unmeasured gain is exactly the move this codebase keeps rejecting elsewhere.
    /// Decide it with a write-throughput measurement, not with suspicion.
    /// </remarks>
    public const string MessageSessionTimestampIndex =
        "CREATE INDEX message_session_timestamp_idx IF NOT EXISTS FOR (m:Message) " +
        "ON (m.session_id, m.timestamp)";

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

    /// <summary>
    /// Index on <c>ReasoningTrace.trace_kind</c> — the promotion marker separating an ordinary episode
    /// from a reusable procedure.
    /// </summary>
    /// <remarks>
    /// Seekable so a procedures-only search is a seek rather than a post-filter over the whole label,
    /// and mirroring <see cref="TraceSuccessIndex"/>, the sibling single-property index here.
    /// </remarks>
    public const string TraceKindIndex = "CREATE INDEX trace_kind_idx IF NOT EXISTS FOR (t:ReasoningTrace) ON (t.trace_kind)";

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

    /// <summary>
    /// Composite index backing the fact merge key.
    /// </summary>
    /// <remarks>
    /// L11. Every fact write MERGEs on <c>{subject_key, predicate_key, object_key, owner_key}</c>
    /// (<c>FactQueries.UpsertBatch</c>, <c>FusedPersistenceQueries.FactUpsertBatch</c>), and nothing
    /// indexed that combination — so each MERGE was an all-<c>:Fact</c> label scan whose cost grew
    /// with the size of the store rather than with the size of the write.
    /// <para>
    /// The property order matches the MERGE pattern. It also puts the two selective properties first:
    /// <c>owner_key</c> is <c>"*"</c> for every shared fact and <c>predicate_key</c> is drawn from a
    /// ~110-entry lexicon, so leading with either would make the prefix nearly non-discriminating.
    /// </para>
    /// <para>
    /// This is what makes the range-index key cap real for facts, which is why
    /// <c>IndexKeyBudget.EnsureCompositeIndexable</c> lands in the same change rather than after it.
    /// </para>
    /// </remarks>
    public const string FactMergeKeyIndex =
        "CREATE INDEX fact_merge_key_idx IF NOT EXISTS FOR (f:Fact) " +
        "ON (f.subject_key, f.object_key, f.predicate_key, f.owner_key)";

    /// <summary>
    /// Index on Fact.owner_key — the only seekable predicate dedup-on-create has.
    /// </summary>
    /// <remarks>
    /// <b>Measured, and it overturns a reasoned rejection.</b> <c>FactQueries.FindDuplicate</c> runs on
    /// <b>every fact write</b> and had <b>no index entry point at all</b>: <c>owner_key</c> is column 4
    /// of <c>fact_merge_key_idx</c> and a composite cannot be seeked from a non-prefix column;
    /// <c>toLower(subject)</c> and <c>toLower(predicate)</c> cannot be indexed because Neo4j 5 has no
    /// functional indexes; and <c>invalidated_at IS NULL</c> / <c>embedding IS NOT NULL</c> are both
    /// unindexable. Profiled on 5.26 with 20,000 facts across 200 owners, dedup-on-create planned a
    /// full <c>NodeByLabelScan</c> of all 20,000 — per fact written.
    /// <para>
    /// With this index the same query plans <c>NodeIndexSeek</c> at 100 rows: a 200x reduction on the
    /// write path. Adding <c>subject_key</c> to the query instead was tried first and does <b>not</b>
    /// work — filtering the composite's leading column alone still plans a full scan, the same
    /// leading-column-only limitation that made <c>message_session_idx</c> necessary.
    /// </para>
    /// <para>
    /// <b>Honest limit.</b> In a SINGLE-TENANT store <c>owner_key</c> is <c>"*"</c> for every shared
    /// fact, so the seek returns the whole label and this buys nothing beyond its write cost. It pays
    /// for itself in the multi-tenant deployment the owner-isolation work exists to serve, which is
    /// the case that scales. An earlier audit rejected this index on that selectivity argument alone;
    /// the argument is right for one deployment shape and wrong for the other, and only measurement
    /// distinguished them.
    /// </para>
    /// </remarks>
    public const string FactOwnerKeyIndex =
        "CREATE INDEX fact_owner_key_idx IF NOT EXISTS FOR (f:Fact) ON (f.owner_key)";

    /// <summary>
    /// Index on Fact.predicate_key — the relation-completeness retrieval path.
    /// </summary>
    /// <remarks>
    /// This is <see cref="FactMergeKeyIndex"/>'s defect repeating one column over.
    /// <c>fact_merge_key_idx</c> is <c>ON (subject_key, object_key, predicate_key, owner_key)</c>, and
    /// Neo4j uses a composite index only when the query filters a matching <b>prefix</b> — so a filter
    /// on <c>predicate_key</c> alone, sitting at column 3, got nothing from it.
    /// <para>
    /// <c>FactQueries.cs:88</c> (<c>SearchByCanonicalPredicates</c>) filters
    /// <c>f.predicate_key IN $predicateKeys</c>, and the query had <b>no index entry point
    /// whatsoever</b>. Neither companion predicate can rescue it: <c>f.invalidated_at IS NULL</c>
    /// (<c>:89</c>) is unindexable because a range index stores no nulls, and the owner clause under
    /// the default scope is <c>(f.owner_id = $ownerId OR f.owner_id IS NULL)</c> (<c>:84</c>, with
    /// <c>MemoryScope.IncludeShared</c> defaulting true) whose <c>IS NULL</c> disjunct disqualifies a
    /// <c>fact_owner_idx</c> seek. The plan was therefore a full <c>:Fact</c> label scan across all
    /// owners, then <c>ORDER BY confidence DESC</c> over the whole result — cost scaling with the
    /// total number of facts in the store.
    /// </para>
    /// <para>
    /// Conditional per turn, but unconditionally a full scan when it fires:
    /// <c>RecallOptions.ExpandFactsByPredicate</c> defaults false (<c>RecallOptions.cs:69</c>), and
    /// <c>Neo4jMemoryContextProvider.cs:234-235</c> flips it on per-turn whenever
    /// <c>decision.RequiresRelationCompleteness</c> — i.e. automatically for every aggregation,
    /// "list all" or "how many" question.
    /// </para>
    /// <para>
    /// <b>Single-property is correct here; a composite would not help.</b> <c>(predicate_key,
    /// owner_id)</c> cannot serve the default owner-or-shared shape for the same <c>IS NULL</c> reason,
    /// and <c>invalidated_at</c> is not seekable at all, so there is no second column worth carrying.
    /// </para>
    /// </remarks>
    public const string FactPredicateKeyIndex =
        "CREATE INDEX fact_predicate_key_idx IF NOT EXISTS FOR (f:Fact) ON (f.predicate_key)";

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

    /// <summary>Index on MemoryReadAudit.kind (read/privacy audit, upstream v0.5-compatible).</summary>
    public const string MemoryReadAuditKindIndex = "CREATE INDEX memory_read_audit_kind_idx IF NOT EXISTS FOR (a:MemoryReadAudit) ON (a.kind)";

    /// <summary>
    /// Index on MemoryReadAudit.memory_id — the property the history read-back actually matches on.
    /// </summary>
    /// <remarks>
    /// BUG-A2. <c>HistoryQueries</c> resolves an item's read audit three times over with
    /// <c>OPTIONAL MATCH (audit:MemoryReadAudit {memory_id: n.id})</c>, and nothing indexed
    /// <c>memory_id</c> — only <c>id</c> (a uniqueness constraint) and <c>kind</c>. So every history
    /// row triggered an all-<c>:MemoryReadAudit</c> label scan.
    /// <para>
    /// This degrades with <b>time rather than with data size</b>, which is what makes it easy to miss:
    /// a recall writes roughly 25 audit rows, so the scanned label grows on every read while the
    /// number of memories stays flat. A store that is fast on day one is slow on day ninety with an
    /// unchanged graph.
    /// </para>
    /// </remarks>
    public const string MemoryReadAuditMemoryIdIndex =
        "CREATE INDEX memory_read_audit_memory_id_idx IF NOT EXISTS FOR (a:MemoryReadAudit) ON (a.memory_id)";

    /// <summary>All property indexes in bootstrap order.</summary>
    public static readonly string[] PropertyIndexes =
    [
        ConversationSessionIndex,
        MessageTimestampIndex,
        MessageRoleIndex,
        MessageSessionIndex,
        MessageSessionTimestampIndex,
        EntityTypeIndex,
        EntityNameIndex,
        EntityCanonicalIndex,
        FactCategoryIndex,
        PreferenceCategoryIndex,
        TraceSessionIndex,
        TraceSuccessIndex,
        TraceKindIndex,
        ReasoningStepTimestampIndex,
        ToolCallStatusIndex,
        SchemaNameIndex,
        SchemaVersionIndex,
        EntityLocationIndex,
        FactOwnerIndex,
        FactMergeKeyIndex,
        FactOwnerKeyIndex,
        FactPredicateKeyIndex,
        EntityOwnerIndex,
        PreferenceOwnerIndex,
        TraceOwnerIndex,
        RelationshipOwnerIndex,
        ConversationArchivedIndex,
        MemoryReadAuditKindIndex,
        MemoryReadAuditMemoryIdIndex
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
        // RESERVED, and deliberately kept. Decision recorded 2026-08-12 after auditing it.
        //
        // State, precisely: the WRITE path is complete and caller-supplied -- AddStepAsync takes an
        // optional embedding and Neo4jReasoningStepRepository persists it -- but no first-party caller
        // supplies one (AgentTraceRecorder, the MCP tool and the evaluation CLI all omit it), and
        // NOTHING reads this index. It is the only vector index here with no reader.
        //
        // Kept rather than dropped because its real cost today is ~zero: a vector index over a property
        // that is always NULL indexes no nodes. Removing it would cost a migration, an ExpectedQueryCount
        // bump and a snapshot regeneration for no measurable gain, and reintroducing it later would cost
        // the same migration again. The honest position is to say what it is rather than to churn schema.
        //
        // The reader belongs with its consumer: step-level retrieval is what procedural memory would want
        // (find the steps that solved a similar sub-problem), so it should arrive in the same change that
        // uses it -- not as speculative machinery whose absence of a caller nobody notices.
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

    /// <summary>
    /// Lists indexes in the terminal FAILED state. Bootstrap previously validated only vector-index
    /// dimensions, so a range index that failed to populate — for example when a composite key
    /// exceeds Neo4j's ~8 KB key-size limit — degraded silently: queries kept working through full
    /// scans and nothing ever reported the index missing. POPULATING is deliberately not treated as
    /// a failure; it is the normal asynchronous build state.
    /// </summary>
    public const string ShowIndexStates =
        "SHOW INDEXES YIELD name, state, type, populationPercent " +
        "RETURN name AS name, state AS state, type AS type, " +
        "populationPercent AS populationPercent";

    // ── Schema-conformance introspection (CLI `schema-check`) ────

    /// <summary>Lists the names of all constraints in the current database.</summary>
    public const string ShowConstraintNames = "SHOW CONSTRAINTS YIELD name RETURN name";

    /// <summary>Lists the names of all indexes (range/fulltext/vector/point) in the current database.</summary>
    public const string ShowIndexNames = "SHOW INDEXES YIELD name RETURN name";

    // ── Migration ───────────────────────────────────────────────

    /// <summary>Unique constraint on Migration.version for tracking applied migrations.</summary>
    public const string MigrationVersionConstraint = "CREATE CONSTRAINT migration_version IF NOT EXISTS FOR (m:Migration) REQUIRE m.version IS UNIQUE";

    /// <summary>Check whether a migration has already been applied.</summary>
    public const string IsMigrationApplied = "MATCH (m:Migration {version: $version}) RETURN m LIMIT 1";

    /// <summary>Record a migration as applied.</summary>
    /// <remarks>
    /// <c>extension_id</c> is the schema-extension discriminator (30.14): null for the base sequence,
    /// the owning extension's id for anything under the <c>ext/&lt;id&gt;/</c> namespace. A property on
    /// the existing bookkeeping node rather than a new <c>(:ExtensionMigration)</c> label, deliberately:
    /// <c>:Migration</c> is not a domain label and is absent from <c>SchemaConstants.NodeLabels</c>, so
    /// it costs nothing in parity, whereas a new bookkeeping label would be the first ever and would
    /// surface in the fresh-database label scans Neo4j-side tooling performs.
    /// </remarks>
    public const string RecordMigration =
        "MERGE (m:Migration {version: $version}) SET m.appliedAtUtc = $appliedAtUtc, m.extension_id = $extensionId";

    /// <summary>Lists applied migrations with their owning extension, for the schema-check owners report.</summary>
    public const string ListAppliedMigrations =
        "MATCH (m:Migration) RETURN m.version AS version, m.extension_id AS extensionId, "
        + "m.appliedAtUtc AS appliedAtUtc ORDER BY version";
}
