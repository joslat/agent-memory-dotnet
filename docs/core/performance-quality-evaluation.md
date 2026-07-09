# Performance and Quality Evaluation

Status: current as of 2026-07-09.

This document defines how Agent Memory for .NET should measure memory-system performance and quality without initially evaluating chat-answer quality, prompt assembly, or model context quality.

## Evaluation Boundary

The first evaluation target is the memory system itself:

- persisted graph state;
- service and repository behavior;
- retrieval result sets and ordering;
- owner, store, and session isolation;
- temporal lifecycle behavior;
- provenance and history;
- compatibility with upstream behavioral expectations.

The first evaluation target is not:

- whether an LLM writes a good final answer;
- whether the prompt contains the best possible wording;
- whether a chat transcript is pleasant or helpful;
- whether model context assembly chooses the final right mix of memories.

Those higher-level evaluations matter, but they should be a later track. First prove that memory writes, reads, ranking, isolation, provenance, and lifecycle semantics are correct and measurable under deterministic fixtures.

## Quality Dimensions

| Dimension | Question | Metric |
|---|---|---|
| Contract conformance | Does the implementation satisfy the documented service/schema behavior? | Pass/fail by scenario id, including TCK-mirrored scenarios. |
| Retrieval correctness | Are expected entities, facts, preferences, messages, or traces returned? | Recall@K, Precision@K, exact-hit rate. |
| Ranking quality | Are the best memories ranked first for a fixture query? | MRR, NDCG@K, rank delta versus expected order. |
| Isolation safety | Can one owner/store/session see another owner's private memory? | Owner/store/session leak count; target is always zero. |
| Temporal correctness | Are invalidated, superseded, and as-of records included or excluded correctly? | Temporal scenario pass rate. |
| Provenance quality | Can a memory be traced back to source messages and lifecycle state? | Source-link completeness, history completeness. |
| Cross-memory coherence | Do conversations, long-term memory, reasoning traces, and relationships connect correctly? | Scenario pass/fail for graph linkage. |

## Performance Dimensions

Measure latency and throughput separately for each memory layer and for the combined golden path.

| Area | Operations |
|---|---|
| Short-term memory | Add conversation, add message, batch add messages, recent messages, all session messages, semantic message search. |
| Long-term memory | Add entity/fact/preference, by-name/by-subject/by-category reads, semantic search, relationship traversal, invalidation, supersession. |
| Reasoning memory | Start trace, add step, record tool call, touched-entity links, complete trace, list traces, similar-trace search. |
| History and audit | Long-term history query by kind, owner, id, live-only, and invalidated-inclusive filters. |
| Maintenance | consolidation dry-run, decay, conflict detection, schema bootstrap/migration. |

Report at least p50, p95, p99, min, max, throughput, error count, and dataset size. For .NET runs, also record allocations when using a benchmark harness that can provide them.

## Python vs .NET Comparison

Compare the Python/upstream implementation and this .NET implementation through the same canonical fixtures, not through model-written answers.

Recommended comparison method:

1. Use the same Neo4j version, database configuration, vector index dimensions, fixture dataset, query set, and embedding vectors.
2. Seed deterministic entities, facts, preferences, conversations, messages, relationships, traces, steps, and tool calls into both implementations.
3. Run the same operation mix through the public API or through the `neo4j-labs/agent-memory-tck` bridge contract where possible.
4. Normalize returned records into a shared JSON shape.
5. Compare conformance, latency distributions, result overlap, retrieval metrics, isolation leaks, temporal behavior, and provenance completeness.

The most useful comparison table is:

| Metric | Python/upstream | .NET | Notes |
|---|---:|---:|---|
| Scenario pass rate | TBD | TBD | Split by Bronze/Silver/Gold/Platinum or local equivalent. |
| p95 write latency | TBD | TBD | Per operation family. |
| p95 read latency | TBD | TBD | Per operation family. |
| Recall@5 | TBD | TBD | Fixture-judged retrieval. |
| MRR | TBD | TBD | Fixture-judged ranking. |
| Owner leak count | TBD | 0 required | .NET should keep stricter owner-scope behavior. |
| Temporal pass rate | TBD | TBD | Invalidation, supersession, as-of recall. |
| Provenance completeness | TBD | TBD | Source message and lifecycle links. |

