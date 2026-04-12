# Neo4j Agent Memory for .NET — Full Implementation Plan

> **Governing specification:** [Agent-Memory-for-DotNet-Specification.md](Agent-Memory-for-DotNet-Specification.md)  
> This implementation plan is derived from and governed by the specification above. If any ambiguity exists between this plan and the specification, the specification takes precedence.

## 1. Executive Decision

### 1.1 Direct answer to the GraphRAG question

Earlier, `Neo4j.AgentMemory.GraphRagAdapter` was called **optional** in a strict architectural sense:

- A **minimum viable Neo4j Memory Provider** can exist without GraphRAG.
- The **memory provider** and the **GraphRAG provider** solve different problems.
- Microsoft documents them as **two separate integrations** for Agent Framework.

That said, for **this implementation**, the GraphRAG adapter should be treated as a **required first-class component** because the target architecture should support both:

1. **persistent agent memory** that is built from conversations over time, and
2. **GraphRAG retrieval** from an existing curated knowledge graph.

### 1.2 Final architecture decision

We will build:

1. **A native .NET Neo4j Memory Provider for Agent Framework**, functionally aligned with `neo4j-agent-memory`.
2. **A separate but required .NET GraphRAG adapter**, reusing the existing .NET Neo4j GraphRAG Context Provider package and patterns.
3. **A .NET MCP server on top of the memory system**, implemented after the memory core and MAF integration are stable.

In short:

- **Memory Provider** = the main product.
- **GraphRAG adapter** = required interoperability component.
- **MCP server** = useful external access layer, but built after the core.

### 1.3 Reference project locations in this workspace

Both reference projects are checked out under the `/Neo4j` folder in this repository:

- **`/Neo4j/agent-memory/`** — the Python `neo4j-agent-memory` project. This is the conceptual reference for memory models, three-memory architecture, extraction patterns, and integration lifecycle.
- **`/Neo4j/neo4j-maf-provider/`** — the Neo4j MAF Provider project. The .NET implementation that we reuse (GraphRAG Context Provider, MAF integration patterns, test conventions) lives under the **`/Neo4j/neo4j-maf-provider/dotnet/`** subfolder.

These paths are important when cross-referencing existing code, reviewing patterns, or running existing tests during implementation.

---

## 2. What We Are Building

## 2.1 High level

We are building a **production-grade .NET solution** that gives Microsoft Agent Framework agents:

- **persistent memory backed by Neo4j**,
- **automatic extraction of structured memory from conversations**,
- **recall of recent, semantic, and graph-based memory**,
- **reasoning-trace storage and reuse**,
- **optional retrieval from an existing Neo4j knowledge graph via GraphRAG**,
- **optional exposure to external clients through MCP**.

This is **not** just a chat history store.
It is a **graph-native memory subsystem** for agents.

## 2.2 Intermediate level

The solution has three main technical responsibilities:

### A. Memory responsibilities

- Persist conversations and sessions.
- Store long-term entities, preferences, facts, and relationships.
- Store reasoning traces and tool calls.
- Retrieve relevant context before an agent run.
- Update memory after an agent run.

### B. Agent Framework responsibilities

- Integrate memory into the Agent Framework lifecycle.
- Inject context automatically before agent execution.
- Save messages and reasoning outputs after execution.
- Expose memory functionality as agent tools.

### C. Knowledge graph interoperability responsibilities

- Reuse the existing Neo4j GraphRAG .NET provider for retrieval from curated graphs.
- Keep GraphRAG retrieval distinct from persistent conversational memory.
- Support blended context when both memory and GraphRAG are useful.

---

## 3. What This Is Not

The implementation explicitly does **not** include the following in the first version:

- No Python runtime dependency.
- No Python framework integrations.
- No spaCy / GLiNER / GLiREL dependency.
- No line-by-line port of the Python repository.
- No attempt to mirror every Python optional extra.
- No requirement for MCP to use the memory provider in-process with Agent Framework.

---

## 4. Reference Architecture Principles

1. **Framework-agnostic core**
   - The memory engine must not depend on Agent Framework types.

2. **Adapters on top**
   - MAF integration, GraphRAG interoperability, and MCP should sit on top of the core.

3. **Separate responsibilities**
   - Memory creation/persistence is different from retrieval over an existing graph.

