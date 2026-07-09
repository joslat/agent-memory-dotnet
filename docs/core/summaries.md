# Core Documentation Summaries

Status: current as of 2026-07-09.

This document summarizes the core docs from high-level intent down to implementation decisions.

## 1. Philosophy - Highest Level

Agent Memory for .NET exists because useful agents need durable, scoped, explainable memory rather than a larger prompt buffer. The project models memory as a graph: conversations provide evidence, extraction creates durable knowledge, reasoning traces preserve execution history, and Neo4j stores the relationships among them.

Key idea: graph-native memory should be auditable, scoped, temporal, and idiomatic to .NET.

## 2. Requirements and Constraints - What the Project Must Satisfy

The system must support short-term, long-term, and reasoning memory; Neo4j persistence; MEAI embeddings; opt-in extraction; blended retrieval; owner/store/session isolation; framework adapters; and operational tooling.

The main constraints are `net9.0`, Neo4j 5.x, vector dimension consistency, MIT licensing, independent project identity, and optional external dependencies. The main pending work is preview soak, API stabilization, local embedding/NLP gaps, additional integrations, richer observability, CLI breadth, and ongoing docs drift control.

## 3. Design Document - How the System Is Shaped

The design is a ports-and-adapters architecture:

- `Abstractions` defines contracts and shared models.
- `Core` implements memory orchestration and portable services.
- `Neo4j` implements persistence, schema, migrations, and GraphRAG context.
- Optional packages add extraction, observability, enrichment, analytics, and framework adapters.
- The meta-package offers a convenient common stack but does not make every optional behavior active.

The memory model is layered as store -> owner -> session, and the graph model is layered as conversations/messages, entities/facts/preferences/relationships, and traces/steps/tool calls.

## 4. Specification - Lowest-Level Normative Contract

The specification defines required packages, service surfaces, schema labels, relationship types, property naming, fact merge keys, temporal semantics, isolation rules, retrieval semantics, extraction semantics, adapter semantics, operational requirements, and testing expectations.

If another document conflicts with the specification, the specification should win unless the code has changed and the docs need an update.

## 5. ADRs - Accepted Decisions

| ADR | Decision summary |
|---|---|
| ADR 0001 | Keep the project an independent community .NET implementation, not an official Neo4j product or fork. |
| ADR 0002 | Use ports-and-adapters layering with portable Core and infrastructure adapters. |
| ADR 0003 | Ship focused packages plus a convenience meta-package. |
| ADR 0004 | Use Neo4j as the primary native persistence model. |
| ADR 0005 | Use Microsoft.Extensions.AI for embeddings and LLM-facing abstractions. |
| ADR 0006 | Model memory as short-term, long-term, and reasoning layers. |
| ADR 0007 | Make store and owner isolation first-class schema/query concerns. |
| ADR 0008 | Internalize GraphRAG retrieval in `AgentMemory.Neo4j` and register it opt-in. |
| ADR 0009 | Use a staged extraction pipeline with opt-in backends and default stubs. |
| ADR 0010 | Prefer non-destructive temporal memory over hard deletion. |
| ADR 0011 | Keep observability, enrichment, analytics, Azure extraction, and GraphRAG opt-in. |
| ADR 0012 | Ship MAF, SK, and MCP surfaces; keep additional frameworks demand-driven. |
| ADR 0013 | Treat docs as operational truth and label historical material explicitly. |
| ADR 0014 | Keep upstream schema compatibility as an automated guardrail, not a runtime compatibility layer. |
| ADR 0015 | Expose long-term memory history as a normalized read-only service and CLI surface. |
| ADR 0016 | Evaluate memory quality/performance through deterministic memory-layer checks before grading chat answers or model context. |

## 6. Performance and Quality Evaluation

Measure the memory system first: persisted state, service behavior, retrieval result sets, ranking, isolation, temporal lifecycle, provenance, and compatibility. Do not start by grading full chat answers or model context assembly, because that entangles memory behavior with prompt design, model selection, and evaluator noise.

The Python-vs-.NET comparison should use identical Neo4j setup, fixture data, embeddings, query sets, and normalized result records. Compare scenario pass rate, p95 latency, Recall@K, MRR/NDCG, owner leak count, temporal correctness, and provenance completeness while documenting intentional .NET extensions.

## 7. Current Implementation Plan

The active implementation plan promotes `AgentMemory.Sample.AgentWithMemory` as the golden path, adds explicit `WithMemoryIdentity(...)` scoping, keeps offline mock providers behind DI replacement seams, cleans stale sample docs, adds sample smoke-build CI, automates compatibility checks through static schema parity plus an upstream-watch cadence, and adds a first long-term memory-history read model.

The deeper compatibility follow-up is behavioral TCK coverage: either bridge to `neo4j-labs/agent-memory-tck` or mirror its high-value scenarios as .NET integration tests while preserving stricter .NET isolation behavior.

## 8. Most Important Low-Level Facts

- The current target framework is `net9.0`.
- The current release recorded by project docs is `0.1.0-preview.4`.
- The repository license is MIT.
- The schema uses snake_case Cypher properties.
- `ReasoningTrace.task_embedding` is the current task vector property.
- `Fact` single and batch upsert merge by `{subject, predicate, object, owner_key}`.
- `owner_id = null` means shared/global memory.
- `owner_key` keeps shared and owned fact triples distinct during MERGE.
- `MemoryStorageStrategy.SharedDatabase` is default and Community-compatible.
- `MemoryStorageStrategy.DatabasePerApplication` requires Neo4j Enterprise or AuraDB.
- GraphRAG retrieval is registered by `AddGraphRagAdapter(...)` in `AgentMemory.Neo4j`.
- LLM extraction is opt-in; core stub extractors do not fabricate memory.
- Analytics is a separate opt-in package, not part of the meta-package.
- Memory quality evaluation starts at the memory layer, not at chat-answer grading.
- Local verification for this 2026-07-09 work passed: 2658 Release unit tests plus a 5-test live Neo4j shakedown; the earlier docs cleanup also recorded 34 Semantic Kernel tests.
