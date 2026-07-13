# Copilot Repository Instructions

## Agent Memory Evaluation

When asked to run or review memory performance/quality evaluation, act as the Memory Evaluation Agent.

Primary files:

- `strategy/core/performance-quality-evaluation.md` defines the evaluation boundary and metrics (internal doc, local-only).
- `strategy/core/adr/0016-memory-evaluation-boundary.md` explains why memory-layer evaluation comes before chat-answer/model-context grading (internal doc, local-only).
- `tests/AgentMemory.Tests.Integration/Compatibility/TckMirroredBehaviorTests.cs` contains the current TCK-mirrored correctness slice.
- `tools/AgentMemory.Cli/Commands/EvaluationCommand.cs` implements the JSON-producing local Neo4j evaluator.

Rules:

- Evaluate deterministic memory behavior: storage, retrieval, ranking, isolation, temporal history, provenance, and latency.
- Do not grade generated chat answers or whole model context for this evaluation track.
- Prefer the VS Code tasks in `.vscode/tasks.json` over inventing shell recipes.
- Treat owner leak count above zero as a release blocker.
- Keep generated reports under `artifacts/evaluation/`; do not commit generated reports unless explicitly asked.

Useful commands:

```powershell
dotnet run --project tools/AgentMemory.Cli/AgentMemory.Cli.csproj -- evaluate --iterations 3 --output artifacts/evaluation/local.json
```

```powershell
dotnet test tests/AgentMemory.Tests.Integration/AgentMemory.Tests.Integration.csproj --no-restore --filter FullyQualifiedName~TckMirroredBehaviorTests
```

```powershell
dotnet test tests/AgentMemory.Tests.Performance/AgentMemory.Tests.Performance.csproj --no-restore
```

Summary format after a run:

- Report path
- Scenario pass rate
- Owner leak count
- Recall@1 and MRR
- Slowest p95 operation
- Failures or follow-up recommendations
