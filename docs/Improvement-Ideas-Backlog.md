# Improvement Ideas Backlog

**Maintained by:** Deckard (Lead), Joi (Docs)
**Last updated:** 2026-07-09
**Status:** Post-preview backlog. Items here are deferred ideas, not known bugs.

The runtime is feature-complete for the current preview. This backlog captures optional or demand-driven work that is outside the `0.1.0-preview.4` stabilization path. Shipped items that appeared in older backlog versions have been removed or reframed here so this document does not double-count completed work.

## Recently Reconciled

The following ideas from earlier backlog versions are no longer pending as originally written:

| Item | Current disposition |
|---|---|
| GDS PageRank/community detection | Shipped as `AgentMemory.Analytics` with `AddGdsMemoryAnalytics`, `IMemoryPageRankService`, and `IMemoryCommunityService`. |
| Basic conflict detection and supersession | Shipped as `IConflictDetectionService`, `Neo4jConflictDetectionService`, `agentmemory conflicts`, and opt-in contradiction resolution through non-destructive `SUPERSEDED_BY`. |
| AutoGen.NET adapter | No separate adapter needed; AutoGen.NET was absorbed into Microsoft Agent Framework and is covered by `AgentMemory.AgentFramework`. |
| CLI migrate/schema-check scope | Shipped and expanded: `migrate`, `bootstrap`, `schema-check`, `consolidate`, `decay`, `conflicts`, `schema-parity`, `invalidate`, `supersede`, `history`. |

## 1. Provenance Scoring and Conflict Events

### Problem

The shipped conflict service can detect fact contradictions and optionally supersede lower-confidence facts. It does not yet expose a richer provenance-scoring model or domain event surface for application-specific conflict handling.

### Current Baseline

- `IConflictDetectionService` detects owner-scoped fact contradictions.
- `ResolveFactContradictionsAsync` can keep the highest-confidence assertion and supersede the rest.
- Supersession is non-destructive: losers are soft-invalidated and linked to winners with `SUPERSEDED_BY`.

### Possible Next Step

Add optional provenance scoring and event hooks:

- `IConflictDetectedEvent` for application handlers.
- `IConflictHandler` or callback policy for ask-the-user flows.
- A provenance score derived from extraction source, confidence, recency, repeated observations, and user feedback.
- Optional score fields in `Fact.Metadata` rather than new first-class schema properties until the shape proves stable.

### Why Deferred

The right scoring model depends on production usage. The current conflict/supersession base is enough for preview; richer policies should be demand-driven.

## 2. Cross-Agent Memory Sharing

### Problem

Owner-scoped memory isolates users and shared/global memory supports a broad common tier, but there is no first-class namespace model for multiple agents collaborating in a named project memory space with explicit read/write membership.

### Sketch

Introduce a `MemoryNamespace` model with access policies:

- private owner memory remains the default;
- a namespace can grant reader/writer permissions to agents or applications;
- repository queries accept an optional namespace scope;
- conflict detection and provenance become more important when multiple agents write to the same namespace.

### Why Deferred

This is a public API and schema commitment. It should wait for real multi-agent orchestration usage.

## 3. Local Embedding Adapter (ONNX)

### Problem

The current embedding abstraction is correct (`IEmbeddingGenerator<string, Embedding<float>>` through MEAI), but the project does not ship a concrete local embedding implementation. Offline, air-gapped, and cost-sensitive deployments need a local path.

### Sketch

Create `AgentMemory.Embedding.Onnx` with:

- an MEAI-compatible embedding generator;
- user-supplied model path and tokenizer configuration;
- startup dimension validation against Neo4j vector indexes;
- no bundled large model files in the NuGet package.

### Why Deferred

ONNX runtime packaging, tokenizer compatibility, model distribution, and vector dimension safety create a real maintenance surface. Build this after user demand is clear.

## 4. Local NLP Extractors

### Problem

The project ships LLM and Azure AI Language extraction, but not a local GLiNER/spaCy-like extractor. High-volume batch extraction can be too expensive or too slow if every extraction calls a hosted model.

### Sketch

Create `AgentMemory.Extraction.LocalNlp` with one or more extractor implementations:

- an ONNX NER extractor for common entity classes;
- optional rule/pattern-based fact and preference extraction;
- confidence-gated fallback to LLM extraction when configured.

Use the existing granular extractor interfaces: `IEntityExtractor`, `IFactExtractor`, `IPreferenceExtractor`, and `IRelationshipExtractor`.

### Why Deferred

There is no mature .NET GLiNER binding today, and a Python sidecar would violate the simple native deployment story.

## 5. Opik-Style LLM Observability

### Problem

The shipped OpenTelemetry layer captures infrastructure spans and metrics, but not LLM-quality artifacts such as prompt/response pairs, retrieval context quality, hallucination feedback, token economics, or extraction-quality regression signals.

### Sketch

Create an optional `AgentMemory.Observability.Opik` package when a stable .NET SDK or stable public API exists. It would decorate extraction and context assembly surfaces, correlate retrieved memory with generated outputs, and emit quality-oriented spans.

### Why Deferred

No official stable .NET Opik SDK was available when this backlog was refreshed. A hand-rolled undocumented client would be brittle.

## 6. Richer CLI Import/Export/Stats/Search

### Current Baseline

The `agentmemory` CLI already covers production maintenance: migrate, bootstrap, schema-check, consolidate, decay, conflicts, schema-parity, invalidate, and supersede.

### Deferred Commands

| Command | Description |
|---|---|
| `export-memory` | Dump selected memory as portable JSON. |
| `import-memory` | Restore exported memory into a store. |
| `stats` | Print graph counts, index status, session activity, and owner distribution. |
| `search` | Ad-hoc semantic/fulltext search from the command line. |

### Why Deferred

MCP already provides rich inspection surfaces for development, and operator demand should shape the command names and output formats.

## 7. Additional Framework Integrations

### LangChain.NET

Possible adapter: implement the relevant memory interface backed by `IMemoryService`. Revisit only with clear user demand.

### Semantic Router

A .NET semantic-router-style integration could wrap hybrid retrieval and route to named handlers. This is speculative and would be a net-new capability, not a compatibility adapter.

## Promotion Rule

Before promoting an item into active work:

1. Re-check the codebase so the sketch is not stale.
2. Write or update an ADR if the item changes schema, public API, or package boundaries.
3. Add it to `ROADMAP.md` only when there is a real demand signal or a release target.
4. Keep tests and docs part of the definition of done.
