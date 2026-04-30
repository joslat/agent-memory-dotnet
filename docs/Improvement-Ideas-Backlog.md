# Improvement Ideas Backlog

**Maintained by:** Deckard (Lead), Joi (Docs)  
**Date:** 2026-04-30  
**Status:** Post-launch backlog. Items here are deferred — not abandoned. Each has a design sketch to accelerate future implementation.

These ideas were explicitly deferred from the v1 release scope. They are captured here with enough detail to restart implementation at any time.

---

## 1. Memory Conflict Detection + Provenance Scoring

### Problem

Long-lived agents accumulate contradicting facts across conversations. A user might say "I work at Company A" in April, then "I just started at Company B" in October. Without conflict detection, the memory graph silently holds both `(:Fact)` nodes as equally true — and context assembly may return both to the LLM, creating confusion or hallucinated synthesis.

This is especially damaging for high-stakes facts: employer, location, health status, stated preferences. The more an agent is used, the worse the silent contradiction problem becomes.

### How It Would Work

A `ConflictDetectionService` runs either on-write (real-time) or on a scheduled cadence. It issues Cypher queries looking for `(:Fact)-[:ABOUT]->(:Entity)` clusters where multiple fact nodes share the same predicate but carry different values — e.g., two `employment` facts about the same `Person` entity pointing to different organizations.

Each `Fact` node gains a `ProvenanceScore` composite property:

```json
{
  "source": "extraction_llm",
  "confidence": 0.87,
  "extractedAt": "2026-04-30T20:00:00Z",
  "conversationId": "conv_abc123",
  "sessionCount": 3
}
```

A `ReliabilityChain` concept governs resolution priority: facts from older sessions with high confidence score higher than recent low-confidence extractions. Recency alone is not sufficient — a confident older fact should not be silently overwritten by a low-confidence new one.

When a conflict is detected:
- Emit an `IConflictDetectedEvent` (domain event, pluggable handler)
- Optionally flag the lower-confidence fact with a `conflictStatus: "superseded"` property
- Optionally prompt the agent (via a registered `IConflictHandler`) to ask the user for resolution

### Implementation Sketch

- **New service:** `ConflictDetectionService` in `AgentMemory.Core`
- **New Cypher queries:** in `AgentMemory.Neo4j`, querying for predicate collisions on shared entity targets
- **Schema change:** `ProvenanceScore` as indexed properties on `Fact` nodes; `conflictStatus` enum property
- **New interfaces:** `IConflictDetectedEvent`, `IConflictHandler` in `AgentMemory.Abstractions`
- **Hook point:** `IFactRepository.UpsertAsync` triggers detection on write; background job handles periodic sweeps

### Why Deferred

Requires real-world usage data to understand the most common conflict patterns before hardcoding heuristics. Confidence scoring itself is model-dependent. The design surface (event shape, handler contract, Cypher patterns) is non-trivial to get right on the first pass. Better to launch v1 and observe.

---

## 2. GDS Integration — PageRank + Community Detection

### Problem

Without graph analytics, context assembly treats all memories as equally weighted (modulo recency and explicit scores). The graph structure — which nodes are most connected, which form topic clusters — is completely ignored. A memory that is referenced by 40 other memories is retrieved with the same weight as one referenced by none.

PageRank applied to the memory graph would surface the most semantically central memories. Community detection (Louvain) would identify topic clusters, enabling richer retrieval: "give me the top 5 memories from the same cluster as this query."

### How It Would Work

An optional `AgentMemory.Analytics` package, requiring the Neo4j GDS plugin to be installed. The package is a zero-dependency add-on; the core library functions without it.

`MemoryPageRankService`:
- Projects the in-memory graph into GDS using `gds.graph.project`
- Runs `gds.pageRank.stream` on the projected graph
- Writes results back as a `pageRankScore` float property on memory nodes via `gds.pageRank.write`

`MemoryCommunityService`:
- Runs `gds.louvain.stream` on the projected graph
- Tags each node with a `communityId` integer property
- Exposes `GetCommunitySummaryAsync(string communityId)` for retrieval

`MemoryContextAssembler` integration:
- Optionally reads `pageRankScore` if available (detected via schema check)
- Boosts retrieval score: `combinedScore = semanticScore * 0.7 + pageRankScore * 0.3`
- Community filtering: optionally restrict context window to top-K memories from the most relevant community

