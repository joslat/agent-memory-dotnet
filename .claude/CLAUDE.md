# Claude Project Instructions

## Memory Evaluation

When asked to run or review memory performance/quality evaluation, use the project skill `/memory-evaluation`.

Evaluation must focus on deterministic memory-layer behavior: persistence, retrieval, ranking, owner/store/session isolation, temporal history, provenance, and latency. Do not grade generated chat answers, prompt quality, or full model context as part of this track.

Preferred VS Code/task entry points are defined in `.vscode/tasks.json`. The CLI evaluator writes JSON reports under `artifacts/evaluation/`.