4. **Replace Python NLP with .NET-native and provider-based extraction**
   - Use LLM structured extraction first.
   - Add managed/cloud or local ONNX extraction later.

5. **Production-first testability**
   - Everything important must have unit, integration, and end-to-end tests.

6. **No hidden assumptions**
   - Every package, interface, component, and lifecycle hook must be explicitly defined.

---

## 5. External Components and What We Reuse

## 5.1 Neo4j Agent Memory (Python project)

**Source:** `neo4j-labs/agent-memory`
**Workspace location:** `/Neo4j/agent-memory/`

We do **not** reuse code directly.
We reuse:

- the memory concept,
- the three-memory model,
- the integration pattern,
- the useful tool surface,
- the graph-native approach,
- the idea of before/after-run memory lifecycle.

We do **not** copy:

- Python-specific package structure,
- Python NLP stack,
- Python framework adapters,
- Python MCP implementation.

## 5.2 Neo4j GraphRAG Context Provider (.NET)

**Source:** `neo4j-labs/neo4j-maf-provider`, .NET package `Neo4j.AgentFramework.GraphRAG`
**Workspace location:** `/Neo4j/neo4j-maf-provider/dotnet/`

We **do** reuse:

- the package itself,
- the existing GraphRAG provider,
- its search modes,
- its retrieval query model,
- its implementation patterns,
- its test patterns where useful.

We **do not** treat it as the memory provider.
It remains a **separate dependency** and a **separate adapter layer**.

## 5.3 Neo4j official .NET driver

Used for:

- sessions,
- transactions,
- Cypher execution,
- indexes,
- constraints,
- vector search orchestration,
- graph persistence.

## 5.4 Microsoft Agent Framework (.NET)

Used for:

- agent lifecycle integration,
- context provider integration,
- tool registration,
- message/session model integration.

## 5.5 Azure / OpenAI / Microsoft.Extensions.AI

Used for:

- embeddings,
- structured extraction,
- future model abstraction.

## 5.6 Official C# MCP SDK

Used later for:

- exposing memory functionality to external MCP clients,
- stdio / HTTP / SSE server implementations.

---

## 6. Final Solution Structure

```text
Neo4j.AgentMemory.sln
│
├── src/
│   ├── Neo4j.AgentMemory.Abstractions/
│   ├── Neo4j.AgentMemory.Core/
│   ├── Neo4j.AgentMemory.Neo4j/
│   ├── Neo4j.AgentMemory.Extraction.Abstractions/
│   ├── Neo4j.AgentMemory.Extraction.Llm/
│   ├── Neo4j.AgentMemory.Extraction.AzureLanguage/            (phase 5)
│   ├── Neo4j.AgentMemory.Extraction.Onnx/                     (phase 5)
│   ├── Neo4j.AgentMemory.AgentFramework/
│   ├── Neo4j.AgentMemory.GraphRagAdapter/
│   ├── Neo4j.AgentMemory.Mcp/                                 (phase 6)
│   ├── Neo4j.AgentMemory.Observability/                       (phase 4)
│   └── Neo4j.AgentMemory.Cli/                                 (optional late phase)
│
├── tests/
│   ├── Neo4j.AgentMemory.Tests.Unit/
│   ├── Neo4j.AgentMemory.Tests.Integration/
│   ├── Neo4j.AgentMemory.Tests.EndToEnd/
│   ├── Neo4j.AgentMemory.Tests.Performance/
│   └── Neo4j.AgentMemory.Tests.Contract/
│
├── samples/
│   ├── Neo4j.AgentMemory.Sample.MinimalAgent/
│   ├── Neo4j.AgentMemory.Sample.MemoryPlusGraphRag/
│   ├── Neo4j.AgentMemory.Sample.ReasoningTrace/
│   └── Neo4j.AgentMemory.Sample.McpClient/                    (phase 6)
│
├── deploy/
│   ├── docker-compose.dev.yml
│   ├── docker-compose.test.yml
│   ├── docker-compose.e2e.yml
│   └── scripts/
│
└── docs/
    ├── architecture/
    ├── testing/
    ├── operations/
    └── decision-records/
```

---

## 7. Layered Architecture

## 7.1 Layer 1 — Abstractions

This layer defines contracts only.
No Neo4j driver code.
No Agent Framework code.
No provider SDK code.

### Key interfaces

