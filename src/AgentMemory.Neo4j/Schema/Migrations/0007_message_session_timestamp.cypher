// Migration 0007 — composite index on the session-scoped message reads.
//
// Message.session_id is the property every session-scoped message query filters on, and nothing
// indexed it: MessageQueries.cs:201 (GetRecentBySession), :212 (GetAllBySession), :258
// (DeleteBySession) and TemporalQueries.cs:99 (GetRecentMessagesAsOf) are the only four predicate
// sites and all four use it. :Message already carried message_timestamp_idx, message_role_idx, a
// content fulltext and a vector index -- everything except the property the hot query filters.
// Fresh deployments pick this up via SchemaBootstrapper; this migration brings existing databases to
// parity. Idempotent (IF NOT EXISTS); each statement runs in its own transaction.
//
// This is the hottest read in the library: GetRecentBySession runs on essentially every turn
// (MemoryContextAssembler.cs:220 -> ShortTermMemoryService.cs:190 -> Neo4jMessageRepository.cs:193,
// entered from the MAF facade, Neo4jChatMessageStore.GetMessagesAsync, and MCP ObservationTools.cs:33).
// Without the index the planner had two options, both proportional to the TOTAL number of messages in
// the store rather than to the session: a NodeByLabelScan(:Message) plus filter, or a backwards
// message_timestamp_idx scan reading newest-first until $limit matches accumulated. The second is what
// makes the defect bimodal and easy to miss in testing -- fast for the session you just wrote to,
// degrading without bound for an idle session in a busy store. GetAllBySession has no LIMIT at all, so
// early termination never applied.
//
// COMPOSITE, NOT BARE: TemporalQueries.cs:99-103 filters session_id equality AND
// m.timestamp <= datetime($asOf), which is exact-prefix plus trailing range -- the canonical composite
// seek. session_id leads, so this one index serves all four sites via its prefix and a separate bare
// (session_id) index would be pure duplicate write cost on the hottest write path.
//
// ON AN EXISTING DATABASE THIS INDEX POPULATES OVER DATA ALREADY WRITTEN, but unlike the fact merge key
// in 0005 no length guard is needed: timestamp is a fixed-width datetime and session_id is an
// identifier, so the composite key is far below Neo4j's ~8 KB range-index cap. A session_id long enough
// to exceed it would already have failed before this migration existed -- MessageQueries.cs:27-28 sets
// the same string on Conversation.session_id, which conversation_session_idx has always indexed.

CREATE INDEX message_session_timestamp_idx IF NOT EXISTS FOR (m:Message) ON (m.session_id, m.timestamp);