### Implementation Sketch

- **New package:** `AgentMemory.Analytics` with `IMemoryAnalyticsService`
- **DI registration:** `AddAgentMemoryAnalytics(this IServiceCollection services)` extension
- **Scheduler hook:** Integrate with existing background job infrastructure for periodic GDS projection refresh
- **GDS version pinning:** Test against GDS 2.x; document minimum version requirement

### Why Deferred

GDS plugin adds operational complexity — not all Neo4j deployments have it (especially AuraDB Free tier). The Python reference has no equivalent. Better after v1 when usage patterns (graph density, typical session sizes) are understood. PageRank on a sparse early-stage graph yields low signal; this pays off at scale.

---

## 3. Cross-Agent Memory Sharing

### Problem

In multi-agent systems, agents currently operate in isolated memory namespaces. A research agent and a writing agent collaborating on the same project cannot share what they've learned. Each starts cold. Entity resolution runs twice on the same data. Facts are duplicated across isolated graphs.

As multi-agent orchestration becomes more common (MAF, Semantic Kernel Process Framework), this isolation becomes a first-class limitation — not a detail.

### How It Would Work

A `SharedMemorySpace` concept: named namespaces that multiple agents can read and write. Each namespace has explicit membership (which `agentId` values can access) and access policies (read-only vs. read-write per agent).

Schema additions:

```cypher
(:Agent)-[:HAS_ACCESS_TO {role: "reader|writer"}]->(:MemoryNamespace)
(:Memory)-[:STORED_IN]->(:MemoryNamespace)
```

All existing repository queries gain an optional `namespaceId` parameter. When omitted, the agent's private namespace is used. When provided and authorized, queries span the shared namespace.

Conflict resolution in shared spaces: default is last-write-wins with provenance logging. With item #1 (Conflict Detection) active, full provenance-based resolution is available.

API surface:

```csharp
await memoryService.ShareWithNamespaceAsync("project-apollo", agentId: "writer-agent");
await memoryService.QuerySharedAsync("project-apollo", query, cancellationToken);
```

### Implementation Sketch

- **Schema migration:** new `MemoryNamespace` node type, new relationship types, migration script
- **Repository changes:** namespace-aware overloads on `IMessageRepository`, `IEntityRepository`, `IFactRepository`
- **Authorization middleware:** `INamespaceAccessPolicy` checked before cross-namespace reads/writes
- **DI:** `AddSharedMemoryNamespace(name, accessPolicy)` builder extension

### Why Deferred

Significant schema redesign with migration complexity. No demand signal yet — real multi-agent usage patterns must be observed before committing to a public API surface. Requires item #1 for safe shared-write semantics. Design must be validated against real orchestration frameworks before a stable API can be promised.

---

## 4. Local Embedding Adapter (ONNX)

### Problem

All current embedding support requires a cloud provider: Azure OpenAI, OpenAI, or any other MEAI-compatible cloud service. Air-gapped enterprise deployments, cost-sensitive use cases, and offline development environments cannot use the library effectively — they are blocked at the embedding step.

### How It Would Work

A new package `AgentMemory.Embedding.Onnx` implementing `IEmbeddingGenerator<string, Embedding<float>>` from Microsoft.Extensions.AI (MEAI). This keeps it fully drop-in compatible with the existing embedding pipeline.

Internals use `Microsoft.ML.OnnxRuntime` to load a bundled or user-supplied ONNX model. The default model would be `all-MiniLM-L6-v2` (384 dimensions, ~90 MB), a widely-used sentence embedding model with permissive licensing (Apache 2.0).

Configuration:

```csharp
builder.AddAgentMemory(options =>
    options.UseOnnxEmbeddings(modelPath: "models/all-MiniLM-L6-v2.onnx", dimensions: 384));
```

Model dimensionality must match the Neo4j vector index dimension. A startup validation step checks this and throws a descriptive `ConfigurationException` on mismatch.

### Implementation Sketch

