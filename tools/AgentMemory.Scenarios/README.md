# AgentMemory.Scenarios

A scripted history is written into each memory under test, then every memory gets the same questions. The checks are
deterministic, as the repository's evaluation boundary asks:

- **The store holds the fact:** a live fact with the right words and the right date fields. A wrong date shows as "same
  words, other dates".
- **The prompt holds the fact:** each needed fact is in **one** item of the prompt (a line, or one entry of a list joined
  with "; "). Two words that are somewhere in the prompt are not a fact.

Answers are logged for a person to read and are never graded.

Every run keeps its trace beside the report:

| File | What it holds |
|---|---|
| `calls.jsonl` | every model call: what was sent, what came back, the tokens; and every JEV judge call (purpose `judge`), its request and response bodies |
| `spans.jsonl` | the library's spans and their tags |
| `agentmemory.log` | the library's log, at Debug |
| `report.json` | the stores, each prompt, and an explained recall per question, with every candidate fact, its score and its rank. The explanation is a second recall with diagnostics on, made after the scored prompt was taken: the candidates and their ranks are the same, but the judge is asked again and may keep a different set |

## Bruna's go-live

`scenarios/bruna-go-live.json` comes from Pablo Piovano's [three-memories-one-question](https://github.com/ppiova/three-memories-one-question)
(MIT, commit `485fff9`; the licence is beside it), used with his permission. We added the store checks.

- **The history:** 49 statements in 8 sessions over three months, from Bruna, a tech lead. Her go-live is on Friday,
  11 December, on site in Córdoba. In November she mentions her sister's wedding in Bariloche, where she will be from
  9 to 13 December.
- **The three questions,** asked on 4 December in a new conversation:
  1. "When is the Acme go-live, and where does it happen?" The answer looks like the question.
  2. "What do I have on my calendar between December 7 and December 13?" A period of time.
  3. "Plan my prep for go-live week." Two facts that do not look alike collide.

## Arms

- **Baselines:** `file` (a notes file loaded from the top) and `vector` (embeddings, most similar first).
- **AgentMemory configurations:** the `graph-*` arms. Arms with the same `Writes` share one store; the first of them
  writes it and the others only recall.
- **The `-narrow` arms** let the judge see 20 candidates instead of 50. In this scenario's store of a few dozen facts,
  50 is the whole store, so a wide judge sees everything; 20 is what a larger store would leave it. It is a weak
  stand-in: in one run the trip ranked inside the top 20 anyway. The busier scenario (`bruna-go-live-busy.json`, 81 more
  ordinary statements) is the real test.

## What it measured (7 November 2026)

Each arm ran once, with GLM-5.3-Flash writing and answering, JEV judging, and bge-m3 embeddings. ✓ means every needed
fact reached the prompt, each in one item. This is one scenario, not a benchmark.

| Arm | Q1 when / where | Q2 the period | Q3 the clash |
|---|:-:|:-:|:-:|
| **Bruna's go-live** (49 statements) | | | |
| file | ✓ | ✗ | ✓ in one run, ✗ in the other: it depends on the room the file gets |
| vector | ✓ | ✓ | ✓ |
| Conversational | ✓ | ✗ | ✗ |
| Recommended, 2.0.0 (no look-ahead, no date reading) | ✓ | ✓ | ✓ |
| **Busier** (130 statements, 119 live facts) | | | |
| vector | ✓ | ✓ | ✗ |
| Recommended without look-ahead | ✓ | ✓ | ✗ |
| Recommended with look-ahead and date reading | ✓ | ✓ | ✓ |

In the small scenario, Recommended's Q3 passes for a reason worth knowing. The trip ranked 44th of 46 by similarity, but
the judge's wide search takes 50 candidates, so it saw the whole store and kept 33 facts. In the busier store, the trip is
not among the 50 candidates. Look-ahead brings it by date instead: "go-live week" resolves to 7–13 December through the
go-live's date, and the trip is dated inside that week.

## Running it

Models come from the environment, as `AgentMemory.Inference` resolves them (`docs/configuration/inference-providers.md`).
Recommended arms call the JEV judge and need `TYPESAFE_API_KEY`. Docker starts a Neo4j of the run's own unless `--uri`
is given.

```powershell
dotnet run --project tools/AgentMemory.Scenarios -- --dry-run      # the plan and the call estimate, nothing run
dotnet run --project tools/AgentMemory.Scenarios -- --max-calls 400 --output artifacts/evaluation/scenarios/my-run
dotnet run --project tools/AgentMemory.Scenarios -- --arms vector,graph-recommended --no-answers
```

`--max-calls` caps the run's chat calls; judge calls are counted apart (`calls.judge`) and are not capped. A run that
reaches the cap fails rather than finishing half-measured, and a run with any failure, including one outside a step,
exits with 1, its report written with what was measured and marked incomplete. Reports go under `artifacts/evaluation/`, which is not
committed.
