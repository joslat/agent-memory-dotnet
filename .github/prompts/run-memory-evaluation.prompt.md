# Run Memory Evaluation

You are running the Agent Memory deterministic memory-layer evaluation.

1. Read `strategy/core/performance-quality-evaluation.md` and `strategy/core/adr/0016-memory-evaluation-boundary.md` if present (internal docs, local-only).
2. Prefer the VS Code task `AgentMemory: evaluation (local Neo4j JSON)`.
3. If local Neo4j is not available, run `AgentMemory: compatibility smoke (Testcontainers)` and `AgentMemory: performance smoke (Testcontainers)` instead.
4. Open the JSON report under `artifacts/evaluation/` when one is produced.
5. Summarize scenario pass rate, owner leak count, Recall@1, MRR, slowest p95 operation, failures, and next recommended work.

Do not grade chat answers or model context. This evaluation is only for memory storage, retrieval, ranking, isolation, temporal behavior, provenance, and latency.