- **New package:** `AgentMemory.Embedding.Onnx`
- **Dependencies:** `Microsoft.ML.OnnxRuntime`, `Microsoft.Extensions.AI`
- **Model distribution:** model file is NOT bundled in NuGet (too large); user supplies path or downloads via a provided script
- **Tokenizer:** use `Microsoft.ML.Tokenizers` (SharpToken alternative) for BPE/WordPiece tokenization
- **Startup validation:** check output dimension against configured vector index at `IHostedService.StartAsync`

### Why Deferred

ONNX native binaries (`onnxruntime.dll` / `.so`) complicate NuGet packaging significantly — platform-specific RID dependencies. Model dimensionality mismatch silently degrades search quality without obvious errors. Adds a large ongoing maintenance surface (ONNX runtime updates, tokenizer compatibility). Not present in Python reference. Address after v1 when deployment constraints from real users are better understood.

---

## 5. Local NLP Extractors (GLiNER / ONNX NER)

### Problem

The Python reference implementation uses GLiNER (zero-shot named entity recognition) and spaCy for entity and fact extraction — without calling an LLM. This makes extraction fast, cheap, and offline-capable. The .NET implementation has no equivalent: all extraction currently requires an LLM call, which costs tokens, adds latency, and creates a hard dependency on a cloud service.

For high-volume extraction scenarios (indexing historical conversations, batch processing), LLM-based extraction is prohibitively expensive.

### How It Would Work

**Option A — ONNX NER model:**  
Export a fine-tuned NER model (e.g., based on `dslim/bert-base-NER` or a GLiNER-equivalent) to ONNX format. Implement `INerExtractor` in a new package `AgentMemory.Extraction.LocalNlp` using `Microsoft.ML.OnnxRuntime`. This handles entity extraction (PERSON, ORG, LOC, etc.) but not zero-shot generalization.

**Option B — Python GLiNER via gRPC sidecar:**  
A lightweight Python gRPC service wraps GLiNER. The .NET package calls it via a generated proto client. Allows full GLiNER capabilities without a native .NET port. Adds operational complexity (Python sidecar must be running).

**Option C — Wait for native .NET GLiNER:**  
The GLiNER model architecture is not fundamentally tied to Python. A .NET port is theoretically possible. Monitor the ecosystem.

The new package would implement `IExtractionService` from `AgentMemory.Abstractions`, making it a drop-in replacement for the LLM extractor.

### Implementation Sketch

- **New package:** `AgentMemory.Extraction.LocalNlp`
- **Start with Option A** (ONNX NER) for entities; combine with a local rule-based fact extractor for common patterns
- **Fallback:** graceful degradation to LLM extractor when local model confidence is below threshold
- **DI:** `UseLocalNlpExtraction(modelPath)` builder extension

### Why Deferred

No production-quality .NET GLiNER binding exists today. Option B (gRPC sidecar) adds significant operational complexity that contradicts the library's goal of simple deployment. Option A covers standard entity types but loses zero-shot generalization. Blocked on ecosystem maturity. Revisit when a viable .NET NER library with ONNX support and acceptable zero-shot performance emerges — watch the ML.NET and ONNX community closely.

---

## 6. Opik Observability Integration

### Problem

OpenTelemetry covers infrastructure-level signals: traces, metrics, logs. It does not capture LLM-specific quality signals: hallucination rates, token efficiency per memory operation, user feedback scores correlated with memory retrieval quality, or prompt→response pair analysis for extraction quality regression.

As agent memory systems scale, understanding _why_ memory retrieval improved or degraded a response is critical for product iteration. Standard APM tools are blind to this layer.

### How It Would Work

An optional package `AgentMemory.Observability.Opik` wraps the extraction and context assembly pipeline to emit Opik-compatible traces.

Each memory operation (extraction, enrichment, context assembly, conflict detection) becomes an Opik span with:
- Input prompt / retrieved context
- Output (extracted entities, assembled context window)
- Token counts and latency
- Confidence scores from extraction
- User feedback signal (if provided by the consuming application)

The package hooks into the existing pipeline via `IExtractionService` and `IMemoryContextAssembler` decorator pattern — no changes to core logic required.

```csharp
builder.AddAgentMemory(options =>
    options.UseOpikObservability(apiKey: "...", projectName: "agent-memory-prod"));
```

### Implementation Sketch

