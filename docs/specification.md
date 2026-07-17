# Agent Memory for .NET - Current Specification

**Status:** Current, code-aligned specification
**Last updated:** 2026-07-17

This document is the active specification for the shipped preview.

## Product Identity

Agent Memory for .NET is an independent community .NET implementation (multi-targeting net8.0, net9.0, and net10.0) of graph-native persistent memory for AI agents. It is inspired by `neo4j-labs/agent-memory`, interoperates with Neo4j and Microsoft agent ecosystems, and is not an official Neo4j product.

## Product Goal

The product provides a native .NET memory provider for agents that can:

- persist short-term conversation memory, long-term structured knowledge, and reasoning traces;
- recall relevant context across sessions with vector, fulltext, hybrid, temporal, graph, and recency-aware retrieval;
- isolate users and applications through owner-scoped memory and optional database-per-application routing;
- integrate with Microsoft Agent Framework, Semantic Kernel, MCP clients, and Neo4j-backed GraphRAG retrieval;
- expose operational maintenance through a CLI, schema bootstrap/migration support, and Testcontainers-backed validation.

## Current Package Set

The shipped source packages are:

- `AgentMemory.Abstractions`
- `AgentMemory.Core`
- `AgentMemory.Neo4j`
- `AgentMemory.Extraction.Llm`
- `AgentMemory.Extraction.AzureLanguage`
- `AgentMemory.Enrichment`
- `AgentMemory.Observability`
- `AgentMemory.AgentFramework`
- `AgentMemory.SemanticKernel`
- `AgentMemory.McpServer`
- `AgentMemory.Analytics`
- `AgentMemory` meta-package

Supporting projects include `tools/AgentMemory.Cli`, samples, benchmarks, and unit/integration/performance tests.

## Required Architecture

The implementation follows ports and adapters:

- `AgentMemory.Abstractions` defines domain records, options, service interfaces, repository interfaces, exceptions, and schema constants.
- `AgentMemory.Core` implements orchestration, context assembly, extraction pipeline stages, entity resolution, truncation strategies, default stubs, and the top-level memory facade.
- `AgentMemory.Neo4j` owns Neo4j driver/session infrastructure, repositories, centralized Cypher, schema bootstrap/migrations, GraphRAG retrieval implementations, memory decay, consolidation, conflict detection, and schema parity/conformance helpers.
- Adapter packages depend inward and translate to external frameworks. Core never depends on Microsoft Agent Framework, Semantic Kernel, MCP, Azure Text Analytics, OpenTelemetry, or Neo4j driver types.

## Memory Model

The system has three memory layers:

- Short-term memory: `Conversation`, `Message`, session history, recent and semantic message recall.
- Long-term memory: `Entity`, `Fact`, `Preference`, and `Relationship` (`RELATED_TO` edge) with provenance, embeddings, owner scoping, soft invalidation, supersession, and temporal recall.
- Reasoning memory: `ReasoningTrace`, `ReasoningStep`, `ToolCall`, tool aggregates, and `TOUCHED` audit edges from reasoning steps to entities.

## Retrieval Requirements

The system supports:

- vector search over messages, entities, facts, preferences, reasoning traces, and reasoning steps;
- BM25 fulltext search over messages, entities, and facts;
- hybrid GraphRAG search with vector/fulltext Reciprocal Rank Fusion;
- graph traversal over Neo4j relationships;
- context assembly with token/character budgets and truncation strategies;
- point-in-time recall with valid-time and transaction-time checks;
- optional recency/structural reranking through `MemoryRankingOptions`.

## Isolation Requirements

The default single-database deployment is logically isolated by `owner_id` and `MemoryScope`. Null owner means shared/global memory. The optional store tier routes an `ApplicationId` to a dedicated Neo4j database through `MemoryStorageStrategy.DatabasePerApplication`, which requires Neo4j Enterprise or AuraDB.

Scoped reads must return only the owner's private memory plus shared memory when `IncludeShared` is enabled. Destructive or mutating operations that accept an owner must not cross owner buckets.

A central `IMemoryIsolationPolicy` (#100) resolves the read scope and write owner for every operation under one of three modes — `SingleTenant` (default, today's behavior), `WarnOnUnscoped`, or `StrictMultiTenant` (fails closed before any repository call when identity is absent) — so isolation enforcement lives in one place rather than being re-derived per service, across every MAF/SK/MCP tenant-facing entry point.

Recalled memory is also subject to a trust-boundary program (#92, Phases 1-8): every recalled entity/fact/preference/message is delimited/escaped, admission-checked against instruction-like content, and stamped with a `MemoryTrustLevel`, so a host can treat recalled content as untrusted reference data rather than an unrestricted system instruction. See `docs/security/threat-model.md` (TT-05) for the full detail.

## Integration Requirements

The shipped adapters are:

- Microsoft Agent Framework: context provider, chat history provider, facade, memory tools, trace recorder, `WithMemoryIdentity` identity flow.
- Semantic Kernel: `Neo4jMemoryPlugin`, text search adapter, DI helpers.
- MCP server: memory tools, resources, prompts, over the stdio host transport.
- GraphRAG: internalized in `AgentMemory.Neo4j`; registered through `AddGraphRagAdapter` when enabled.

## Operational Requirements

The project must provide:

- idempotent schema bootstrap;
- file-based migrations and migration tracking;
- runtime schema conformance checking (`agentmemory schema-check`);
- static upstream schema parity checking (`agentmemory schema-parity`);
- operational commands for migrate, bootstrap, consolidate, decay, conflicts, invalidate, and supersede;
- Release builds with warnings as errors for source packages;
- unit, Semantic Kernel, live Neo4j integration, and performance smoke coverage.

## Non-Goals

The shipped preview does not attempt to bundle a model, host Neo4j, provide a SaaS backend, require Python, or implement every Python ecosystem integration. Local ONNX embeddings, local NLP extractors, LangChain.NET, Opik-specific observability, and richer CLI import/export/stat commands are deferred until there is demand or ecosystem maturity.

## Related Documents

- [`getting-started.md`](getting-started.md) — install, configure, first memory store
- [`architecture.md`](architecture.md) — packages, layers, boundaries, dependency rules
- [`agent-framework.md`](agent-framework.md) — Microsoft Agent Framework integration
- [`schema.md`](schema.md) — Neo4j schema reference
