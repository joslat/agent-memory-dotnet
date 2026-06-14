# Agent Memory for .NET — Consolidated Specification

Status: Working baseline specification  
Project type: Independent community project  
Scope: Native .NET implementation of graph-native persistent memory for Microsoft Agent Framework, backed by Neo4j, with GraphRAG interoperability.

> Independent community project. Not affiliated with, endorsed by, or supported by Neo4j, Inc.  
> This project is a .NET implementation inspired by `neo4j-labs/agent-memory` and designed to interoperate with Neo4j and the public `neo4j-maf-provider` GraphRAG integration.

## 0. Purpose of this specification

This document is the canonical functional and architectural reference for the project.

It exists so that implementation does not depend on:
- remembered chat context
- ad hoc decisions
- hidden assumptions
- subjective interpretation left undocumented

This document is intended to **precede the implementation plan** and define the target system clearly enough that a normal implementation model or engineer can execute successfully and consistently.

### 0.1 Related documents and reference sources

**Implementation plan:** [Agent-memory-for-dotnet-implementation-plan.md](Agent-memory-for-dotnet-implementation-plan.md)  
The implementation plan contains the phased build order, exact package responsibilities, detailed deliverables, CI/CD plan, and backlog derived from this specification.

**Reference source code:**

Both upstream reference projects are checked out under the `/Neo4j` folder in this workspace:

- **`/Neo4j/agent-memory/`** — the Python `neo4j-labs/agent-memory` project. This is the conceptual reference for the three-memory model, extraction patterns, graph schema conventions, and agent lifecycle integration. We reuse its concepts and architecture — not its code.
- **`/Neo4j/neo4j-maf-provider/`** — the `neo4j-labs/neo4j-maf-provider` project. The .NET implementation lives under **`/Neo4j/neo4j-maf-provider/dotnet/`**. This provides the existing Neo4j GraphRAG Context Provider for .NET that we reuse directly, along with MAF integration patterns and test conventions.

## 1. Product specification

### 1.1 Product identity

Working product name: **Agent Memory for .NET**

### 1.2 Product goal

Provide a **native .NET implementation of graph-native persistent memory for AI agents** that:

- integrates with **Microsoft Agent Framework**
- persists memory in **Neo4j**
- supports long-lived memory across agent runs and sessions
- supports **GraphRAG interoperability** using the existing .NET Neo4j GraphRAG provider
- remains cleanly extensible without Python runtime dependencies

### 1.3 What the product is

This product is a **Neo4j Memory Provider for .NET**, not just a retrieval layer or chat storage helper.

It is intended to provide three memory layers:

- **Short-term memory** for conversations, message history, and session context
- **Long-term memory** for entities, preferences, facts, and relationships derived from interactions
- **Reasoning memory** for traces, steps, tool usage, and prior outcomes

### 1.4 What the product is not

The product is not:

- a generic cache
- a simple chat transcript store
- only a retrieval provider
- only a GraphRAG provider
- a Python-dependent implementation
- a fork or rebranding of the upstream Python project
- an official Neo4j product

### 1.5 Primary users

Primary users include:

- .NET developers building AI agents
- teams using Microsoft Agent Framework
- Neo4j users who want persistent memory for agents
- teams that want memory + GraphRAG in one coherent solution

### 1.6 Core capabilities

The product shall support:

#### Short-term memory
- persist conversation history per session
- retrieve recent messages
- search historical messages semantically
- maintain session isolation
- support future summarization/compression

#### Long-term memory
- store entities
- store preferences
- store facts
- store relationships between entities
- support graph-native and semantic recall

#### Reasoning memory
- store traces
- store steps
- store tool calls and outcomes
- retrieve similar prior traces for future agent runs

#### Agent lifecycle integration
- inject memory before an agent run
- persist messages and derived memory after an agent run
- expose memory operations as agent tools

#### GraphRAG interoperability
- integrate with the existing .NET Neo4j GraphRAG provider
- support memory-only, GraphRAG-only, and blended retrieval modes

### 1.7 First-version exclusions

The first version shall not require:

- direct parity with spaCy
- direct parity with GLiNER
- direct parity with GLiREL
- Python framework integrations
- MCP support in the first delivery increment

### 1.8 Product success criteria

The product is successful when a normal .NET team can:

- install the packages
- connect to Neo4j
- wire the system into Microsoft Agent Framework
- persist memory across sessions
- retrieve relevant short-term and long-term memory
- store and retrieve reasoning traces
- combine memory with GraphRAG retrieval
- validate behavior through the provided tests and harness

## 2. Architecture specification

### 2.1 Architectural style

