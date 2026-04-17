# Schema Review Findings — Gaff

**Date:** 2025-07-24
**Author:** Gaff (Neo4j Persistence Engineer)
**Scope:** Deep verification sprint — code vs documentation discrepancies

---

## Discrepancies Found

### 1. ToolCallStatus Enum Parity Gap (Medium Priority)

**File:** `src/Neo4j.AgentMemory.Abstractions/Domain/Reasoning/ToolCallStatus.cs`

The .NET `ToolCallStatus` enum has 4 values: `Pending`, `Success`, `Error`, `Cancelled`.
Python defines 6: `pending`, `success`, `failure`, `error`, `timeout`, `cancelled`.

**Impact:** `Neo4jToolCallRepository.cs:61` has Cypher checking `$status IN ['error', 'timeout']` for `failed_calls` increment, but `Timeout` is not a valid enum value so that branch is dead code.

**Recommendation:** Add `Failure` and `Timeout` to `ToolCallStatus` enum.

### 2. MCP Tool Count Stale in 4 Documents (Low Priority)

README.md, feature-record.md, python-dotnet-comparison.md, and schema.md all say "21 tools" but actual count is **28** `[McpServerTool]` attributes.

**Recommendation:** Update all documents to reflect 28 tools.

### 3. Test File Count Stale (Trivial)

feature-record.md and python-dotnet-comparison.md say "55+ test files" but actual count is **111+** test class files.

### 4. Schema.md Internal Contradiction (Trivial)

Section 2.5 lists `relationship_id (MemoryRelationship.id)` as ".NET extension" constraint, but section 2.3 correctly states this phantom constraint was removed. Delete the row from section 2.5.

### 5. Schema Index Difference (Informational)

Python: `schema_id_idx` on Schema.id
.NET: `schema_version_idx` on Schema.version

This is a real schema difference not explicitly documented in the difference tables.

---

## Decision Requested

Should we:
1. Add `Failure` and `Timeout` to `ToolCallStatus` enum? (impacts serialization/deserialization)
2. Update all doc counts in a single documentation sweep?
3. Accept the `schema_version_idx` vs `schema_id_idx` difference as intentional?
