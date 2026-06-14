# AgentMemory Benchmarks

A [BenchmarkDotNet](https://benchmarkdotnet.org/) harness that measures the latency/throughput of the
hot-path Neo4j operations against a **real** Neo4j 5.26, started automatically via
[Testcontainers](https://dotnet.testcontainers.org/) (so the only prerequisite is a running Docker engine).

> **Not part of CI or the published packages.** Perf numbers are hardware-sensitive, so this project is
> intentionally excluded from `AgentMemory.slnx`, the CI gate, and the NuGet meta-package. Run it manually
> when you want to measure.

## Benchmarks

| Class | Operation measured |
|-------|--------------------|
| `BatchUpsertBenchmarks` | `Neo4jEntityRepository.UpsertBatchAsync` — batch `UNWIND`/`MERGE` upsert (100 / 1000 entities) |
| `VectorSearchBenchmarks` | `Neo4jEntityRepository.SearchByVectorAsync` — entity vector search (top-K 10 / 50 over 2000 embedded entities) |
| `DecayPruneBenchmarks` | `Neo4jMemoryDecayService.PruneExpiredMemoriesAsync` — server-side decay scan/score over 2000 nodes |
| `HybridRetrievalBenchmarks` | `Neo4jGraphRagContextSource.GetContextAsync` — hybrid (vector + fulltext) retrieval over 2000 `:Knowledge` nodes |

All benchmarks run **in-process** (one shared Testcontainer for the whole run) and use deterministic,
LLM-free embeddings, so a run is reproducible and needs no API keys.

## Running

From the repository root, with Docker running:

```bash
# Run everything (Release is required by BenchmarkDotNet):
dotnet run -c Release --project benchmarks/AgentMemory.Benchmarks -- --filter '*'

# Run one class:
dotnet run -c Release --project benchmarks/AgentMemory.Benchmarks -- --filter '*VectorSearch*'

# Fast smoke check (one invocation each, no statistics) — handy to confirm wiring/Docker:
dotnet run -c Release --project benchmarks/AgentMemory.Benchmarks -- --filter '*' --job Dry
```

Results (and the BenchmarkDotNet artifacts) are written under `BenchmarkDotNet.Artifacts/`.

## Notes

- The embedding dimensionality (`BenchmarkNeo4j.EmbeddingDimensions`, default **768**) and seed corpus
  sizes are constants — tweak them to model your own workload.
- Because Neo4j round-trips are millisecond-scale, the job uses modest warmup/iteration counts; raise them
  in `BenchmarkConfig` if you need tighter confidence intervals.
- First run pulls the `neo4j:5.26` image, which can take a minute.