```text
IMemoryService
IShortTermMemoryService
ILongTermMemoryService
IReasoningMemoryService
IMemoryContextAssembler
IMemoryExtractionPipeline
IEntityExtractor
IPreferenceExtractor
IFactExtractor
IRelationExtractor
IEmbeddingProvider
IEntityResolver
IGeocoder
IEnrichmentService
IClock
IIdGenerator
```

## 7.2 Layer 2 — Core

This layer implements memory orchestration and domain logic.
It depends only on abstractions.

### Key responsibilities

- session management
- memory assembly
- deduplication policy
- extraction orchestration
- reasoning-trace orchestration
- merge strategy
- recall strategy

## 7.3 Layer 3 — Neo4j persistence

This layer implements repositories, Cypher queries, indexes, and transaction handling.

### Key responsibilities

- conversations/messages
- entities/preferences/facts/relations
- reasoning traces/steps/tool calls
- vector and fulltext search
- schema bootstrap and migrations

## 7.4 Layer 4 — Adapters

These are required consumers on top of the core.

### Adapters

- Agent Framework adapter
- GraphRAG adapter
- MCP adapter
- Observability adapter

---

## 8. Domain Model

## 8.1 Short-term memory domain

### Conversation
- `ConversationId`
- `SessionId`
- `UserId?`
- `CreatedAt`
- `UpdatedAt`
- `Metadata`

### Message
- `MessageId`
- `ConversationId`
- `Role`
- `Text`
- `CreatedAt`
- `Metadata`
- `Embedding?`
- `ToolCalls?`

## 8.2 Long-term memory domain

### Entity
- `EntityId`
- `Name`
- `CanonicalName`
- `Type`
- `Subtype?`
- `Description?`
- `Aliases[]`
- `Attributes`
- `Embedding?`
- `Confidence`
- `SourceRefs`
- `CreatedAt`

### Preference
- `PreferenceId`
- `Category`
- `PreferenceText`
- `Context?`
- `Confidence`
- `Embedding?`
- `CreatedAt`

### Fact
- `FactId`
- `Subject`
- `Predicate`
- `Object`
- `Confidence`
- `ValidFrom?`
- `ValidUntil?`
- `Embedding?`
- `CreatedAt`

### Relationship
- `RelationshipId`
- `SourceEntityId`
- `TargetEntityId`
- `Type`
- `Confidence`
- `Description?`
- `ValidFrom?`
- `ValidUntil?`

## 8.3 Reasoning memory domain

### ReasoningTrace
- `TraceId`
- `SessionId`
- `Task`
- `TaskEmbedding?`
- `Outcome?`
- `Success?`
- `StartedAt`
- `CompletedAt?`
- `Metadata`

### ReasoningStep
- `StepId`
- `TraceId`
- `StepNumber`
- `Thought?`
- `Action?`
- `Observation?`
- `Embedding?`

### ToolCall
- `ToolCallId`
- `StepId`
- `ToolName`
- `ArgumentsJson`
- `ResultJson?`
- `Status`
- `DurationMs?`
- `Error?`

---

## 9. Neo4j Graph Model

## 9.1 Node labels

```text
Conversation
Message
Entity
Preference
Fact
ReasoningTrace
ReasoningStep
ToolCall
Tool
Extractor
```

## 9.2 Relationship types

```text
HAS_MESSAGE
NEXT_MESSAGE
MENTIONS
RELATED_TO
HAS_PREFERENCE
HAS_FACT
HAS_STEP
USED_TOOL
INITIATED_BY
TRIGGERED_BY
EXTRACTED_FROM
EXTRACTED_BY
SAME_AS
```

## 9.3 Constraints and indexes

### Required constraints
- unique conversation id
- unique message id
- unique entity id
- unique preference id
- unique fact id
- unique trace id
- unique step id
- unique tool call id

### Required vector indexes
- messages.embedding
- entities.embedding
- preferences.embedding
- facts.embedding
- reasoningTrace.taskEmbedding
- reasoningStep.embedding (optional, phase 2)

### Required fulltext indexes
- messages.text
- entities.name/canonicalName/description
- preferences.preference/context
- facts.subject/predicate/object

### Optional indexes
- session id
- created/updated timestamps
- entity type
- tool name

---

## 10. Exact Project Responsibilities

## 10.1 `Neo4j.AgentMemory.Abstractions`