The system shall follow a **layered ports-and-adapters design**.

This is required to keep:
- the memory core framework-agnostic
- MAF integration modular
- GraphRAG integration modular
- future MCP exposure additive rather than invasive

### 2.2 Architectural layers

#### Core layer
Candidate packages:
- `AgentMemory.Abstractions`
- `AgentMemory.Core`

Responsibilities:
- domain models
- interfaces/contracts
- orchestration
- memory context assembly
- extraction coordination
- framework-agnostic behavior

The Core layer shall **not** depend on:
- MAF types
- GraphRAG provider types
- MCP SDK types
- host application code

#### Neo4j infrastructure layer
Candidate package:
- `AgentMemory.Neo4j`

Responsibilities:
- Neo4j repositories
- Cypher execution
- index usage
- graph persistence
- query and search implementation
- mapping between Neo4j records and core models

#### Microsoft Agent Framework adapter layer
Candidate package:
- `AgentMemory.AgentFramework`

Responsibilities:
- MAF context provider
- MAF-compatible message store
- memory tool factory
- MAF lifecycle integration
- mappings between MAF objects and internal models

#### GraphRAG interoperability adapter layer
Candidate package:
- `AgentMemory.GraphRagAdapter`

Responsibilities:
- wrap/reuse the existing .NET Neo4j GraphRAG provider
- expose GraphRAG through internal retrieval abstractions
- support blended retrieval with memory

#### Future optional adapter layer
Not part of this specification scope:
- MCP

### 2.3 Dependency direction

Allowed direction:
- Core <- Neo4j infrastructure
- Core <- MAF adapter
- Core <- GraphRAG adapter

Disallowed direction:
- Core must not depend on adapters
- GraphRAG adapter must not own persistent memory logic
- MAF adapter must not own business logic that belongs in core services

### 2.4 Core contracts

The system shall define contracts equivalent to:

- `IMemoryService`
- `IShortTermMemoryService`
- `ILongTermMemoryService`
- `IReasoningMemoryService`
- `IMemoryContextAssembler`
- `IExtractionService`
- `IEmbeddingProvider`
- `IGraphRagContextSource`
- `IClock`
- `IIdGenerator`

These may be refined, but their responsibilities must remain clearly separated.

### 2.5 Repository structure

Recommended structure:

- `src/`
  - `AgentMemory.Abstractions/`
  - `AgentMemory.Core/`
  - `AgentMemory.Neo4j/`
  - `AgentMemory.AgentFramework/`
  - `AgentMemory.GraphRagAdapter/`
- `tests/`
  - `AgentMemory.UnitTests/`
  - `AgentMemory.IntegrationTests/`
  - `AgentMemory.EndToEndTests/`
- `docs/`
  - `specs/`
  - `architecture/`
  - `examples/`

### 2.6 Host integration principle

A host app shall compose:
- Neo4j connection/configuration
- extraction implementation
- core services
- repositories
- MAF adapter
- optional GraphRAG adapter usage

The host shall not bypass core services for core behavior.

## 3. Memory model specification

### 3.1 Memory layers

The system shall support three distinct logical memory layers.

#### Short-term memory
Purpose:
- represent recent, session-scoped interactions

Core concepts:
- `Conversation`
- `Message`
- `SessionInfo`
- future `ConversationSummary`

Required `Message` fields:
- `MessageId`
- `SessionId`
- `ConversationId`
- `Role`
- `Content`
- `TimestampUtc`
- `Metadata`
- embedding or embedding reference strategy

Required behaviors:
- add message
- batch add messages
- list conversation
- get recent messages
- search messages semantically
- clear session
- preserve session isolation

> **Implementation note — message linking pattern:** The graph model shall use a `FIRST_MESSAGE` relationship from `Conversation` to its first `Message`, plus `NEXT_MESSAGE` relationships between consecutive messages, forming a linked list. This enables O(1) access to the latest message and efficient ordered traversal without relying solely on timestamp ordering. This pattern is proven in the Python reference implementation.

#### Long-term memory
Purpose:
- represent durable knowledge extracted from interactions

Core concepts:
- `Entity`
- `Fact`
- `Preference`
- `Relationship`

Required `Entity` fields:
- `EntityId`
- `Name`
- `CanonicalName` optional
- `Type`
- `Subtype` optional
- `Description` optional
- `Confidence`
- `Attributes`
- `Metadata`

Required `Fact` fields:
- `FactId`
- `Subject`
- `Predicate`
- `Object`
- `Category` optional — classifies the fact (e.g., "personal", "technical", "temporal"). Enables category-based filtering and indexing.
- `Confidence`
- `ValidFrom` optional
- `ValidUntil` optional
- `Metadata`

