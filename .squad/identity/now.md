---
updated_at: 2026-07-25T00:00:00Z
focus_area: NuGet Release Prep and Post-Cleanup Sprint Planning
active_issues: []
---

# What We're Focused On

All 6 implementation phases + gap-closure sprint (Waves A–C) are complete. Documentation cleanup sprint is also complete. The solution ships 11 packages (10 source + 1 meta-package), with extensive test coverage and ~99% functional parity with the Python reference.

Current focus areas (see `docs/nextsteps.md` for full prioritised sequence with explicit benefits, cons, and ordering rationale):

- **NuGet release preparation** — CHANGELOG.md, CONTRIBUTING.md, package versioning, CI publish workflow; no code changes required
- **Streaming extraction** — `IStreamingExtractionPipeline` for long-document use cases; highest-value functional gap vs Python
- **Local embedding adapter** — ONNX/sentence-transformers via MEAI for air-gapped/cost-sensitive deployments
- **Additional framework integrations** — AutoGen.NET first; ecosystem breadth is the most visible gap vs Python

## Scope Clarifications (as of 2026-07-25)

Three explicit scope positions now recorded in `.squad/decisions/inbox/deckard-scope-clarifications.md`:

- **Schema/query parity tracking is closed** — one known gap (`DELETE_SESSION_DATA`, Step 5) is already logged; no further parity tracking needed
- **Framework-count parity with Python is not a goal** — we target enterprise .NET frameworks by demand, not by count
- **Local NLP (GLiNER/spaCy) and full CLI are optional** — not required for v1; deferred until community demand justifies them

## Key Facts (ground-truth as of 2026-07-25)

- **Packages:** 10 source packages + 1 meta-package (`Neo4j.AgentMemory`) = 11 total
- **Forward-looking plan:** `docs/nextsteps.md`
- **MCP tools:** 21 `[McpServerTool]` methods, 6 resources, 3 prompts
- **Test target:** .NET 9 (`net9.0` in Directory.Build.props)
- **Embedding API:** `IEmbeddingGenerator<string, Embedding<float>>` (MEAI) via `IEmbeddingOrchestrator`; `IEmbeddingProvider` no longer exists
- **GraphRAG:** Built into `Neo4j.AgentMemory.Neo4j` — no separate `GraphRagAdapter` package
- **License:** Apache 2.0
- **DateTime storage:** Native Neo4j `datetime()` via `Neo4jDateTimeHelper` (G1 complete)
- **Test counts:** Volatile — run `dotnet test --list-tests` for ground truth; do not hard-code in docs