Contains:
- domain contracts
- core service interfaces
- provider option interfaces
- mapping contracts

Must not reference:
- Neo4j.Driver
- Microsoft.Agents.*
- Azure SDKs
- MCP SDK

## 10.2 `Neo4j.AgentMemory.Core`

Contains:
- `MemoryService`
- `ShortTermMemoryCoordinator`
- `LongTermMemoryCoordinator`
- `ReasoningMemoryCoordinator`
- `MemoryContextAssembler`
- `MemoryRecallPlanner`
- `MemoryPolicyEvaluator`
- `MemoryMergeService`
- `PreferenceInferenceService`
- `FactInferenceService`

## 10.3 `Neo4j.AgentMemory.Neo4j`

Contains:
- `Neo4jMemoryStore`
- repositories for each aggregate
- Cypher query builder classes
- schema installer
- migration runner
- vector/fulltext search wrappers

Suggested repositories:
- `ConversationRepository`
- `MessageRepository`
- `EntityRepository`
- `PreferenceRepository`
- `FactRepository`
- `ReasoningTraceRepository`
- `ToolCallRepository`
- `SchemaRepository`

## 10.4 `Neo4j.AgentMemory.Extraction.Abstractions`

Contains:
- `IMessageExtractor`
- `ExtractionRequest`
- `ExtractionResult`
- `ExtractedEntity`
- `ExtractedRelation`
- `ExtractedPreference`
- `ExtractedFact`

## 10.5 `Neo4j.AgentMemory.Extraction.Llm`

Contains:
- `LlmStructuredExtractionPipeline`
- provider-specific prompt templates
- JSON schema validation
- retry / repair logic
- confidence normalization

First version extraction strategy:
- one-pass structured extraction prompt
- separate optional preference/fact inference step if needed
- no Python NLP runtime

## 10.6 `Neo4j.AgentMemory.AgentFramework`

This is the **MAF layer**.
It sits **on top of** the memory core.

Contains:
- `Neo4jMemoryContextProvider`
- `Neo4jChatMessageStore`
- `Neo4jMicrosoftMemory`
- `MemoryToolFactory`
- `AgentTraceRecorder`
- MAF-to-internal model mappers

This package is the only package that should know MAF lifecycle types.

## 10.7 `Neo4j.AgentMemory.GraphRagAdapter`

This is a **required sibling adapter**, not a core dependency.

Contains:
- wrapper around `Neo4j.AgentFramework.GraphRAG`
- `IGraphRagContextSource` implementation
- blended retrieval orchestration hooks
- optional fallback policy if GraphRAG is disabled/unavailable

Responsibilities:
- invoke existing GraphRAG provider
- unify result shape with memory context shape
- keep GraphRAG retrieval separate from memory persistence

## 10.8 `Neo4j.AgentMemory.Mcp`

Built later.
Contains:
- .NET MCP server
- stdio and HTTP transport hosts
- core and extended tool profiles
- DTOs / JSON serialization for MCP tool I/O

---

## 11. MAF Integration Design

## 11.1 Why MAF stays on top

The memory provider should be reusable outside MAF.
Therefore:

- core memory service must not depend on MAF,
- MAF adapter translates MAF lifecycle into memory operations.

## 11.2 MAF integration points

### A. Context Provider

`Neo4jMemoryContextProvider`

Responsibilities:
- run before invocation,
- inspect current user message,
- request assembled memory context,
- optionally request GraphRAG context,
- combine and inject context into the agent run.

### B. Chat Message Store

`Neo4jChatMessageStore`

Responsibilities:
- persist and list messages for a session,
- delegate directly to short-term memory service.

### C. Memory facade

`Neo4jMicrosoftMemory`

Responsibilities:
- convenience API for MAF users,
- expose `GetContext`, `SaveMessage`, `SearchMemory`, `AddPreference`, `AddFact`, etc.

### D. Tools

`MemoryToolFactory`

Responsibilities:
- expose memory operations as agent tools.

Suggested first-wave tools:
- `search_memory`
- `remember_preference`
- `remember_fact`
- `recall_preferences`
- `search_knowledge`
- `find_similar_tasks`

### E. Trace recorder

`AgentTraceRecorder`

Responsibilities:
- record reasoning traces, tool calls, outcomes,
- attach them to session and task context.

## 11.3 MAF lifecycle flow

