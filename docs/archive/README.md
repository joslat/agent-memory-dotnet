# docs/archive

This folder holds documents that were active planning or analysis artifacts during earlier phases of the project. Implementation is complete; these files are kept for historical context only.

| File | What it was |
|------|-------------|
| `refactoring-plan.md` | Four-wave refactoring plan (Waves A–C gap closure + Wave D). All waves complete. |
| `python-agent-memory-analysis.md` | Phase 0 analysis of the Python `neo4j-labs/agent-memory` reference — used to plan the .NET design. Superseded by `docs/parity-assessment.md` and `docs/python-dotnet-comparison.md`. |
| `cypher-analysis.md` | Early Cypher parity analysis. Superseded by `docs/parity-assessment.md` (July 2026). |
| `Analysis_Review.md` | The 2026-05-31 full-repository analysis & review that kicked off the remediation branch. All findings addressed; superseded by `docs/Memory_Review_and_Implementation_Plan.md`. *(archived 2026-06-07)* |
| `Implementation_Plan_Remediation.md` | Remediation/hardening/docs plan derived from `Analysis_Review.md`. Closed/complete. *(archived 2026-06-07)* |
| `Remaining_Work_Roadmap.md` | Pre-remediation prioritized roadmap; explicitly superseded by `docs/Memory_Review_and_Implementation_Plan.md` (its own source-of-truth note). *(archived 2026-06-07)* |
| `aspire-demo-plan.md` | Plan for the Aspire demo (`samples/AspireDemo`). Demo shipped. *(archived 2026-06-07)* |
| `delete-session-gap-plan.md` | Plan for the `DELETE_SESSION_DATA` parity gap. Shipped + integration-tested. *(archived 2026-06-07)* |
| `maf-1.9.0-migration.md` | Microsoft Agent Framework 1.1.0→1.9.0 migration plan. Migration landed. *(archived 2026-06-07)* |
| `full-implementation-plan.md` | The full 6-phase implementation plan (was `Agent-memory-for-dotnet-implementation-plan.md` at repo root). All phases complete. *(archived 2026-06-14)* |
| `bitemporal-memory-assessment.md` | Design/discussion for bitemporal storage + invalidate-not-delete. **Implemented** (D5/D7, verified live 2026-06-13) — kept for design rationale. *(archived 2026-06-14)* |
| `decay-improvement-proposal.md` | Structure-first decay proposal (recency re-ranker, hop-decay, intent presets). **Implemented** (D1–D3) — kept for design rationale. *(archived 2026-06-14)* |
| `upstream-issue-memory-decay-bitemporal.md` | Ready-to-file upstream issue companion to the two design docs above. *(archived 2026-06-14)* |

> These documents are read-only history. Do not update them with new status — open a new doc instead.
> The table above is not exhaustive of every file in this folder; it indexes the most recently archived sets.
