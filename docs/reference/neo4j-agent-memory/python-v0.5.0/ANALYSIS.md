# neo4j-agent-memory — repo analysis @ v0.5.0

> **Snapshot:** `neo4j-labs/agent-memory` `main` @ `f29ae8dd8` (2026-06-03) · Python pkg **v0.5.0** (`python-v0.5.0` tag; TS SDK `typescript-v0.4.0`) · captured 2026-06-07.
> **Activity refresh:** checked 2026-07-09; upstream `main` had docs/NAMS activity through `6d02e986` on 2026-07-08, but this folder remains the embedded v0.5.0 schema snapshot until a tagged release or material Bolt/schema change requires a new capture.
> **Companion files:** [`schema.md`](./schema.md) · [`schema.cypher`](./schema.cypher) · [`schema.json`](./schema.json).
> **Baseline for the diff:** the early `~v0.1.0` shape that `agent-memory-dotnet` mirrors (bolt-only, POLE+O, short/long/reasoning memory, plain vector recall, `Fact.valid_from/valid_until`, no NAMS/ontology/TS-SDK/pluggable-providers).

---

## 1. TL;DR — what changed since last time

Upstream made **one architecturally dominant move** and a cluster of additive features:

1. **NAMS (Neo4j Agent Memory Service) — a hosted backend.** The library is now **dual-backend**: `bolt` (direct Neo4j, what the .NET port targets) *or* `nams` (a managed HTTP service). `connect()` auto-selects NAMS when `MEMORY_API_KEY` is set; on NAMS, `client.graph` raises `NotSupportedError` and you go through `client.query.cypher()`. (v0.4 → v0.5 added **workspace addressing**.)
2. **Polyglot monorepo + TypeScript SDK** (`@neo4j-labs/agent-memory`), with independent `python-v*` / `typescript-v*` release tags and cross-language conformance enforced by a **Technology Compatibility Kit** (`agent-memory-tck`).
3. **Ontology control surface** (`client.ontology`): 28 system templates, validation modes, and (Unreleased) **import / diff / migrate** — generalizing the fixed POLE+O model into a governable, versionable ontology.
4. **Pluggable LLM/embedding providers** (v0.3): `LLMProvider`/`EmbeddingProvider` Protocols, native adapters, a universal `LiteLLMProvider`, and a `from_provider("openai/…")` factory.
5. **Production primitives** (v0.2): multi-tenancy + `:User` layer, **buffered writes**, **consolidation primitives**, an **eval harness**, the `[:TOUCHED]` audit edge, `core.encryption`, and `adopt_existing_graph`.
6. **Temporal/graph touches:** a working **`supersede_preference()`** + Preference `as_of=` (valid-time time-travel), and (Unreleased) **`expand_graph(node_id)`** 1-hop neighborhood.

For a **bolt-only .NET port**, these sort into four buckets: **(a) hosted-backend / out-of-scope-by-design** (NAMS, dual routing, workspaces, auth, server-side extraction status); **(b) schema additions**; **(c) bolt-implementable API additions**; **(d) build/packaging** (the TS SDK).

---

## 2. Feature diff by theme (with .NET-port parity bucket)