### Before agent run
1. MAF adapter receives the current request.
2. Current message + session identity are mapped into internal `MemoryRecallRequest`.
3. Memory core retrieves:
   - recent messages,
   - semantically similar messages,
   - relevant entities,
   - preferences,
   - facts,
   - similar reasoning traces.
4. GraphRAG adapter retrieves graph-based domain context if configured.
5. Context assembler creates the final context block.
6. MAF adapter injects the result into the agent run.

### After agent run
1. Save user message.
2. Save assistant response.
3. Run extraction.
4. Merge/update long-term memory.
5. Record reasoning trace and tool calls.
6. Emit metrics and logs.

---

## 12. GraphRAG Adapter Design

## 12.1 Why it is required in this plan

Because the target system must support both:
- persistent memory created from conversations,
- retrieval from an existing curated graph.

## 12.2 Why it is still separate

Because these are different responsibilities:

- Memory provider = creates and evolves memory.
- GraphRAG provider = searches existing graph-backed knowledge.

## 12.3 Adapter responsibilities

`Neo4j.AgentMemory.GraphRagAdapter` will:

- reference `Neo4j.AgentFramework.GraphRAG`,
- create and configure Neo4j GraphRAG providers,
- normalize their results into internal context fragments,
- expose an abstraction the memory context assembler can call.

## 12.4 Internal contract

```text
IGraphRagContextSource
    Task<GraphRagContextResult> GetContextAsync(GraphRagContextRequest request, CancellationToken ct)
```

### Request fields
- session id
- user id
- user query
- top-k
- search mode (vector/fulltext/hybrid)
- retrieval query name or direct query
- enable traversal
- tags/scopes

### Result fields
- context items
- score
- source nodes
- retrieval mode
- query metadata

## 12.5 Blend policy

The memory system should support:

- `MemoryOnly`
- `GraphRagOnly`
- `MemoryThenGraphRag`
- `GraphRagThenMemory`
- `Blended`

The default should be:

- **Blended** when both are enabled.

### Default blended order
1. recent short-term memory
2. relevant long-term memory
3. GraphRAG context
4. similar reasoning traces

This order can be tuned later.

---

## 13. Extraction Strategy (No Python Runtime)

## 13.1 Phase 1 strategy

Use **LLM structured extraction**.

### Why
- simplest to implement in .NET,
- avoids Python NLP runtime,
- supports custom schema quickly,
- supports entities + relations + preferences + facts in one design,
- good fit for Azure OpenAI / OpenAI / Microsoft.Extensions.AI.

## 13.2 Extraction pipeline design

### Input
- session id
- message id
- role
- message text
- surrounding conversation context (optional limited window)

### Output
- extracted entities
- extracted relations
- extracted preferences
- extracted facts
- extraction metadata
- confidence scores

## 13.3 Pipeline steps

1. normalize text
2. run structured extraction prompt
3. validate JSON against schema
4. repair invalid output if necessary
5. normalize types / confidence / aliases
6. deduplicate against existing graph
7. persist changes transactionally

## 13.4 Future extraction backends

### Phase 4
- Azure AI Language NER backend for managed extraction

### Phase 5
- ONNX/local extraction backend if needed

---

## 14. Memory Recall Design

## 14.1 Recall inputs

- current user query
- session id
- user id
- recall mode
- memory types enabled
- GraphRAG enabled or not
- max items per memory type
- thresholds

## 14.2 Recall outputs

- recent conversation section
- relevant past messages section
- relevant entities section
- preference section
- fact section
- GraphRAG section
- similar past tasks section

## 14.3 Recall ranking

### Short-term
- most recent messages
- semantic search over session/global message memory

### Long-term
- semantic entity search
- preference search
- fact search
- optional graph neighborhood expansion

### Reasoning
- semantic task similarity
- success filter
- recency weighting

### GraphRAG
- external provider scores
- mode-specific ranking

## 14.4 Context budget policy

The assembler must enforce a token/character budget.

### Required policies
- max recent messages
- max retrieved messages
- max entities
- max preferences
- max facts
- max GraphRAG items
- max traces
- truncation strategy

---

## 15. Exact Deliverables by Phase

## Phase 0 — Discovery and design lock

### Objective
Freeze architecture and non-goals.

### Deliverables
- architecture decision records
- solution structure
- naming conventions
- package boundaries
- coding standards
- provider configuration model

