---
name: memory-evaluation
description: Run Agent Memory deterministic performance and quality evaluation, then summarize the JSON report and release risks.
disable-model-invocation: true
---

## Goal

Run the repository's memory-layer evaluation. Do not evaluate chat-answer quality, prompt quality, or full model context.

## Read First

- `docs/core/performance-quality-evaluation.md`
- `docs/core/adr/0016-memory-evaluation-boundary.md`
- `.vscode/tasks.json`
- `tools/AgentMemory.Cli/Commands/EvaluationCommand.cs`

## Preferred Run Order

1. In VS Code, run task `AgentMemory: evaluation (local Neo4j JSON)`.
2. If local Neo4j is not available, run task `AgentMemory: compatibility smoke (Testcontainers)`.
3. Then run task `AgentMemory: performance smoke (Testcontainers)`.
4. For a benchmark plumbing check, run task `AgentMemory: benchmark smoke (Testcontainers)`.

## Terminal Fallback

```powershell
dotnet run --project tools/AgentMemory.Cli/AgentMemory.Cli.csproj -- evaluate --iterations 3 --output artifacts/evaluation/local.json
```

```powershell
dotnet test tests/AgentMemory.Tests.Integration/AgentMemory.Tests.Integration.csproj --no-restore --filter FullyQualifiedName~TckMirroredBehaviorTests
```

```powershell
dotnet test tests/AgentMemory.Tests.Performance/AgentMemory.Tests.Performance.csproj --no-restore
```

## Summary Format

Return:

- report path;
- scenario pass rate;
- owner leak count;
- Recall@1 and MRR;
- slowest p95 operation;
- failed scenario names and errors;
- recommended next implementation step.

Owner leak count must be zero. Treat any nonzero owner leak count as a release blocker.
