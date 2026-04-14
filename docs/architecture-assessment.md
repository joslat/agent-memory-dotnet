# Architecture Assessment & Ecosystem Strategy

**Author:** Roy (Core Memory Domain Engineer)  
**Requested by:** Jose Luis Latorre Millas  
**Date:** July 2026  
**Scope:** Full architecture audit + .NET AI ecosystem positioning

---

## Table of Contents

1. [Current Architecture Diagram](#1-current-architecture-diagram)
2. [Project Reference Map](#2-project-reference-map)
3. [Cross-Reference Matrix](#3-cross-reference-matrix)
4. [Boundary & Clean Architecture Assessment](#4-boundary--clean-architecture-assessment)
5. [Architecture Recommendations](#5-architecture-recommendations)
6. [Ecosystem Strategy](#6-ecosystem-strategy)
7. [Actionable Next Steps](#7-actionable-next-steps)

---

## 1. Current Architecture Diagram

### Actual Dependency Graph (verified from .csproj files)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           ADAPTERS / EDGE LAYER                             │
│                                                                             │
│  ┌───────────────────┐  ┌───────────────────┐  ┌────────────────────────┐  │
│  │  AgentFramework    │  │  GraphRagAdapter   │  │  McpServer             │  │
│  │  MAF 1.1.0 adapter │  │  neo4j-maf-bridge  │  │  MCP 1.2.0 tools       │  │
│  │  → Abstractions    │  │  → Abstractions    │  │  → Abstractions        │  │
│  │  → Core            │  │  → neo4j-maf-prov  │  │                        │  │
│  │  + M.Agents.AI     │  │  + M.E.AI          │  │  + ModelContextProtocol│  │
│  │  + M.E.AI          │  │  + Neo4j.Driver *   │  │  + M.E.Hosting         │  │
│  └────────┬───────────┘  └────────┬───────────┘  └────────┬───────────────┘  │
│           │                       │                       │                  │
├───────────┼───────────────────────┼───────────────────────┼──────────────────┤
│           │         EXTENSION / CROSS-CUTTING LAYER       │                  │
│                                                                             │
│  ┌───────────────────┐  ┌───────────────────┐  ┌────────────────────────┐  │
│  │  Extraction.Llm   │  │  Extraction.Azure  │  │  Observability         │  │
│  │  → Abstractions   │  │  → Abstractions    │  │  → Abstractions        │  │
│  │  → Core           │  │  + Azure.AI.Text   │  │  → Core                │  │
│  │  + M.E.AI         │  │                    │  │  + OpenTelemetry.Api   │  │
│  └────────┬──────────┘  └────────┬───────────┘  └────────┬───────────────┘  │
│           │                      │                       │                  │
│  ┌────────┴──────────────────────┴───────────────────────┘                  │
│  │  Enrichment  → Abstractions + M.E.Http + M.E.Caching.Memory             │
│  └────────┬──────────────────────────────────────────────                   │
│           │                                                                 │
├───────────┼─────────────────────────────────────────────────────────────────┤
│           │                 PERSISTENCE LAYER                               │
│  ┌────────┴──────────┐                                                      │
│  │  Neo4j             │  → Abstractions, → Core                             │
│  │  Repositories,     │  + Neo4j.Driver 6.0.0                               │
│  │  Schema, Queries   │                                                     │
│  └────────┬──────────┘                                                      │
│           │                                                                 │
├───────────┼─────────────────────────────────────────────────────────────────┤
│           │                 ORCHESTRATION LAYER                              │
│  ┌────────┴──────────┐                                                      │
│  │  Core              │  → Abstractions only                                │
│  │  Services,         │  + FuzzySharp 2.0.2                                 │
│  │  Resolution,       │  + M.E.DI/Logging/Options                           │
│  │  Validation        │                                                     │
│  └────────┬──────────┘                                                      │
│           │                                                                 │
├───────────┼─────────────────────────────────────────────────────────────────┤
│           │                 FOUNDATION LAYER                                │
│  ┌────────┴──────────┐                                                      │
│  │  Abstractions      │  ZERO external dependencies                         │
│  │  Domain models,    │  Pure C# 13 records + interfaces                    │
│  │  Service contracts │                                                     │
│  └───────────────────┘                                                      │
└─────────────────────────────────────────────────────────────────────────────┘

(* GraphRagAdapter uses Neo4j.Driver transitively via neo4j-maf-provider ProjectReference)
```

### Simplified Dependency Flow

```
Abstractions ← Core ← Neo4j
                 ↑        ↑
                 │        └── (Integration Tests)
                 │
          ┌──────┼──────────────┐
          │      │              │
    AgentFramework  Extraction.Llm  Observability
          │
          │
  GraphRagAdapter ← neo4j-maf-provider (external submodule)
          │
  Extraction.AzureLanguage (Abstractions only)
  Enrichment (Abstractions only)
  McpServer (Abstractions only)
```

---

## 2. Project Reference Map

### Source Projects (10)

| # | Project | References (ProjectReference) | Key PackageReferences | Purpose |
|---|---------|------------------------------|----------------------|---------|
| 1 | **Abstractions** | *none* | *none* | Domain models, service interfaces, repository contracts. Zero-dependency foundation. |
| 2 | **Core** | Abstractions | FuzzySharp, M.E.DI/Logging/Options | Business logic: services, entity resolution, validation, stubs, extraction pipeline. |
| 3 | **Neo4j** | Abstractions, Core | Neo4j.Driver 6.0.0, M.E.DI/Logging/Options | Persistence adapter: repositories, schema bootstrapping, Cypher queries. |
| 4 | **AgentFramework** | Abstractions, Core | M.Agents.AI.Abstractions 1.1.0, M.E.AI 10.4.1, M.E.DI/Logging/Options | Microsoft Agent Framework adapter: context provider, type mappers, session bridge. |
| 5 | **GraphRagAdapter** | Abstractions, neo4j-maf-provider (ProjectRef) | M.E.AI 10.4.1, M.E.DI/Logging/Options | Bridge to neo4j-maf-provider IRetriever for vector/fulltext/hybrid search. |
| 6 | **Extraction.Llm** | Abstractions, Core | M.E.AI 10.4.1, M.E.DI/Logging/Options | LLM-based entity/fact/preference/relationship extraction via IChatClient. |
| 7 | **Extraction.AzureLanguage** | Abstractions | Azure.AI.TextAnalytics 5.3.0, M.E.DI/Logging/Options | Azure Cognitive Services extraction: entity recognition, relationship extraction. |
| 8 | **Enrichment** | Abstractions | M.E.Http, M.E.Caching.Memory, M.E.DI/Logging/Options | Entity enrichment: geocoding, HTTP, caching, rate limiting. |
| 9 | **Observability** | Abstractions, Core | OpenTelemetry.Api 1.12.0, M.E.DI/Logging | Decorator-pattern instrumentation: traces, metrics, structured logging. |
| 10 | **McpServer** | Abstractions | ModelContextProtocol 1.2.0, M.E.Hosting | MCP tool definitions: 18 tools for memory operations via Model Context Protocol. |

### Test Projects (2)

| Project | References | Key PackageReferences |
|---------|-----------|----------------------|
| **Tests.Unit** | ALL 10 src projects | FluentAssertions, NSubstitute, xUnit, M.E.AI, M.Agents.AI, MCP |
| **Tests.Integration** | Abstractions, Core, Neo4j | Testcontainers.Neo4j, Neo4j.Driver, FluentAssertions, NSubstitute, xUnit |

### Sample Projects (3)

| Project | References |
|---------|-----------|
| **Sample.MinimalAgent** | AgentFramework, Neo4j, Core |
| **Sample.BlendedAgent** | Core, Neo4j, AgentFramework, GraphRagAdapter, Observability |
| **Sample.McpHost** | McpServer, Core, Neo4j |

### External Submodule (1)

| Project | Dependencies | Note |
|---------|-------------|------|
| **neo4j-maf-provider** (Neo4j.AgentFramework.GraphRAG) | M.Agents.AI.Abstractions, Neo4j.Driver | External repo included as Git submodule. Targets net8.0. |

### "Referenced By" Reverse Map

| Project | Referenced By |
|---------|--------------|
| **Abstractions** | Core, Neo4j, AgentFramework, GraphRagAdapter, Extraction.Llm, Extraction.AzureLanguage, Enrichment, Observability, McpServer, Tests.Unit, Tests.Integration |
| **Core** | Neo4j, AgentFramework, Extraction.Llm, Observability, Tests.Unit, Tests.Integration, Sample.MinimalAgent, Sample.BlendedAgent, Sample.McpHost |
| **Neo4j** | Tests.Unit, Tests.Integration, Sample.MinimalAgent, Sample.BlendedAgent, Sample.McpHost |
| **AgentFramework** | Tests.Unit, Sample.MinimalAgent, Sample.BlendedAgent |
| **GraphRagAdapter** | Tests.Unit, Sample.BlendedAgent |
| **Extraction.Llm** | Tests.Unit |
| **Extraction.AzureLanguage** | Tests.Unit |
| **Enrichment** | Tests.Unit |
| **Observability** | Tests.Unit, Sample.BlendedAgent |
| **McpServer** | Tests.Unit, Sample.McpHost |

---

## 3. Cross-Reference Matrix

Each cell: ✅ (references) · ❌ (doesn't) · ⚠️ (probably shouldn't) · 🔶 (should consider adding)

### Source Project → Source Project References

| Project | Abstractions | Core | Neo4j | AgentFramework | GraphRag | McpServer | AzureLanguage | Extraction.Llm | Enrichment | Observability |
|---------|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **Abstractions** | — | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Core** | ✅ | — | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Neo4j** | ✅ | ✅ | — | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **AgentFramework** | ✅ | ✅ | ❌ | — | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **GraphRagAdapter** | ✅ | ❌ | ❌ | ❌ | — | ❌ | ❌ | ❌ | ❌ | ❌ |
| **McpServer** | ✅ | ❌ | ❌ | ❌ | ❌ | — | ❌ | ❌ | ❌ | ❌ |
| **Extraction.Azure** | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | — | ❌ | ❌ | ❌ |
| **Extraction.Llm** | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | — | ❌ | ❌ |
| **Enrichment** | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | — | ❌ |
| **Observability** | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | — |

### Verification: No Circular Dependencies

Dependency direction flows strictly downward:

```
Layer 4 (Adapters):     AgentFramework, GraphRagAdapter, McpServer
Layer 3 (Extensions):   Extraction.Llm, Extraction.Azure, Enrichment, Observability
Layer 2 (Persistence):  Neo4j
Layer 1 (Orchestration): Core
Layer 0 (Foundation):    Abstractions
```

**Result: ✅ Zero circular dependencies. Zero inappropriate cross-references.**

---

## 4. Boundary & Clean Architecture Assessment

### 4.1 Abstractions Layer — ✅ CLEAN

| Criteria | Status | Evidence |
|----------|--------|---------|
| Zero external PackageReferences | ✅ Pass | .csproj has no PackageReference items |
| No Neo4j.Driver imports | ✅ Pass | grep confirms zero `using Neo4j.Driver` |
| No Microsoft.Agents.* imports | ✅ Pass | grep confirms zero `using Microsoft.Agents` |
| No implementation code | ✅ Pass | Contains only records, interfaces, enums, options |
| Pure domain types | ✅ Pass | 26 domain types, 15 service interfaces, 10 repository interfaces, 9 options types |

**Verdict:** The Abstractions layer is exemplary. Zero dependencies, clean contracts, proper separation.

### 4.2 Core Layer — ✅ CLEAN

| Criteria | Status | Evidence |
|----------|--------|---------|
| Depends only on Abstractions | ✅ Pass | Single ProjectReference to Abstractions |
| No Neo4j.Driver imports | ✅ Pass | grep confirms zero `using Neo4j.Driver` |
| No Microsoft.Agents.* imports | ✅ Pass | grep confirms zero `using Microsoft.Agents` |
| No Neo4j namespace imports | ✅ Pass | grep confirms zero `using Neo4j.AgentMemory.Neo4j` |
| Framework-agnostic logic | ✅ Pass | Contains services, resolution chain, validation, stubs |
| External deps minimal | ✅ Pass | Only FuzzySharp (string matching) + M.E.* (DI plumbing) |

**Verdict:** Core is truly framework-agnostic. Business logic is isolated from persistence and framework concerns.

### 4.3 Neo4j Persistence Layer — ✅ CLEAN

| Criteria | Status | Evidence |
|----------|--------|---------|
| References Abstractions + Core only | ✅ Pass | Two ProjectReferences, both correct |
| Implements repository interfaces | ✅ Pass | 9 repository implementations + schema bootstrapper |
| No upward dependencies | ✅ Pass | Does not reference AgentFramework, McpServer, etc. |
| Cypher centralized | ⚠️ Partial | Queries/ directory exists but queries still inline in some repos (F3 finding) |

**Verdict:** Proper persistence adapter. The Cypher centralization gap (F3) is a code quality issue, not an architecture violation.

### 4.4 AgentFramework Adapter — ✅ CLEAN

| Criteria | Status | Evidence |
|----------|--------|---------|
| Thin translation layer | ✅ Pass | ~300 LOC, context provider + mappers + DI + tools |
| No Neo4j.Driver imports | ✅ Pass | grep confirms zero `using Neo4j.Driver` |
| No Neo4j namespace imports | ✅ Pass | grep confirms zero `using Neo4j.AgentMemory.Neo4j` |
| MAF types don't leak outward | ✅ Pass | Internal mappers in Mapping/ namespace |
| Delegates to Core/Abstractions | ✅ Pass | Uses IMemoryService, IEmbeddingProvider |

**Verdict:** The MAF adapter properly wraps Core without leaking framework concerns. D11 (Thin Adapter) decision is well-enforced.

### 4.5 GraphRagAdapter — ✅ CLEAN (with caveat)

| Criteria | Status | Evidence |
|----------|--------|---------|
| Bridges to neo4j-maf-provider | ✅ Pass | Uses IRetriever from Neo4j.AgentFramework.GraphRAG |
| Implements IGraphRagContextSource | ✅ Pass | Clean interface implementation from Abstractions |
| No tight coupling to memory engine | ✅ Pass | Only depends on Abstractions (not Core or Neo4j) |
| Neo4j.Driver used internally | ✅ Expected | IDriver injected for direct retriever construction |

**Caveat:** The ProjectReference to neo4j-maf-provider points to a source checkout (`..\..\Neo4j\neo4j-maf-provider\`). For NuGet publishing, this must become a PackageReference to a published `Neo4j.AgentFramework.GraphRAG` NuGet package. Additionally, neo4j-maf-provider targets **net8.0** while our projects target **net9.0** — this works via backward compatibility but is a version asymmetry to track.

### 4.6 McpServer — ✅ CLEAN

| Criteria | Status | Evidence |
|----------|--------|---------|
| Only depends on Abstractions | ✅ Pass | Single ProjectReference to Abstractions |
| No Core reference | ✅ Pass | Tool definitions resolve services via DI at runtime |
| No Neo4j reference | ✅ Pass | Pure interface-driven |
| Clean MCP tool definitions | ✅ Pass | 18 tools across 6 tool classes |

**Verdict:** McpServer is properly isolated. It defines tools against abstractions and relies on DI for runtime binding. This is the correct approach — the host application (Sample.McpHost) wires up Core + Neo4j, not the server itself.

### 4.7 Extraction Packages — ✅ CLEAN

| Package | Scope | Status |
|---------|-------|--------|
| **Extraction.Llm** | Abstractions + Core | ✅ Correct — needs Core for extraction pipeline registration |
| **Extraction.AzureLanguage** | Abstractions only | ✅ Correct — implements extractor interfaces directly |

Both extraction packages are properly scoped. The asymmetry (Llm needs Core, Azure doesn't) is justified: Llm extraction uses Core's extraction infrastructure while Azure provides raw extractors.

### 4.8 Enrichment — ✅ CLEAN

Depends only on Abstractions. Implements enrichment services with HTTP + caching infrastructure. No business logic leakage.

### 4.9 Observability — ✅ CLEAN

Depends on Abstractions + Core. Uses decorator pattern (InstrumentedMemoryService, InstrumentedGraphRagContextSource) with OpenTelemetry.Api only. Correct — decorators need access to the types they wrap.

---

## 5. Architecture Recommendations

### 5.1 No Boundary Violations Found

After examining all 10 source projects, 2 test projects, and 3 sample projects:

- **Zero circular dependencies**
- **Zero inappropriate upward references**
- **Zero framework leakage** (no MAF types in Core/Abstractions, no Neo4j types in Core/Abstractions)
- **All dependency arrows point downward** through the layer stack

The architecture is well-executed. The ports-and-adapters pattern is consistently applied.

### 5.2 Issues to Address

| ID | Issue | Severity | Description |
|----|-------|----------|-------------|
| A1 | neo4j-maf-provider is a ProjectReference | **Medium** | Must become PackageReference for NuGet publishing. Blocks GraphRagAdapter NuGet package. |
| A2 | net8.0 vs net9.0 asymmetry | **Low** | neo4j-maf-provider targets net8.0; all our projects target net9.0. Works but worth aligning. |
| A3 | Unit test project references ALL src projects | **Low** | Tests.Unit references all 10 src projects. Consider splitting into per-project test assemblies for faster CI. Not blocking, but unusual for a project of this scale. |
| A4 | McpServer MCP tools resolve services at runtime | **Info** | McpServer depends only on Abstractions. Tools use `[FromServices]` DI resolution. Host must wire up Core + Neo4j. This is architecturally correct but could surprise consumers. Document clearly. |
| A5 | Cypher query centralization incomplete | **Low** | Finding F3 — some queries still inline in repositories. Code quality, not architecture. |

### 5.3 Strengths

1. **Abstractions layer is pristine** — zero dependencies, clean contracts, 60+ types
2. **Core is truly portable** — could be backed by PostgreSQL, Cosmos, or any other persistence store
3. **Each adapter is independently replaceable** — swap MAF for Semantic Kernel, swap Neo4j for Cosmos
4. **Extension packages are orthogonal** — extraction, enrichment, observability are all opt-in
5. **InternalsVisibleTo used judiciously** — enables testing without polluting public API

---

## 6. Ecosystem Strategy

### 6.1 Current .NET AI Landscape

| Framework | Maturity | User Base | Our Status |
|-----------|----------|-----------|------------|
| **Microsoft Agent Framework (MAF)** 1.1.0 | Post-GA, stabilizing | Growing (Microsoft-backed) | ✅ Adapter exists |
| **Semantic Kernel** | GA, v1.x stable | **Very large** (most popular .NET AI framework) | ❌ No adapter |
| **AutoGen (.NET)** | Preview/Experimental | Small (mostly Python users) | ❌ No adapter |
| **LangChain.NET** | Community port | Small | ❌ Not a priority |
| **Microsoft.Extensions.AI** | GA (10.x) | Broad (standard library tier) | ⚠️ Partially used (IChatClient in Extraction.Llm) |
| **Neo4j .NET Driver** | GA 6.0.0 | Established | ✅ Used in Neo4j layer |
| **neo4j-maf-provider** | Pre-release | Emerging | ✅ Bridged via GraphRagAdapter |

### 6.2 Strategic Recommendations

#### R1: Build a Semantic Kernel Adapter — HIGH PRIORITY

**What:** Create `Neo4j.AgentMemory.SemanticKernel` package with SK plugin/filter integration.

**Why:**
- Semantic Kernel has **significantly larger adoption** than MAF in the .NET ecosystem
- SK is the de facto standard for .NET AI orchestration (>10K GitHub stars, heavy enterprise adoption)
- SK's plugin model (`KernelFunction`) and filter pipeline (`IFunctionInvocationFilter`) map naturally to our memory operations
- The Python agent-memory equivalent supports 7+ frameworks; .NET currently supports only MAF + MCP
- SK users are the most likely early adopters of a .NET memory library
- Our clean architecture makes this straightforward: create a thin adapter like AgentFramework

**Integration pattern:**
```
Neo4j.AgentMemory.SemanticKernel
  → Abstractions, Core
  + Microsoft.SemanticKernel.Abstractions
  
  Classes:
  - MemoryPlugin : KernelPlugin (exposes memory ops as SK functions)
  - MemoryAutoRecallFilter : IFunctionInvocationFilter (auto-inject context)
  - SKTypeMapper (ChatMessageContent ↔ Message mapping)
  - ServiceCollectionExtensions.AddAgentMemoryForSemanticKernel()
```

| Attribute | Value |
|-----------|-------|
| **Impact** | 🔴 High — unlocks largest .NET AI user base |
| **Effort** | 🟡 Medium (M) — similar scope to AgentFramework adapter |
| **Dependencies** | None — Abstractions + Core are ready |
| **Risks** | SK API is stable but evolves. Pin to SK Abstractions package to minimize coupling. |

#### R2: Provide Microsoft.Extensions.AI Integration — HIGH PRIORITY

**What:** Ensure our `IEmbeddingProvider` interface can be backed by `IEmbeddingGenerator<string, Embedding<float>>` from M.E.AI, and provide a bridge adapter.

**Why:**
- M.E.AI is becoming the **unified AI abstraction layer** for .NET (shipped with .NET 10)
- It provides vendor-neutral interfaces for chat, embeddings, and tool calling
- We already use M.E.AI in Extraction.Llm, AgentFramework, and GraphRagAdapter
- Our `IEmbeddingProvider` interface predates M.E.AI stabilization; bridging it would let any M.E.AI embedding backend (OpenAI, Ollama, Azure, etc.) plug in without custom adapters
- This is the lowest-friction integration and benefits ALL consumers (not just SK or MAF users)

**Integration pattern:**
```
Option A: Bridge adapter in Core (or new Neo4j.AgentMemory.Extensions.AI package)
  - MEAIEmbeddingProviderAdapter : IEmbeddingProvider (wraps IEmbeddingGenerator)
  - ServiceCollectionExtensions.AddMEAIEmbeddingProvider()

Option B: Define IEmbeddingProvider in terms of M.E.AI directly
  (Breaking change — deferred unless doing major version bump)
```

| Attribute | Value |
|-----------|-------|
| **Impact** | 🔴 High — reduces onboarding friction for every consumer |
| **Effort** | 🟢 Small (S) — single adapter class + DI extension |
| **Dependencies** | M.E.AI Abstractions (already referenced in several packages) |
| **Risks** | Low. M.E.AI is stable (GA in .NET 10). Adapter approach avoids breaking changes. |

#### R3: Clarify Relationship with neo4j-maf-provider — MEDIUM PRIORITY

**What:** Define whether we consume, fork, or contribute upstream to `neo4j-maf-provider`.

**Why:**
- We currently reference it as a **Git submodule with ProjectReference** — this blocks NuGet publishing of GraphRagAdapter
- neo4j-maf-provider is Neo4j's official MAF integration; we built adapters around its `IRetriever` abstraction
- The adapter layer (our `AdapterVectorRetriever`, etc.) wraps neo4j-maf-provider's types to satisfy our `IGraphRagContextSource` interface
- Three paths forward, each with different implications

**Options:**

| Option | Pros | Cons |
|--------|------|------|
| **A. Consume as NuGet** | Clean dependency, standard. | Blocked until neo4j-maf-provider publishes stable NuGet. |
| **B. Contribute upstream** | Our improvements benefit Neo4j community. We become co-maintainers. | Requires coordination. Our adapter patterns may not align with neo4j-maf-provider's vision. |
| **C. Fork internally** | Full control, no external dependency. | Maintenance burden. Diverges from upstream. |

**Recommendation:** **Option A (consume as NuGet) + Option B (contribute upstream).**
- Short term: keep ProjectReference for development velocity.
- Medium term: when neo4j-maf-provider publishes stable NuGet, switch GraphRagAdapter to PackageReference.
- Contribute our `AdapterVectorRetriever`/`AdapterHybridRetriever` patterns upstream as examples or extensions.

| Attribute | Value |
|-----------|-------|
| **Impact** | 🟡 Medium — unblocks NuGet publishing for GraphRagAdapter |
| **Effort** | 🟡 Medium (M) — coordination + PR work |
| **Dependencies** | neo4j-maf-provider NuGet publishing timeline |
| **Risks** | External dependency on Neo4j's publishing schedule. |

#### R4: Defer AutoGen Adapter — LOW PRIORITY

**What:** Do NOT build an AutoGen (.NET) adapter now.

**Why:**
- AutoGen .NET is experimental/preview — API instability is high
- AutoGen's primary community is Python; .NET port has limited adoption
- The multi-agent patterns in AutoGen overlap with MAF's agent orchestration
- If AutoGen .NET stabilizes and gains traction, revisit in 6–12 months

| Attribute | Value |
|-----------|-------|
| **Impact** | 🟢 Low — small user base, experimental API |
| **Effort** | 🟡 Medium (M) — API instability means frequent maintenance |
| **Dependencies** | AutoGen .NET reaching stable release |
| **Risks** | Building on unstable API → constant breakage. |

#### R5: Defer LangChain.NET — NOT A PRIORITY

**What:** Do NOT build a LangChain.NET adapter.

**Why:**
- LangChain.NET is a community port with limited adoption
- The .NET ecosystem has converged on Semantic Kernel + MAF as the primary frameworks
- LangChain is primarily a Python ecosystem tool
- If a .NET developer is choosing LangChain.NET, they likely need Python LangChain instead

| Attribute | Value |
|-----------|-------|
| **Impact** | 🟢 Low |
| **Effort** | 🟡 Medium |
| **Risks** | Wasted effort on dying ecosystem. |

#### R6: Define NuGet Package Publish Order — HIGH PRIORITY

**What:** Publish packages in strict dependency order for the initial NuGet release.

**Why:** Each tier depends only on previously published tiers. Circular dependency issues are impossible if we follow the order.

**Recommended publish order:**

```
Wave 1 (Foundation):
  1. Neo4j.AgentMemory.Abstractions
  2. Neo4j.AgentMemory.Core

Wave 2 (Persistence + Extensions):
  3. Neo4j.AgentMemory.Neo4j
  4. Neo4j.AgentMemory.Extraction.Llm
  5. Neo4j.AgentMemory.Extraction.AzureLanguage
  6. Neo4j.AgentMemory.Enrichment
  7. Neo4j.AgentMemory.Observability

Wave 3 (Adapters):
  8. Neo4j.AgentMemory.AgentFramework
  9. Neo4j.AgentMemory.McpServer
  10. Neo4j.AgentMemory.GraphRagAdapter (blocked on neo4j-maf-provider NuGet)

Wave 4 (Convenience):
  11. Neo4j.AgentMemory (meta-package: Abstractions + Core + Neo4j + Extraction.Llm)

Wave 5 (New adapters):
  12. Neo4j.AgentMemory.SemanticKernel (after R1 implementation)
```

| Attribute | Value |
|-----------|-------|
| **Impact** | 🔴 High — enables community adoption |
| **Effort** | 🟡 Medium (M) — CI/CD pipeline + NuGet metadata |
| **Dependencies** | NuGet API keys, CI pipeline, package signing |
| **Risks** | Premature publish of unstable APIs. Use `-preview` suffixes initially. |

#### R7: Developer Adoption Story — HIGH PRIORITY

**What:** Define the "5-minute getting started" experience for each consumer persona.

**Personas and their onramps:**

| Persona | Package(s) | Getting Started |
|---------|-----------|-----------------|
| **SK developer** (largest audience) | `Neo4j.AgentMemory.SemanticKernel` + `Neo4j.AgentMemory.Neo4j` | `kernel.ImportPluginFromType<MemoryPlugin>()` |
| **MAF developer** | `Neo4j.AgentMemory.AgentFramework` + `Neo4j.AgentMemory.Neo4j` | `services.AddAgentMemory().AddNeo4jPersistence()` |
| **MCP tool user** | `Neo4j.AgentMemory.McpServer` + `Neo4j.AgentMemory.Neo4j` + `Neo4j.AgentMemory.Core` | `builder.AddAgentMemoryMcpTools()` |
| **Custom framework** | `Neo4j.AgentMemory.Core` + `Neo4j.AgentMemory.Neo4j` | `services.AddAgentMemoryCore().AddNeo4jPersistence()` |
| **Library author** | `Neo4j.AgentMemory.Abstractions` | Reference contracts, implement against interfaces |

**Key insight:** The Semantic Kernel persona represents the **largest addressable audience** in .NET AI today. Without R1, we miss the primary adoption channel.

| Attribute | Value |
|-----------|-------|
| **Impact** | 🔴 High — determines whether developers actually try the library |
| **Effort** | 🟢 Small (S) — documentation + sample projects |
| **Dependencies** | R1 (SK adapter) for the largest persona |
| **Risks** | None. Pure documentation effort. |

### 6.3 Priority Matrix

```
                    HIGH IMPACT
                        │
         R1 (SK)        │   R6 (NuGet Publish)
         R2 (M.E.AI)    │   R7 (Adoption Story)
                        │
  LOW EFFORT ───────────┼─────────── HIGH EFFORT
                        │
         R3 (neo4j-maf) │   R4 (AutoGen)
                        │
                        │   R5 (LangChain.NET)
                    LOW IMPACT
```

### 6.4 Recommended Execution Order

| Priority | Recommendation | Why First |
|----------|---------------|-----------|
| 🥇 1st | R2: M.E.AI bridge adapter | Smallest effort, benefits every consumer. Ship in days. |
| 🥈 2nd | R6: NuGet package publishing | Enables any external adoption at all. |
| 🥉 3rd | R1: Semantic Kernel adapter | Unlocks the largest .NET AI audience. |
| 4th | R7: Developer adoption docs | Makes the above three discoverable. |
| 5th | R3: neo4j-maf-provider resolution | Unblocks GraphRagAdapter NuGet. |
| Deferred | R4: AutoGen, R5: LangChain.NET | Wait for ecosystem stabilization. |

---

## 7. Actionable Next Steps

### Immediate (This Sprint)

1. **Create M.E.AI embedding bridge** — Single `MEAIEmbeddingProviderAdapter` class in Core (or small extension package) that wraps `IEmbeddingGenerator<string, Embedding<float>>` as `IEmbeddingProvider`. Register via DI extension. (~1 day)

2. **Add NuGet package metadata** — Add `<PackageId>`, `<Description>`, `<PackageTags>`, `<Authors>`, `<PackageLicenseExpression>`, `<RepositoryUrl>` to all 10 src .csproj files. (~half day)

3. **Create CI/CD pipeline for NuGet** — GitHub Actions workflow: build → test → pack → push to NuGet (or GitHub Packages for preview). (~1-2 days)

### Short-Term (Next 2 Sprints)

4. **Implement Semantic Kernel adapter** — New `Neo4j.AgentMemory.SemanticKernel` project with `MemoryPlugin`, `MemoryAutoRecallFilter`, type mappers, DI extensions. (~1 week)

5. **Write getting-started guides** — Per-persona quickstart docs: SK, MAF, MCP, Custom. Sample projects for each. (~3-4 days)

6. **Publish preview NuGet packages** — Waves 1–3 as `0.1.0-preview.1`. Gather community feedback. (~1 day after CI/CD is ready)

### Medium-Term (Next Quarter)

7. **Resolve neo4j-maf-provider packaging** — Coordinate with Neo4j team on NuGet publishing. Switch ProjectReference to PackageReference. Publish GraphRagAdapter NuGet. (~dependent on external timeline)

8. **Consider meta-package** — After individual packages are stable, publish `Neo4j.AgentMemory` convenience package. (~1 day)

9. **Evaluate AutoGen .NET** — Revisit if/when AutoGen .NET exits preview. (~quarterly check-in)

---

*This assessment reflects the codebase as of July 2026. All recommendations should be revisited if the .NET AI ecosystem undergoes significant shifts (e.g., SK/MAF merge, AutoGen .NET GA, new Microsoft framework).*