### Tasks
1. define package map
2. define domain model
3. define interface contracts
4. define graph schema
5. define test strategy
6. define CI expectations

### Exit criteria
- architecture signed off
- interfaces and package boundaries frozen

---

## Phase 1 — Core memory engine

### Objective
Implement the framework-agnostic memory core and Neo4j persistence.

### Deliverables
- abstractions package
- core package
- Neo4j persistence package
- schema bootstrapper
- short-term memory
- long-term memory
- reasoning memory

### Tasks

#### 1. Abstractions
- create domain contracts
- create options contracts
- create core service interfaces

#### 2. Neo4j infrastructure
- driver factory
- session factory
- transaction runner
- schema installer
- migration runner

#### 3. Short-term memory
- create conversation
- add message
- list conversation
- semantic message search
- recent context formatting

#### 4. Long-term memory
- add/update entity
- add preference
- add fact
- add relationship
- search entities/preferences/facts
- dedup hooks

#### 5. Reasoning memory
- start trace
- add step
- record tool call
- complete trace
- find similar traces

#### 6. Context assembly
- implement `MemoryContextAssembler`
- implement token/size budgets
- implement configurable section inclusion

### Exit criteria
- in-process memory engine works without Agent Framework
- all repositories and services have unit and integration tests

---

## Phase 2 — LLM extraction pipeline

### Objective
Implement a .NET-native structured extraction pipeline.

### Deliverables
- extraction abstractions
- LLM extraction package
- extraction orchestration in core
- entity/relationship/preference/fact merge policies

### Tasks
1. define extraction DTOs and JSON schema
2. implement provider-neutral prompt builder
3. implement extraction result validator
4. implement repair/retry path
5. implement entity normalization
6. implement preference/fact inference logic
7. integrate extraction into post-message workflow
8. implement provenance storage hooks

### Exit criteria
- user message and assistant output can update long-term graph memory
- extraction succeeds against golden samples

---

## Phase 3 — MAF adapter

### Objective
Integrate the memory system with Microsoft Agent Framework.

### Deliverables
- `Neo4jMemoryContextProvider`
- `Neo4jChatMessageStore`
- `Neo4jMicrosoftMemory`
- `MemoryToolFactory`
- `AgentTraceRecorder`
- sample MAF app

### Tasks
1. map MAF message/session types to internal types
2. implement before-run context provider logic
3. implement after-run persistence logic
4. implement chat message store
5. implement convenience facade
6. implement memory tools
7. implement reasoning trace capture
8. add end-to-end sample with one agent

### Exit criteria
- MAF app runs fully with persistent memory
- context is injected before run
- conversation and reasoning are persisted after run

---

## Phase 4 — GraphRAG interoperability + observability

### Objective
Add the required GraphRAG adapter and baseline operational telemetry.

### Deliverables
- `Neo4j.AgentMemory.GraphRagAdapter`
- blended context support
- `Neo4j.AgentMemory.Observability`
- OpenTelemetry spans/metrics/logs
- sample app combining memory + GraphRAG

### Tasks
1. wrap `Neo4j.AgentFramework.GraphRAG`
2. create internal `IGraphRagContextSource`
3. normalize GraphRAG results to memory context fragments
4. implement blend policy
5. add feature toggles and fallback behavior
6. add metrics for retrieval/extraction/persistence
7. add tracing around before/after run lifecycle

### Exit criteria
- MAF app can run in memory-only, GraphRAG-only, and blended modes
- telemetry is visible and correlated

---

## Phase 5 — Advanced extraction and enrichment

### Objective
Add optional advanced backends and enrichment features.

### Deliverables
- Azure Language extraction adapter
- ONNX extraction adapter (optional)
- geocoding service
- enrichment service

### Tasks
1. define geocoding abstraction
2. implement Nominatim geocoder
3. optionally implement Google geocoder
4. define enrichment abstraction
5. implement Wikimedia enrichment
6. optionally implement Diffbot enrichment
7. add cache and queue behavior
8. add safeguards and rate limits

### Exit criteria
- location enrichment and external enrichment can be enabled independently
- failures do not break core memory workflows

---

## Phase 6 — MCP server

### Objective
Expose memory functionality to external MCP clients.

### Deliverables
- .NET MCP server
- core tool profile
- extended tool profile
- transport hosts
- sample external client config