Required `Preference` fields:
- `PreferenceId`
- `Category`
- `PreferenceText`
- `Context` optional
- `Confidence`
- `Metadata`

Required `Relationship` fields:
- `RelationshipId`
- `SourceEntityId`
- `TargetEntityId`
- `RelationshipType`
- `Confidence`
- `ValidFrom` optional
- `ValidUntil` optional
- `Attributes`

Required behaviors:
- add entity
- add fact
- add preference
- add relationship
- retrieve entities by name
- retrieve facts by subject
- retrieve preferences by category
- semantic search over long-term memory
- support deduplication hooks
- support provenance hooks

> **Implementation note — entity resolution complexity:** The Python reference implementation uses a 4-strategy entity resolution chain: exact match → fuzzy match (token sort ratio ≥ 0.85) → semantic match (embedding cosine ≥ 0.8) → type-aware filtering. Post-resolution actions include auto-merge at ≥ 0.95 confidence, `SAME_AS` flagging at 0.85–0.95, and new entity creation below 0.85. Our Phase 2 implementation should match this complexity level. The `IEntityResolver` interface supports this design.

#### Reasoning memory
Purpose:
- represent how the agent reasoned and what tools it used

Core concepts:
- `ReasoningTrace`
- `ReasoningStep`
- `ToolCall`

Required `ReasoningTrace` fields:
- `TraceId`
- `SessionId`
- `Task`
- `Outcome` optional
- `Success` optional
- `StartedAtUtc`
- `CompletedAtUtc` optional
- `Metadata`

Required `ReasoningStep` fields:
- `StepId`
- `TraceId`
- `StepNumber`
- `Thought` optional
- `Action` optional
- `Observation` optional
- `Metadata`

Required `ToolCall` fields:
- `ToolCallId`
- `StepId`
- `ToolName`
- `Arguments`
- `Result` optional
- `Status`
- `DurationMs` optional
- `Error` optional

Required behaviors:
- start trace
- add step
- record tool call
- complete trace
- retrieve trace with steps
- list traces by session
- search similar traces

### 3.2 Session model

Session identity shall be explicit and configurable.

Supported strategies may include:
- per conversation
- per day
- persistent per user

The chosen strategy must be deterministic and testable.

### 3.3 Storage principles

- The three memory layers may share Neo4j physically but must remain logically distinct.
- Retrieval may blend the layers, but storage responsibilities must stay separated.
- Writes should be idempotent where practical, or explicitly documented where not.

> **Implementation note — metadata serialization:** Neo4j does not natively support `Map` properties on nodes. All `Metadata` dictionaries (`IReadOnlyDictionary<string, object>`) must be serialized as JSON strings when persisted and deserialized on read. This applies to every domain type that carries `Metadata` (Message, Entity, Fact, Preference, ReasoningTrace, etc.). The Python reference uses the same approach.

### 3.4 Provenance principle

Derived long-term memory should be traceable to source messages when practical.
Reasoning traces should be linkable to the messages that initiated them when practical.

The graph model shall support cross-memory-layer relationships for provenance and traceability:

- `INITIATED_BY` — links a `ReasoningTrace` to the `Message` that triggered the reasoning
- `TRIGGERED_BY` — links a `ToolCall` to the `Message` that caused the tool invocation
- `HAS_TRACE` — links a `Conversation` to its associated `ReasoningTrace` nodes

These cross-layer relationships enable tracing from reasoning outcomes back to their conversational origins and are essential for debugging, auditing, and future explainability features.

### 3.5 Neo4j schema requirements

The Neo4j persistence layer requires specific indexes for query performance, semantic search, and fulltext search. These are created by the schema bootstrapper on startup.

#### 3.5.1 Vector indexes

Six vector indexes are required for semantic search across all memory types. All use cosine similarity with configurable dimensions (default: 1536). Neo4j 5.11+ is required for vector index support.

| Index Name | Node Label | Property | Purpose |
|---|---|---|---|
| `message_embedding_idx` | `Message` | `embedding` | Semantic message search |
| `entity_embedding_idx` | `Entity` | `embedding` | Entity similarity search |
| `preference_embedding_idx` | `Preference` | `embedding` | Preference search |
| `fact_embedding_idx` | `Fact` | `embedding` | Fact search |
| `reasoning_step_embedding_idx` | `ReasoningStep` | `embedding` | Reasoning step similarity |
| `task_embedding_idx` | `ReasoningTrace` | `taskEmbedding` | Similar reasoning task search |

