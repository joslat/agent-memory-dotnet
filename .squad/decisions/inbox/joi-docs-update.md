# Decision: Post-Sprint Documentation Audit Process

**Author:** Joi (Docs / DX Engineer)  
**Date:** 2025-07-24  
**Status:** Proposed

## Context

After the gap closure sprint (Waves A–C), 9 out of 13 docs files had stale information (wrong test counts, wrong MCP tool counts, missing feature completions, outdated phase statuses). The `memory_get_observations` tool was listed as "not implemented" in 3 separate documents despite being fully implemented.

## Decision

After every sprint or major feature merge, a documentation audit should be performed. The audit should:

1. Verify all numeric claims (test counts, tool counts, parity percentages) against actual code
2. Check phase/status trackers against git log
3. Search for "not implemented" / "❌" markers and verify each one against source
4. Update the implementation-status.md test results section

## Consequences

- Prevents documentation drift from accumulating
- Keeps README accurate for new developers evaluating the project
- Ensures the feature-record and comparison docs remain reliable references

## Alternatives Considered

- Automated test-count extraction in CI — too brittle, doesn't cover narrative docs
- Docs-as-code with generated sections — over-engineering for current project size