### Tasks
1. add MCP package
2. implement server bootstrap
3. implement stdio transport
4. implement HTTP transport if desired
5. expose core tools
6. expose extended tools
7. add MCP server sample and docs
8. add contract tests for tool payloads

### Core tools
- `memory_search`
- `memory_get_context`
- `memory_store_message`
- `memory_add_entity`
- `memory_add_preference`
- `memory_add_fact`

### Extended tools
- `memory_get_conversation`
- `memory_list_sessions`
- `memory_get_entity`
- `memory_create_relationship`
- `memory_start_trace`
- `memory_record_step`
- `memory_complete_trace`
- `graph_query`

### Exit criteria
- Claude Desktop / any MCP client can invoke the server successfully
- contract tests validate tool schemas and outputs

---

## 16. Test Strategy and Test Harness

## 16.1 Test layers

### Unit tests
Purpose:
- validate pure business logic
- validate merging, ranking, recall, validation, mapping, and formatting

Use for:
- assemblers
- planners
- validators
- mapping utilities
- policy engines

### Integration tests
Purpose:
- validate actual Neo4j interactions
- validate transactions, indexes, searches, persistence, extraction writes

Use for:
- repositories
- schema bootstrap
- vector/fulltext search
- MAF adapter persistence lifecycle

### End-to-end tests
Purpose:
- validate a real agent flow
- validate before/after run behavior
- validate memory + GraphRAG blend

Use for:
- minimal agent sample
- reasoning trace sample
- memory+GraphRAG sample

### Contract tests
Purpose:
- validate public API stability
- validate MCP tool request/response schemas
- validate facade behavior

### Performance tests
Purpose:
- validate latency and scale assumptions
- benchmark memory recall, write throughput, extraction throughput

## 16.2 Harness components

### Required tooling
- xUnit
- FluentAssertions
- Testcontainers for .NET
- Docker / Neo4j container
- snapshot/golden files for extraction tests
- benchmark harness (BenchmarkDotNet or dedicated perf tests)

## 16.3 Neo4j integration harness

Use **Testcontainers** to spin up a real Neo4j instance per test suite or test collection.

### Required test setup capabilities
- boot Neo4j with APOC if needed
- apply schema bootstrap automatically
- seed test data
- tear down cleanly

## 16.4 Golden datasets

Create stable golden datasets for:
- preference extraction
- fact extraction
- entity extraction
- relationship extraction
- memory recall assembly
- GraphRAG blended assembly
- reasoning trace similarity

Each golden dataset should include:
- input messages
- expected extracted structures
- expected normalized output
- expected persisted graph effects

## 16.5 End-to-end sample scenarios

### Scenario 1 — Personal assistant
- user states preferences across multiple sessions
- agent recalls preferences automatically later

### Scenario 2 — Retail assistant
- user asks for products
- memory stores brand and size preferences
- later queries use those preferences

### Scenario 3 — Investigator agent
- GraphRAG retrieves curated domain graph context
- memory layer stores conversation-specific findings
- blended context is used in subsequent runs

### Scenario 4 — Tool-using agent
- tools are called during planning
- reasoning traces and tool usage are stored
- similar tasks can be recalled later

## 16.6 Quality gates

- all unit tests pass
- integration tests pass against real Neo4j
- end-to-end scenarios pass
- code coverage threshold met
- no public API breaks without approval
- performance baseline within threshold

---

## 17. CI/CD Plan

## 17.1 CI jobs

### Pull request pipeline
- restore
- build
- unit tests
- integration tests with Neo4j test container
- static analysis
- formatting / linting
- API compatibility checks

### Main branch pipeline
- all PR jobs
- end-to-end tests
- performance smoke tests
- package publishing dry run

### Release pipeline
- version bump validation
- package pack
- package publish
- release notes generation
- sample verification

## 17.2 Artifacts

- NuGet packages
- test result XML
- coverage report
- benchmark report
- sample build artifacts

---

## 18. Operational Concerns

## 18.1 Configuration model

Create strongly typed options per layer.

### Required option groups
- Neo4j connection options
- embedding provider options
- extraction options
- memory recall options
- GraphRAG options
- MAF adapter options
- MCP server options
- observability options
- enrichment/geocoding options

## 18.2 Failure handling

### Required rules
- memory write failures must be surfaced and logged
- extraction failures must not corrupt message persistence
- GraphRAG failures must degrade gracefully when configured to do so
- enrichment failures must not block memory provider execution
- MCP server failures must return structured tool errors

