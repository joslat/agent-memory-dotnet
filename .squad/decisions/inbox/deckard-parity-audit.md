# Decision: P1 Sprint Complete — P2 Items Are Not Parity Blockers

**Author:** Deckard (Lead / Solution Architect)  
**Date:** 2025-07-23  
**Status:** Proposed

## Context

The P1 Schema Parity Sprint completed 10 of 11 P1 items (P1-9 datetime deferred), bringing schema parity from ~88% to ~96%. After thorough audit of both Python reference code (`queries.py`, `query_builder.py`, `schema.py`) and .NET implementations, we now have a precise picture of what remains.

## Decision

### 1. P2 items are improvements, not parity requirements

After verifying every P2 item against the Python reference:

- **P2-1 (Schema node)**: Only needed if we support custom entity schema models (YAML/JSON config files). Python uses it; .NET uses fixed types. **Classified: Nice-to-have.**
- **P2-2 (Graph export queries)**: Python has 4 typed export queries. .NET already has `MemoryExportGraph` MCP tool. **Classified: Improvement.**
- **P2-3 (GET_MEMORY_STATS)**: Diagnostic utility. **Classified: Improvement.**
- **P2-4 (Session listing pagination)**: DX improvement. **Classified: Improvement.**
- **P2-6 (Tool.description)**: Python defines but never auto-populates in `CREATE_TOOL_CALL`. **Classified: Trivial gap.**

### 2. P1-9 (datetime) is the single biggest remaining schema gap

ISO string timestamps are functional but prevent:
- Native temporal arithmetic in Cypher
- Efficient temporal range comparisons
- Cross-implementation consistency on shared databases

Estimated effort: 3-5 days (all repos + migration + tests).

### 3. Multi-stage extraction pipeline is the biggest functional gap

The absence of `ExtractionPipeline` with merge strategies (UNION, INTERSECTION, CONFIDENCE, CASCADE, FIRST_SUCCESS) is the most impactful functional gap for production use. This is purely functional, not schema-related.

### 4. The ~96% schema parity and ~91% functional parity numbers are verified

These are based on line-by-line code comparison, not estimates. All claims are traceable to specific files and line numbers.

## Impact

- No further schema work needed for "production-ready" status
- P1-9 datetime migration can be scheduled as a standalone effort
- Multi-stage extraction pipeline should be prioritized for Phase 3
- P2 items can be deprioritized without affecting parity claims