- **New package:** `AgentMemory.Observability.Opik`
- **Pattern:** Decorator over `IExtractionService` and `IMemoryContextAssembler`
- **SDK dependency:** Awaiting official Comet ML .NET SDK (currently Python/JS only)
- **Fallback plan:** If no official SDK, implement a thin HTTP client against the Opik REST API directly

### Why Deferred

No official .NET Opik SDK exists as of 2026-04-30. Comet ML's .NET support roadmap is unclear. A hand-rolled HTTP client is possible but creates a fragile dependency on an undocumented internal API. Revisit when Comet ML ships official .NET support, or when a community-maintained .NET Opik client reaches stability.

---

## 7. Full CLI Tool Feature Set

### What Ships in v1

The v1 CLI (`dotnet-agent-memory` tool) includes two commands:
- `migrate` — apply Neo4j schema migrations
- `schema-check` — validate the current schema against expected state

This is intentionally scoped: the CLI is infrastructure, not a product feature.

### What's Deferred

| Command | Description |
|---|---|
| `export-memory` | Dump a session's memory graph as portable JSON (entities, facts, messages, preferences) |
| `import-memory` | Restore a previously exported memory graph into a new or existing session |
| `stats` | Print memory graph statistics: node counts by type, relationship counts, vector index sizes, session activity |
| `prune` | Manually trigger memory decay for a session or globally, with dry-run mode |
| `search` | Ad-hoc semantic search from the command line: `dotnet-agent-memory search "what does José prefer for breakfast"` |

### Implementation Sketch

All commands use the existing `IMemoryService` and repository interfaces — no new core logic required. The work is purely CLI plumbing: argument parsing (System.CommandLine), output formatting (JSON / table), and authentication (connection string from env or config).

`export-memory` and `import-memory` together enable memory portability across environments (dev → staging → prod), which is valuable for debugging and testing.

### Why Deferred

The MCP server already provides most of these capabilities for developer use via tool calls. The CLI is an ops convenience, not a differentiator. Build after v1 once real operators provide feedback on what they actually need at the command line.

---

## 8. Additional Framework Integrations (AutoGen.NET / LangChain.NET / Semantic Router)

### AutoGen.NET (Microsoft Agent Framework)

AutoGen.NET has been renamed and absorbed into Microsoft Agent Framework (MAF). **Any AutoGen.NET integration IS the existing MAF adapter.** No separate work is needed here — the `AgentMemory.AgentFramework` package covers this. Close this item.

### LangChain.NET

LangChain.NET (`tryAGI/semantic-kernel-LangChain`) is active but occupies a niche in the .NET ecosystem. The majority of .NET agent developers use Semantic Kernel or MAF. Building a LangChain.NET adapter delivers lower ROI than deepening SK/MAF integration.

**Implementation sketch if pursued:** Implement `IChainMemory` from LangChain.NET's memory interface, backed by `IMemoryService`. Straightforward adapter — ~200 LOC.

**Revisit based on:** community demand signal (GitHub issues, Discord). If 3+ users request it explicitly, the effort is justified.

### Semantic Router

[Aurelio AI's Semantic Router](https://github.com/aurelio-labs/semantic-router) (Python) uses semantic similarity to route inputs to handlers. The .NET ecosystem has no direct equivalent, but the concept maps to `IMemoryContextAssembler` used as a route scorer: "given this input, which memory namespace / agent handler is most relevant?"

**Implementation sketch:** A `SemanticRouterAdapter` that wraps `HybridRetriever` and exposes a `RouteAsync(string input) → string routeName` API. Interesting, speculative. The value depends heavily on the consuming application's architecture.

**Why deferred:** No .NET Semantic Router library to integrate against. This would be a net-new capability, not an integration. Revisit when the pattern matures in .NET.

---

## How to Promote an Item

When an item is ready for active development:

1. **Deckard does a design review** — read the implementation sketch, update if stale, identify any blocking dependencies
2. **Add a row to `docs/nextsteps.md`** Proposal Priority Matrix with current impact / effort / risk scores
3. **Move the expanded description to an `architecture/` ADR** if the item carries significant design decisions (schema changes, new public API surfaces, new package boundaries)
4. **Remove from this backlog** once the ADR exists and the item is tracked in the active roadmap

Items should not be promoted speculatively — wait for a real demand signal (user requests, observed pain points, or a dependency item completing).
