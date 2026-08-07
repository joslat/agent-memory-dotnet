# AgentMemory LongMemEval

Opt-in operator tooling for a public, sampled LongMemEval-S memory-quality characterization through
[AgentEval](https://agenteval.dev/). The runner uses the official question and type-specific judge
methodology, but a 10-question sample is not presented as the paper's full-dataset score.
It is deliberately a separate, non-packable project; AgentEval is preview software, and none of its
dependencies enter the published AgentMemory libraries or the
main CLI.

## What the adapter measures

AgentEval selects real LongMemEval-S questions, injects each question's multi-session history, asks
the agent, and applies its type-specific binary judge. AgentEval/LongMemEval do not select an
AgentMemory storage or retrieval mode: **our tool-local benchmark bridge** makes that choice. The
bridge exposes explicit `raw`, `structured`, and `hybrid` modes. None gives the injected history
directly to the answer model. The raw-message vector control performs this bounded sequence:

1. buffer AgentEval's injected `(user, assistant)` turns;
2. batch-persist them as AgentMemory messages in a question-specific owner/session scope;
3. semantically recall only through AgentMemory;
4. give the answer model the recalled messages plus the question;
5. refuse the question if storage or recall produced zero items.

**Mode boundary:** `raw` persists messages with `AddMessagesAsync` and bypasses the extraction
prompts. `structured` additionally runs entity, fact, preference, and relationship extraction once
per real source session and answers from graph-derived memory without raw-message recall. `hybrid`
runs the same extraction and combines graph-derived memory with raw-message recall.

The raw arm was chosen first as a bounded control, not as the predicted highest-quality configuration.
The fixed 10-question seed-42 sample contains 474 source sessions and 4,958 source turns. Extracting
all four categories once per source session with today's fan-out would add 1,896 LLM completions before
retries; flattening each roughly 500-turn question into one extraction request would instead risk
context overflow and erase the session/time boundaries under test. `--prepared-pair` therefore
prepares the structured graph once, freezes it, and evaluates isolated Structured and Hybrid clones.

Prepared-pair extraction runs up to four planned batches concurrently within one question and caps
all extraction provider calls from the process at 12. Both controls are explicit through
`--max-concurrent-batches-per-extraction` and `--max-concurrent-extraction-batches`, are recorded in
the preparation fingerprint, and fail closed when provider calls retry, fail, exceed the cap, or do
not all complete. Per-call telemetry is content-free and bounded to call ordinal, estimated input
size, provider duration, retry state, exception type, and numeric provider status.
Each provider batch uses deterministic short source-session aliases (`s1` through `s4`) and a
batch-specific JSON schema that constrains acknowledgements and learned-item source keys to those
aliases. AgentMemory maps aliases back to the immutable source-session ids before persistence. The
sealed preparation fingerprint records this contract as `batch-source-alias-schema-v1`.

The report contains AgentEval's overall, task-averaged, per-type and per-question results alongside
per-question AgentMemory stored/retrieved counts and opt-in ranked evidence. The evaluator aligns each
retrieved message with its source session/turn/timestamp after recall and reports gold-session recall,
gold-turn hit, first-gold ranks, reciprocal rank, session diversity, similarity scores, and answer-prompt
size. `has_answer` and `answer_session_ids` remain evaluator-side; they are never persisted, embedded,
queried, or sent to the answer model. This proves that a score was produced through the memory system
instead of by silently leaving the full history in model context.

## Prerequisites

- Docker
- the real `longmemeval_s_cleaned.json` dataset from
  <https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main>
- `AZURE_OPENAI_ENDPOINT`
- `AZURE_OPENAI_API_KEY`
- `AZURE_OPENAI_DEPLOYMENT`
- `AZURE_OPENAI_EMBEDDING_DEPLOYMENT`

No embedded or synthetic dataset fallback exists. The tool exits nonzero when data or credentials
are missing.

## Reproduce

```powershell
dotnet run -c Release --project tools/AgentMemory.LongMemEval -- `
  --dataset C:\path\to\longmemeval_s_cleaned.json `
  --questions 10 `
  --seed 42 `
  --max-relevant 30 `
  --evidence-detail identifiers `
  --oracle failed `
  --judge-retries 2 `
  --output artifacts\evaluation\longmemeval\report.json
```

For content-free extraction accounting diagnostics, `--prepared-pair` can select exactly one frozen
question position and source-session ordinal:

```powershell
dotnet run -c Release --project tools/AgentMemory.LongMemEval -- `
  --prepared-pair `
  --dataset C:\path\to\longmemeval_s_cleaned.json `
  --questions 10 `
  --seed 42 `
  --evidence-detail identifiers `
  --diagnostic-question 3 `
  --diagnostic-source-session 14
```

Diagnostic-only execution forbids `--output` and content evidence. It never seals or clones prepared
state and never runs recall, answer generation, or judging; it can therefore never be accepted as a
LongMemEval score.
Defaults are 10 questions, seed 42 and 30 recalled messages. The profile pins Neo4j 5.26 and uses
the configured real Azure OpenAI embedding deployment for both persisted history and recall queries.
The tool probes the provider's vector dimension before creating the Neo4j index and records the
embedding deployment and dimension in the report fingerprint. The configured chat deployment answers
questions and acts as AgentEval's judge. AgentEval 0.16 explicitly requests judge temperature zero and caps judge output at 30 tokens. For the
configured reasoning deployment, the tool narrowly translates the exact judge option signature
`temperature=0, maxOutputTokens=30` to provider-default temperature and a 512-token ceiling. Other
requests are unchanged, and AgentEval's prompt and binary scoring remain authoritative. The policy is
recorded in the report fingerprint; empty or invalid output still rejects the run.

A valid base run requires exactly two LLM calls per question (one answer and one judge), one AgentMemory
telemetry record per question, nonzero stored messages, nonzero recalled items, and a valid explicit
yes/no judge verdict. Empty, invalid, provider-failed, or internally inconsistent verdicts reject the
base score instead of becoming ordinary incorrect answers.

`--evidence-detail` is `identifiers` by default; `none` keeps only aggregate evidence and `content`
explicitly retains recalled/question/answer text for local forensic work. Default accepted and rejected
reports are content-free; accepted safe-mode reports preserve scores, question identifiers/types/outcomes,
durations, counters, and evidence without serializing AgentEval's native content-bearing result or options.
`--judge-retries` and `--oracle none|failed|all` run after the immutable AgentEval result.
Their calls and outcomes are reported separately and never alter AgentEval's score or its required `2N`
base call count. Oracle mode gives the answer model only labelled source sessions and uses the same
answer deployment and type-specific judge to distinguish retrieval failure from reader/judge limits.

## Reference arms — what a score is measured *against*

A LongMemEval percentage means nothing on its own, because it silently compares one AgentMemory
configuration against another. `--reference-arm` supplies the two ends of the band, on the identical
sample, seed, answer deployment and judge:

```powershell
dotnet run -c Release --project tools/AgentMemory.LongMemEval -- `
  --reference-arm no-memory `      # the question alone: the model's parametric floor
  --dataset <longmemeval_s_cleaned.json> --questions 10 --seed 42 --judge-retries 2

dotnet run -c Release --project tools/AgentMemory.LongMemEval -- `
  --reference-arm full-history `   # every real turn in context: the ceiling retrieval aims at
  --dataset <longmemeval_s_cleaned.json> --questions 10 --seed 42 --judge-retries 2
```

Neither arm starts a container or makes an embedding, extraction, storage or recall call, so neither
needs Docker or an embedding deployment; each costs ~20 provider calls. They cannot be combined with
`--memory-mode`, `--prepared-pair`, `--exclude-synthetic-messages`, or a non-`none` `--oracle` —
those are rejected rather than ignored, because they have no meaning for an arm with no memory.

**Measured band (seed 42, ten questions, 2026-08-07):**

| Arm | Overall | Mean context (est. tokens/question) |
|---|---:|---:|
| no-memory floor | 0.0% | 0 |
| AgentMemory raw | 70.0% | 4,284 |
| full-history ceiling | 80.0% | 120,524 |

Read a score against **80.0%, not 100%**: two of the ten questions fail even with the entire
conversation in context, so they are reasoning or judging limits that no retrieval change can reach.
On this sample AgentMemory reaches 87.5% of the achievable band on 28.1× less context.

Whether the history fits is decided by **the provider rejecting the prompt**, never by a token
estimate — every question in this dataset is 113,750–128,489 estimated tokens against a 128k window,
so an estimate would be deciding inside its own error bar. A question that does not fit is reported
as `skipped-context-window` and excluded from fitted accuracy rather than scored wrong; if every
question skips, the arm reports "not measurable on this deployment" instead of 0%. The arms'
system prompts necessarily differ from the shipped memory prompt (instructing a model to use
"retrieved memory" when there is none would manufacture abstentions) and are recorded verbatim in
each report.

## Reading a score

The first run is a characterization baseline, not a product-quality pass/fail gate. A small sample
has high variance. Compare two implementations only when all fingerprint fields match:

The accepted fixed-evaluator seed-42 diagnostic control (`r8`) scored **70.0% overall** and
**69.44% task-averaged** with 20 base calls, 5,878 messages stored, 300 ranked recalls, and three
valid failed-question oracle arms. It exactly repeated `r7`'s ten outcomes after `r7` was rejected as a
checkpoint for unsafe default content retention. This is not a measured product improvement over the
earlier 60.0% / 52.78% characterization: the raw storage/retrieval mode was unchanged, and the comparison
also spans corrected judge output compatibility plus non-deterministic model execution. Use `r8` as the
diagnostic control for subsequent paired candidates.

- exact dataset SHA-256;
- selected question count and seed;
- answer and judge model deployment;
- retrieval cap;
- embedding implementation and dimensions;
- Neo4j image;
- AgentEval version.

This raw-message control cannot grade optimization rank 4 by itself because our bridge bypasses the
extraction prompts that rank 4 changes. The implemented operating-mode comparison runs the same sampled
questions as explicit `raw`, `structured` (derived graph only), and `hybrid` (raw plus derived graph)
arms. The accepted raw r8 remains the fixed-ten control while guarded Structured/Hybrid
characterization is in progress; do not call the raw score the full AgentMemory LongMemEval score.
Preserve the deterministic extraction-quality guard: sampled model evidence complements the zero-noise
pipeline fixture; it does not replace it.

## Verification

```powershell
dotnet test tests/AgentMemory.Tests.Unit.LongMemEval
dotnet build AgentMemory.slnx -c Release
```

The adapter tests verify persistence-before-recall, no-history rejection, and distinct owner/session
scopes across questions.