## 18.3 Transactions

Required transactional boundaries:
- message persistence
- graph update from extraction
- trace/step/tool-call recording
- entity dedup/merge

## 18.4 Idempotency

Must define idempotent behavior for:
- repeated message save
- repeated fact creation
- repeated preference creation
- duplicate trace recording
- retried extraction pipeline calls

---

## 19. Security and Privacy

## 19.1 Secrets
- use secure configuration providers
- never log secrets
- keep provider-specific credentials out of domain model

## 19.2 Data classification
- define what can be stored in memory
- support redaction hooks if needed
- support opt-out for certain message classes

## 19.3 Least privilege
- Neo4j credentials should have only required privileges
- provider credentials should be scoped appropriately

---

## 20. Acceptance Criteria

The implementation is considered successful when all of the following are true:

1. A .NET Agent Framework app can use the provider to persist memory into Neo4j.
2. The provider supports the three memory types:
   - short-term,
   - long-term,
   - reasoning memory.
3. The provider can automatically extract structured memory from conversations using .NET-compatible extraction.
4. The MAF context provider injects relevant memory before agent execution.
5. The provider persists user/assistant messages and reasoning after execution.
6. The system supports GraphRAG retrieval from an existing Neo4j graph as a separate adapter.
7. The system supports blended memory + GraphRAG context assembly.
8. All important flows are covered by unit, integration, and end-to-end tests.
9. The architecture is reusable outside MAF.
10. The MCP server can later expose the same memory system without redesigning the core.

---

## 21. Suggested Initial Backlog (Implementation Order)

## Epic 1 — Foundation
- create solution
- create abstractions package
- create core package
- create Neo4j package
- create test projects
- add CI skeleton

## Epic 2 — Core memory
- implement short-term memory
- implement long-term memory
- implement reasoning memory
- implement context assembler
- implement schema bootstrap

## Epic 3 — Extraction
- extraction abstractions
- LLM structured extractor
- validation / repair
- merge and provenance

## Epic 4 — Agent Framework adapter
- context provider
- chat message store
- facade
- tools
- trace recorder

## Epic 5 — GraphRAG interoperability
- GraphRAG adapter
- blended retrieval
- sample app

## Epic 6 — Operations and observability
- OTel
- configuration hardening
- logs/metrics
- perf tests

## Epic 7 — MCP
- MCP server
- tool profiles
- contract tests
- sample configs

## Epic 8 — Advanced enrichment
- geocoding
- enrichment
- optional additional extractors

---

## 22. What You Must Not Leave to Guesswork During Implementation

The implementing model or engineer must not guess:

- package boundaries,
- lifecycle responsibilities,
- whether GraphRAG is inside or outside the memory core,
- whether MAF belongs inside the core,
- whether MCP is part of phase 1,
- whether Python NLP is required,
- whether GraphRAG is the memory provider,
- how tests are split,
- whether memory is framework-agnostic,
- which features are required in which phase.

The decisions are:

- **Memory core is framework-agnostic.**
- **MAF is an adapter on top.**
- **GraphRAG is a separate but required adapter in this solution.**
- **MCP is a later external-access layer.**
- **Extraction is .NET-native and provider-based, starting with LLM structured extraction.**
- **Neo4j persistence is implemented directly with the official .NET driver.**

---

## 23. Final Build Order Summary

### Build first
1. framework-agnostic memory core
2. Neo4j persistence
3. LLM extraction pipeline
4. MAF adapter

### Build next
5. GraphRAG adapter using the existing .NET GraphRAG provider
6. observability and operational hardening

### Build after that
7. MCP server
8. advanced enrichment / optional alternate extractors

---

## 24. Final Recommendation

The safest and highest-success implementation is:

1. **Implement `neo4j-agent-memory` as a native .NET Neo4j Memory Provider.**
2. **Implement the MAF layer as an adapter package on top of the core.**
3. **Implement GraphRAG interoperability as a required separate adapter that wraps the existing .NET GraphRAG provider.**
4. **Implement MCP only after the core provider and MAF integration are stable.**

That architecture gives:

- maximum clarity,
- minimum coupling,
- real reusability,
- easy testing,
- a clean path to external MCP access,
- and a much higher probability of successful implementation.

