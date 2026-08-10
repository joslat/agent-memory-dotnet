// Migration 0009 — index Message.session_id, the property the hot session reads filter.
//
// 0007 added a composite (session_id, timestamp) believing it would also serve the session-only
// queries because session_id is its leading column. MEASURED, THAT IS FALSE. Neo4j will not seek a
// composite from a leading-column predicate alone; asking by hint returns "Must use the properties
// session_id, timestamp ... but only session_id was found", and ORDER BY timestamp is not a
// predicate. Verified on 5.26 with 20,000 messages across 200 sessions: with only the composite the
// planner chose NodeByLabelScan over all 20,000 rows; with this index it chose NodeIndexSeek
// estimating 100.
//
// Both are therefore needed and neither is redundant. This one serves GetRecentBySession (every
// turn), GetAllBySession and DeleteBySession; 0007's composite serves GetRecentMessagesAsOf, which
// filters both columns and was measured to seek it.
//
// Fresh deployments pick this up via SchemaBootstrapper; this migration brings existing databases to
// parity. Idempotent (IF NOT EXISTS); each statement runs in its own transaction.

CREATE INDEX message_session_idx IF NOT EXISTS FOR (m:Message) ON (m.session_id);
