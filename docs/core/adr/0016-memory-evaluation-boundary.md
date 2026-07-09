# ADR 0016 - Memory Evaluation Boundary

Status: Accepted

Date: 2026-07-09

## Context

The project needs to measure performance and quality, and it also needs to compare this .NET implementation with the upstream Python/Neo4j agent-memory ecosystem. The tempting evaluation path is to run chat transcripts through an LLM and grade the final answers, but that mixes several systems at once: extraction, persistence, retrieval, context assembly, prompt design, model behavior, and evaluator behavior.

That kind of end-to-end evaluation is useful later, but it is too noisy for the first quality/performance baseline.

## Decision

Evaluate the memory system first, independently from generated chat-answer quality and model-context quality.

The primary evaluation layers are:

- static schema parity against the embedded upstream snapshot;
- behavioral compatibility through `neo4j-labs/agent-memory-tck` or mirrored TCK-style integration scenarios;
- deterministic retrieval-quality fixtures with judged expected records;
- owner/store/session isolation checks with zero tolerated leaks;
- temporal lifecycle checks for invalidation, supersession, history, and as-of recall;
- live Neo4j performance measurements for read/write/search/maintenance operation families.

Python-vs-.NET comparisons should use the same Neo4j version, same fixture data, same embeddings, same query set, and normalized result records. The comparison should measure conformance, latency, retrieval ranking, leakage, temporal correctness, and provenance completeness, not model-written answer quality.

## Consequences

Positive consequences:

- Quality failures point to memory behavior instead of being hidden inside prompt/model variability.
- Python-vs-.NET comparison can be fair even when wrappers and prompt surfaces differ.
- Stricter .NET behavior, especially owner isolation and temporal lifecycle support, can be measured as intentional behavior rather than treated as accidental incompatibility.
- Later model-context and answer-quality evaluation can build on a stable memory baseline.

Tradeoffs:

- This does not answer whether a full agent produces the best final answer.
- Retrieval-quality fixtures require maintained judged datasets.
- Performance numbers require controlled live Neo4j environments and should not be overgeneralized from developer machines.

## Alternatives Considered

### Evaluate final chat answers first

Rejected as the first baseline. It is valuable product evidence, but it entangles memory, prompt assembly, model choice, model temperature, and evaluator behavior.

### Measure only raw database latency

Rejected as insufficient. Raw latency matters, but quality also includes retrieval correctness, ranking, isolation, temporal behavior, provenance, and compatibility.

### Require exact Python graph equivalence

Rejected. The .NET implementation intentionally adds stronger isolation, temporal lifecycle fields, store controls, and operational tooling. Exact graph identity is less useful than compatible behavior plus documented divergences.

## Verification Anchors

- `docs/core/performance-quality-evaluation.md`
- `docs/core/compatibility-automation.md`
- `docs/core/implementation-plan-golden-path-compatibility.md`
- `tests/AgentMemory.Tests.Integration/ShakedownEndToEndTests.cs`
- `tools/AgentMemory.Cli/Commands/MemoryCommands.cs` (`SchemaParityCommand`, `HistoryCommand`)