| # | Theme | Since | Bucket | Status in `agent-memory-dotnet` |
|---|---|---|---|---|
| 1 | **NAMS hosted backend** + dual bolt/NAMS routing + workspace addressing | 0.4–0.5 | (a) hosted, out-of-scope | **Absent (by design).** Port keeps bolt as the only transport. Optional future `IMemoryBackend` abstraction if a NAMS transport is ever wanted. `workspace_id` is a NAMS addressing concern, **not** a graph-schema change. |
| 2 | **Ontology surface** (28 templates, validation, import/diff/migrate) | 0.5 | (c) API + governance | **Absent.** Port has the baseline analogue (`:Schema` persistence + POLE+O constants). Sizeable, deliberate future feature — not a quiet gap. |
| 3 | **TypeScript SDK / polyglot monorepo** | 0.5 | (d) packaging | **Out-of-scope.** The port is itself a language binding. *Track `agent-memory-tck`* for conformance (see §4). |
| 4 | **Pluggable LLM/embedding providers** (`from_provider`, LiteLLM) | 0.3 | (c) API | **Partially covered.** Port abstracts extraction/embedding via DI + `Microsoft.Extensions.AI` (`IChatClient`/`IEmbeddingGenerator`) — the .NET-idiomatic "LiteLLM". Worth documenting the mapping + mirroring **vector-dimension validation**. |
| 5 | **Multi-tenancy + `:User` layer** | 0.2/0.4 | (b) schema + read-path | **Intentionally divergent — KEPT.** Upstream models tenancy *relationally* (`:User` + `HAS_*` edges + denormalized `Conversation.user_identifier`; Entity/Fact global; **write-only** enforcement, anonymous reads — issue #135). The port uses a **scalar `owner_id`** on Fact/Entity/Preference/ReasoningTrace (+`owner_key`) **enforced on the read/recall path** + an optional per-app store tier. This *closes* upstream's anonymous-recall gap → value-add, not regression. |
| 6 | **Buffered writes** (`client.buffered`, `flush()`) | 0.2 | (c) API | **Absent.** Easy bolt add via `Channel<T>` + background `IHostedService`; arguably more natural in .NET. Low schema impact. |
| 7 | **Consolidation primitives** + `ConsolidationRun` node + `MemoryReadAudit` | 0.2/0.5 | (b)+(c) | **Already ported / enhanced.** `Neo4jConsolidationService` and `ConsolidationRun` are present. As of 2026-07-09, `MemoryReadAudit` schema is also present and access tracking writes audit rows with .NET-local `memory_id`, `read_at`, `owner_id`, and `access_count` detail beyond upstream v0.5.0 `id`/`kind`. |
| 8 | **Preference `as_of` + `supersede_preference`** (+ Unreleased `expand_graph` 1-hop) | 0.5 / Unrel. | (c) API | **Mixed.** Port has **broader** `as_of` recall (entities/facts/preferences/traces — wider than upstream's preferences-only) and `valid_from/valid_until` on Fact **and** Relationship. **But** lacks `supersede_preference` (invalidate-not-delete writer) and `expand_graph`. Per the port's own `bitemporal-memory-assessment.md`, `invalidated_at` is read-but-never-written (orphan) and decay `DETACH DELETE`s. → Add a supersede/invalidate writer + a 1-hop expand. |
| 9 | **Audit-trail `[:TOUCHED]`** + `TraceOutcome.error_kind` | 0.2 | (b)+(c) | **Already ported** (by-id variant; `RecordTouchedEntitiesByIds`/`GetTouchedEntityIds`). At parity. |
| 10 | **Encryption** (`core.encryption`) | 0.2 | (a)/different layer | **Partial.** Port has bolt-level **TLS only** (transport). If upstream `core.encryption` is field/at-rest, that's a genuine gap — scope it before mirroring. |
| 11 | **Eval harness** (`client.eval.run`) | 0.2 | (c) tooling | **Absent** as a first-class memory-quality suite (port has unit/integration tests). Optional quality tooling. |
| 12 | **`adopt_existing_graph`** | 0.2 | (c) API, bolt-native | **Absent.** Bolt-*exclusive* upstream (NAMS raises `NotSupportedError`) → squarely in-scope for a bolt-only port. Good future feature. |

---

## 3. Schema diff vs baseline (see `schema.md` for the full catalog)

**New node labels:** `User` (v0.4 multi-tenant), `ConsolidationRun` + `MemoryReadAudit` (v0.5 hygiene/privacy). **New properties:** `Conversation.archived`/`archived_at`, `ReasoningTrace.error_kind`, `Entity.location` (Point), `Entity.merged_into`/`merged_at`/`aliases`, pre-aggregated `Tool` counters + `last_used_at`. **New edges:** `TOUCHED` (recorded_at), `SAME_AS` (non-destructive dedup), `SUPERSEDED_BY` (v0.5, invalidate-not-delete; written in `long_term.py`), plus the provenance edges `EXTRACTED_FROM` / `EXTRACTED_BY`. **New indexes:** `trace_error_kind_idx`, `conversation_archived_idx`, `consolidation_run_kind_idx`, `memory_read_audit_kind_idx`; a **geospatial point index** `entity_location_idx`. Entity nodes now carry **dynamic labels** `(:Entity:TYPE:SUBTYPE)`.

**Counts:** 14 distinct labels · 16 relationship types (+`SUPERSEDED_BY` from `long_term.py`) · **12** uniqueness constraints · 14 managed range indexes (+2 `:Schema`) · 6 vector indexes (1536-dim cosine) · 1 point index · **no fulltext**.

**Port update (2026-07-09):** `agent-memory-dotnet` now implements the `MemoryReadAudit` label, uniqueness constraint, and `kind` index, and adds richer local fields (`memory_id`, `read_at`, `owner_id`, `access_count`) to support history/audit reporting.

**Temporal posture (key for our bitemporal work):** valid-time (`valid_from`/`valid_until`) on **Fact** and **RELATED_TO** (and now Preference, written by `add_preference`/`supersede_preference`). **No transaction-time axis anywhere** — `invalidated_at`/`expired_at` do not exist. `as_of` recall is valid-time only, Preferences-only. Forgetting is **non-destructive** (dedup→`:SAME_AS`, expiry→`archived=true`, supersession→`valid_until`+`:SUPERSEDED_BY`); hard `DETACH DELETE` only in `clear_session`. → This is the opposite of the port's destructive decay (see `decay-improvement-proposal.md`).

**Multi-tenant negative finding:** in `graph/queries.py` the `:User` node is **never linked** to any other node and **no read/write is owner-scoped** — confirming upstream's write-only/edge-based tenancy and validating the port's divergent scalar-`owner_id`, read-enforced model.

---

## 4. The "schema analyzer for compatibility" repo (your question)

There is **no single Neo4j project literally branded "schema analyzer for compatibility."** Best matches, in order of likely relevance to you:

1. **`neo4j-labs/agent-memory-tck`** — **Technology Compatibility Kit** for neo4j-agent-memory implementations: a *"formal behavioral specification, 189 executable test scenarios, and a compliance framework that enables any implementation — in any language — to verify conformance with the neo4j-agent-memory data model."* **This is almost certainly the "compatibility" tool you're thinking of, and the most valuable to this project** — the .NET port could run it to prove conformance. (Behavioral/data-model conformance, not a schema-diff.)
2. **`neo4j/graph-schema-introspector`** (official, https://github.com/neo4j/graph-schema-introspector) — a Neo4j 5 procedure `experimental.introspect.asJson({})` that **analyzes an existing graph and emits its schema as JSON**. The "analyzer" piece. *PoC, stale (last push 2024-01).*
3. **`neo4j/graph-schema-json-js-utils`** (official, npm `@neo4j/graph-schema-utils`, https://github.com/neo4j/graph-schema-json-js-utils) — the **Graph Schema JSON spec** + `validateSchema()` to validate a schema against the spec. Actively maintained (2026-05). *Validates one schema against the spec — not a two-schema diff.*

   → #2 + #3 **composed** (introspect → validate against an expected JSON schema) give an "analyze + check compatibility" workflow, but **neither alone is a turnkey schema-diff/compatibility tool.**
4. **`clpm88/neo4j-graph-diff`** — the only repo that *literally* diffs two Neo4j graphs at schema + entity level — but it's an **unaffiliated community** Go tool (0 stars), not a Neo4j-official/labs project.

**Not matches:** `apoc.meta.schema` (live introspection only), `michael-simons/neo4j-migrations` (Flyway-style migration runner), `neo4j-labs/neosemantics` (RDF/SHACL validation), `cypher-workbench` (modeling). agent-memory's own `client.schema.adopt_existing_graph()` adopts a graph but exposes no public compatibility-diff method.

> **Recommendation:** if your goal is *"prove the .NET port conforms,"* use **`agent-memory-tck`**. If your goal is *"introspect/compare a graph's schema as JSON,"* use **`graph-schema-introspector` + `@neo4j/graph-schema-utils`** (and note our [`schema.json`](./schema.json) is already a machine-readable schema you can diff against future snapshots).

---

## 5. Recommended next steps for the .NET port

- **Track `agent-memory-tck`** — wire it into CI if it's language-neutral; it's the canonical compatibility check.
- **Add `supersede_preference` equivalent + invalidate-not-delete** (ties into `bitemporal-memory-assessment.md` and `decay-improvement-proposal.md`) — and `expand_graph` 1-hop (ties into the structural-decay proposal).
- **Cheap, high-value bolt-native adds:** `adopt_existing_graph`, buffered writes, vector-dimension validation.
- **Affirm the divergences** (scalar `owner_id` read-enforced tenancy; broader `as_of`) in the parity docs — they're value-adds, not regressions.
- **Scope `core.encryption`** before deciding whether to mirror field/at-rest encryption (port only has transport TLS today).
- Re-snapshot upstream into a new `python-v0.6.x/` folder when NAMS/ontology stabilize.

---

## 6. Sources

Live repo @ `f29ae8dd8`: `pyproject.toml` (v0.5.0), `CHANGELOG.md` (v0.2–Unreleased), `graph/schema.py`, `graph/queries.py`, `schema/models.py`, `schema/persistence.py`, `README.md`, `neo4j.com/labs/agent-memory/explanation/backends`. Repos: `neo4j-labs/agent-memory-tck`, `neo4j/graph-schema-introspector`, `neo4j/graph-schema-json-js-utils`, `clpm88/neo4j-graph-diff`. Port parity context: `docs/schema-parity-assessment.md`, `docs/bitemporal-memory-assessment.md`, `docs/decay-improvement-proposal.md`.
