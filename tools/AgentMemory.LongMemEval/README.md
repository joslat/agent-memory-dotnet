# AgentMemory LongMemEval

Opt-in operator tooling for a public, sampled LongMemEval-S memory-quality characterization through
[AgentEval](https://agenteval.dev/). The runner uses the official question and type-specific judge
methodology, but a 10-question sample is not presented as the paper's full-dataset score.
It is deliberately a separate, non-packable project; AgentEval is preview software, and none of its
dependencies enter the published AgentMemory libraries or the
main CLI.

## What the adapter measures

AgentEval selects real LongMemEval-S questions, injects each question's multi-session history, asks
the agent, and applies its type-specific binary judge. The adapter implements structured history
injection and does not give that history directly to the answer model:

1. buffer AgentEval's injected `(user, assistant)` turns;
2. batch-persist them as AgentMemory messages in a question-specific owner/session scope;
3. semantically recall only through AgentMemory;
4. give the answer model the recalled messages plus the question;
5. refuse the question if storage or recall produced zero items.

**Scope boundary:** this adapter persists raw messages with `AddMessagesAsync`; it does not invoke the
entity, fact, preference, or relationship extraction prompts. Its score characterizes semantic
message recall plus answer quality. It is not, by itself, a sampled extraction-prompt quality test.


The report contains AgentEval's overall, task-averaged, per-type and per-question results alongside
per-question AgentMemory stored/retrieved counts. This proves that a score was produced through the
memory system instead of by silently leaving the full history in model context.

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
  --output artifacts\evaluation\longmemeval\report.json
```

Defaults are 10 questions, seed 42 and 30 recalled messages. The profile pins Neo4j 5.26 and uses
the configured real Azure OpenAI embedding deployment for both persisted history and recall queries.
The tool probes the provider's vector dimension before creating the Neo4j index and records the
embedding deployment and dimension in the report fingerprint. The configured chat deployment answers
questions and acts as AgentEval's judge. AgentEval 0.16 explicitly requests judge temperature zero;
for deployments that only accept their default temperature, the tool translates only that unsupported
zero to provider-default while leaving AgentEval's prompt and binary scoring unchanged.

A valid run requires exactly two LLM calls per question (one answer and one judge), one AgentMemory
telemetry record per question, nonzero stored messages, nonzero recalled items, and no embedded agent
or judge errors. If any guard fails, the command exits nonzero and writes an `accepted=false`
diagnostic report. That report omits questions, answers, model responses, and exception text; it
contains only safe validation categories, public question ids/types, durations, and aggregate
storage/recall counts.

## Reading a score

The first run is a characterization baseline, not a product-quality pass/fail gate. A small sample
has high variance. Compare two implementations only when all fingerprint fields match:

- exact dataset SHA-256;
- selected question count and seed;
- answer and judge model deployment;
- retrieval cap;
- embedding implementation and dimensions;
- Neo4j image;
- AgentEval version.

This raw-message mode cannot grade optimization rank 4 by itself because it bypasses the extraction
prompts that rank 4 changes. Before rank 4, add an explicit sampled extraction mode or a sibling test
and compare it with an identical fingerprint. Preserve the existing deterministic extraction-quality
guard as well: sampled model evidence complements the zero-noise pipeline fixture; it does not replace
it.

## Verification

```powershell
dotnet test tests/AgentMemory.Tests.Unit.LongMemEval
dotnet build AgentMemory.slnx -c Release
```

The adapter tests verify persistence-before-recall, no-history rejection, and distinct owner/session
scopes across questions.