#### 3.5.2 Property indexes

Nine property indexes are required for efficient filtering and lookup:

| Index Name | Node Label | Property | Purpose |
|---|---|---|---|
| `message_session_id` | `Message` | `sessionId` | Session-scoped message queries |
| `message_timestamp` | `Message` | `timestamp` | Temporal ordering |
| `entity_type` | `Entity` | `type` | Entity type filtering |
| `entity_name_prop` | `Entity` | `name` | Entity name lookup |
| `fact_category` | `Fact` | `category` | Fact category filtering |
| `preference_category` | `Preference` | `category` | Preference category filtering |
| `reasoning_trace_session_id` | `ReasoningTrace` | `sessionId` | Session-scoped trace queries |
| `reasoning_step_timestamp` | `ReasoningStep` | `timestamp` | Step temporal ordering |
| `tool_call_status` | `ToolCall` | `status` | Tool call status filtering |

#### 3.5.3 Fulltext indexes

Three fulltext indexes are required for text-based search:

| Index Name | Node Label | Properties | Purpose |
|---|---|---|---|
| `message_content` | `Message` | `content` | Message text search |
| `entity_name` | `Entity` | `name`, `description` | Entity text search |
| `fact_content` | `Fact` | `subject`, `predicate`, `object` | Fact text search |

## 4. Microsoft Agent Framework integration specification

### 4.1 Role of this layer

The MAF layer adapts the memory core to Microsoft Agent Framework.
It must not become the owner of the memory domain logic.

### 4.2 Main adapter components

Required components:

- `Neo4jMemoryContextProvider`
- `Neo4jChatMessageStore`
- `Neo4jMicrosoftMemoryFacade` or equivalent convenience facade
- `MemoryToolFactory`
- `AgentTraceRecorder` helper

### 4.3 Pre-run behavior

Before an agent run, the MAF adapter shall:

1. inspect incoming user message(s)
2. determine session and user identity
3. query memory services for:
   - recent short-term memory
   - relevant long-term memory
   - relevant preferences, facts, and entities
   - relevant reasoning traces
4. optionally query the GraphRAG adapter when enabled
5. assemble the context into a form consumable by the agent

### 4.4 Post-run behavior

After an agent run, the MAF adapter shall:

1. persist new user and assistant messages
2. trigger extraction on newly persisted content
3. persist derived long-term memory
4. optionally persist reasoning and tool trace information

### 4.5 Message store behavior

The Neo4j-backed message store shall:
- expose operations compatible with MAF expectations
- delegate message persistence and retrieval to short-term memory services
- preserve session isolation
- support clear/reset behavior per session

### 4.6 Memory tools

The memory tool factory shall expose tools equivalent to:

- `search_memory`
- `remember_preference`
- `remember_fact`
- `recall_preferences`
- `search_knowledge`
- `find_similar_tasks`

These tools shall call core services rather than adapter-private logic.

### 4.7 Independence rule

The Core layer shall not reference MAF types directly.
The MAF adapter shall be responsible for mapping between:
- MAF messages and internal message models
- MAF session constructs and internal session context
- MAF tool activity and internal reasoning models

## 5. GraphRAG interoperability specification

### 5.1 Role of GraphRAG in this solution

For this solution, GraphRAG is treated as **required** as an interoperability component.

But it is **not** the memory provider itself.

### 5.2 Core principle

Persistent memory and GraphRAG retrieval solve different problems:

- memory builds and evolves knowledge from interactions over time
- GraphRAG retrieves from an existing or curated knowledge graph

Both must be supported.

### 5.3 Reuse mandate

The implementation shall reuse the existing **.NET Neo4j GraphRAG provider** rather than recreate its retrieval stack from scratch unless there is a documented gap.

### 5.4 Required adapter behavior

The GraphRAG adapter shall:

- wrap the existing .NET Neo4j GraphRAG provider
- expose a stable internal retrieval interface
- support vector, fulltext, hybrid, and graph-enriched retrieval modes as available upstream
- normalize results into context objects or strings usable by the memory context assembler

### 5.5 Retrieval modes

The system shall support:

- `MemoryOnly`
- `GraphRagOnly`
- `Blended`

In `Blended` mode, the context assembler shall combine:
- memory-derived context
- GraphRAG-derived context

The blending policy must be explicit and configurable.

### 5.6 Separation rule

GraphRAG shall not be used as a substitute for:
- session message persistence
- preference storage
- fact storage
- reasoning traces

### 5.7 Required tests

The GraphRAG adapter shall have dedicated integration tests validating:
- provider wiring
- retrieval mode selection
- result normalization
- blended context assembly