Do not require identical internal graph extensions. The comparison should distinguish compatible behavior from intentional .NET extensions such as `owner_id`, `owner_key`, `invalidated_at`, stronger isolation, and memory history.

## Harness Shape

Use three layers of automated evidence:

| Layer | Tooling | Output |
|---|---|---|
| Correctness | xUnit integration tests and TCK-mirrored scenario ids. | Pass/fail with scenario names. |
| Retrieval quality | Deterministic fixture runner with judged query sets. | JSON/CSV metrics: Recall@K, Precision@K, MRR, NDCG. |
| Performance | BenchmarkDotNet or a dedicated scenario runner over live Neo4j. | JSON reports with latency percentiles, throughput, dataset size, environment metadata. |

Store machine-readable reports under `artifacts/evaluation/` in CI or local runs. The repository should not commit generated benchmark output unless it is a dated release artifact.

## Initial Scenario Set

Start small and deterministic:

| ID | Scenario | Why it matters |
|---|---|---|
| MQ-001 | Short-term conversation/message persistence and session reads. | Validates transient conversation memory without involving model context. |
| MQ-002 | Long-term entity/fact/preference round-trip plus owner/shared visibility. | Validates durable structured memory and treats any owner leak as a release blocker. |
| MQ-003 | Relationship traversal and touched-entity provenance. | Validates graph-native memory and links reasoning activity back to entities. |
| MQ-004 | Reasoning trace, steps, tool calls, and completion. | Validates execution memory and provenance. |
| MQ-005 | Temporal history, invalidation, and supersession. | Validates non-destructive lifecycle semantics. |
| MQ-006 | Vector retrieval against fixed embeddings and judged expectations. | First retrieval-quality score without model context. |
| MQ-007 | Python vs .NET fixture parity. | Separates behavioral compatibility from implementation details. |

## VS Code and Agent Entry Points

This repository includes a shared execution path for humans, GitHub Copilot, and Claude Code in Visual Studio Code.

| Surface | File | Purpose |
|---|---|---|
| CLI evaluator | `tools/AgentMemory.Cli/Commands/EvaluationCommand.cs` | Runs deterministic memory-layer quality/performance scenarios against a live Neo4j database and writes JSON under `artifacts/evaluation/`. |
| VS Code tasks | `.vscode/tasks.json` | Provides local-Neo4j evaluation plus Testcontainers compatibility/performance smoke tasks. |
| Copilot repository instructions | `.github/copilot-instructions.md` | Teaches Copilot to use the evaluation boundary and tasks. |
| Copilot custom agent | `.github/agents/memory-evaluation.agent.md` | Dedicated Copilot agent persona for running and summarizing evaluation. |
| Copilot prompt file | `.github/prompts/run-memory-evaluation.prompt.md` | Reusable prompt for Copilot Chat in VS Code. |
| Claude project instructions | `.claude/CLAUDE.md` | Points Claude Code to the project memory-evaluation skill. |
| Claude skill | `.claude/skills/memory-evaluation/SKILL.md` | Adds `/memory-evaluation` for Claude Code. |

The local Neo4j evaluator command is:

```bash
dotnet run --project tools/AgentMemory.Cli/AgentMemory.Cli.csproj -- evaluate --iterations 3 --output artifacts/evaluation/local.json
```

The generated report is intentionally ignored by git through the existing `artifacts/` ignore rule.

## Gates

Suggested gates before treating evaluation as healthy:

- zero owner/store/session isolation leaks;
- all schema parity tests pass for the embedded upstream snapshot;
- all local TCK-mirrored scenarios pass;
- deterministic retrieval fixtures meet the agreed Recall@K and MRR thresholds;
- p95 latency budgets are defined and tracked for each operation family;
- every accepted divergence is documented in compatibility docs or ADRs.

## Later Track: Context Assembly

After the deterministic memory layer is stable, add a separate context-assembly evaluation. That later track should measure which memories are selected into model context, token budget behavior, provenance visibility, and context relevance. It still should not start with grading generated chat answers; answer quality is a final end-to-end product evaluation, not the first memory-system health signal.