## 6. Testing and test harness specification

### 6.1 Testing principle

Testing is mandatory architecture, not optional polish.

### 6.2 Test layers

#### Unit tests
Unit tests shall cover:
- domain logic
- normalization
- context assembly rules
- mapping logic
- retrieval mode selection
- extraction validation

#### Integration tests
Integration tests shall cover:
- Neo4j persistence
- short-term memory flows
- long-term memory flows
- reasoning memory flows
- MAF adapter flows
- GraphRAG adapter flows

#### End-to-end tests
End-to-end tests shall cover:
- full agent execution lifecycle
- before-run context injection
- after-run persistence
- multi-session recall
- blended memory + GraphRAG retrieval

### 6.3 Test harness requirements

The test harness shall provide:
- automated Neo4j provisioning, preferably containerized
- deterministic seeded data
- repeatable configuration
- isolation between tests where practical
- explicit cleanup/reset rules

### 6.4 Data strategy

The harness shall define:
- baseline graph fixtures
- memory-only scenarios
- GraphRAG-only scenarios
- blended scenarios
- trace/tool-call scenarios

### 6.5 Acceptance gates

No implementation increment is complete unless:
- unit tests pass
- relevant integration tests pass
- no known failing critical scenario exists
- documentation reflects the delivered behavior

### 6.6 Performance smoke tests

At minimum, smoke tests shall validate:
- multi-message session persistence
- retrieval latency within acceptable development thresholds
- no catastrophic degradation as memory depth increases

## 7. Non-functional requirements specification

### 7.1 Maintainability
The codebase shall be structured so a normal .NET team can understand and extend it without relying on prior chat discussions.

### 7.2 Modularity
MAF and GraphRAG concerns shall remain modular adapters and not be embedded into the memory core.

### 7.3 Reliability
The system shall fail transparently and predictably.
Errors in extraction or retrieval shall not silently corrupt memory.

### 7.4 Observability
The system should support structured logging from the start.
OpenTelemetry support should be added during hardening.

### 7.5 Testability
Every major component must be testable without requiring a full host application.

### 7.6 Portability
The solution shall avoid Python runtime dependencies.

### 7.7 Security
Secrets and provider credentials must be supplied through standard .NET configuration and must not be hard-coded.

### 7.8 Backward-safe evolution
Configuration and package structure should support future extensions without breaking the conceptual model.

### 7.9 Performance
The design shall avoid unnecessary round-trips and duplicate retrieval work.
Batch operations should be supported where relevant.

### 7.10 Documentation
This specification, the implementation plan, examples, and package documentation shall be sufficient for another engineer to implement and validate behavior.

## 8. Decisions, constraints, and glossary

### 8.1 Key decisions

#### Decision 1
Build the .NET Neo4j Memory Provider as the main product.

#### Decision 2
Keep the memory core framework-agnostic.

#### Decision 3
Implement MAF as an adapter on top of the memory core.

#### Decision 4
Treat GraphRAG as required for this solution, but implement it as a separate interoperability adapter.

#### Decision 5
Do not depend on Python runtime components.

#### Decision 6
Use LLM-based structured extraction first.

#### Decision 7
Do not include MCP in the first delivery increment.

### 8.2 Constraints

- independent community project
- not an official Neo4j product
- must avoid source confusion in branding
- must be implementable by a normal .NET team
- must not rely on remembered chat context
- must have strong automated tests

### 8.3 Glossary

#### Memory Provider
The component that persists and recalls agent memory over time.

#### GraphRAG
Graph-based retrieval augmentation from an existing knowledge graph.

#### Short-term memory
Recent, session-scoped interaction memory.

#### Long-term memory
Durable structured knowledge extracted from interactions.

#### Reasoning memory
Stored traces of prior reasoning, steps, and tool usage.

#### MAF
Microsoft Agent Framework.

#### Blended retrieval
Combining persistent memory context with GraphRAG retrieval context.

### 8.4 Interpretation rule

If implementation ambiguity exists, the following precedence applies:

1. explicit statements in this specification
2. documented project decisions
3. conservative interpretation preserving separation of concerns
4. issue an ADR or spec update instead of silently guessing

## 9. Closing statement

This document defines the target system to be built:

A native .NET Neo4j Memory Provider for Microsoft Agent Framework that persists and recalls agent memory across three layers — short-term, long-term, and reasoning memory — backed by Neo4j and interoperable with the existing .NET Neo4j GraphRAG provider through a dedicated adapter.

This specification is intended to precede and govern the [implementation plan](Agent-memory-for-dotnet-implementation-plan.md).
