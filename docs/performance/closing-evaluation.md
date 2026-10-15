# Closing-policy evaluation tool

`router-arena closing-evaluate` records a paired comparison of the library's current named and
similarity update-judge questions on caller-supplied memory pairs. It does not write to Neo4j or change
runtime defaults. Inputs and output evidence live outside this repository's public documentation.

## Run

```shell
dotnet run --project tools/AgentMemory.RouterArena -- closing-evaluate --data /path/to/data --file pairs.json --config judge.json --out /path/to/new-run --dry-run
```

The dry-run validates the entire dataset/configuration, rejects existing output paths and counts
unique requests before resolving credentials or creating clients. Remove `--dry-run` to send the
configured requests. `--limit n` selects the first `n` source rows, with the full dataset count retained
in evidence. A limit is a partial run, not a complete evaluation.

Example configuration:

```json
{
  "judge": {
    "name": "decision-model",
    "model": "your-model",
    "endpoint": "https://your-server.example/v1/systemone",
    "keyVariable": "DECISION_MODEL_API_KEY"
  },
  "timeout": "00:00:10"
}
```

The endpoint must implement the System One protocol. A key is optional for a local unauthenticated
server; when `keyVariable` is supplied it must be populated for execution. Credentials in endpoint
userinfo/query strings and `OPENAI_API_KEY` are rejected. No host chat provider is needed by this tool.

## Input

```json
{
  "schemaVersion": 1,
  "purpose": "development",
  "nowUtc": "2026-10-12T12:00:00Z",
  "cases": [{
    "id": "example",
    "source": "writer-named",
    "kind": "fact",
    "said": "I moved to Lille last week.",
    "storedMemory": "A | lives in | Lyon",
    "newMemory": "A | lives in | Lille",
    "shouldClose": true,
    "rationale": "The current residence changed."
  }]
}
```

Sources are `writer-named` and `similarity`. Kinds are `fact`, `preference` and `relationship`;
relationships are named-only, matching the current write path. IDs must be unique. Identical pairs
with the same context must agree on their label. Labels and rationales are validated locally and
excluded from judge requests. The command uses the library's actual request renderer and question text.

## Evidence and interpretation

The baseline uses the named question at 0.60 or the similarity question at 0.65. The candidate uses
the named question for both sources, with development threshold views of 0.55, 0.60 and 0.65. Responses
are shared by exact rendered-request hash, so threshold views and repeated source rows add no calls.
These thresholds define this comparison; they are not newly recommended runtime defaults.

`<prefix>.json` contains dataset/configuration/library/runner and criteria hashes, the requested model
identifier and endpoint, case-to-request mappings, and one observation per unique request. A configured
model alias does not establish the provider's immutable model version. Each observation records its
score or failure, elapsed time, physical HTTP attempts/retries, pending attempts and reported token
usage. Missing usage remains unknown. No request text, label, credential or provider error body is saved.

Results are checkpointed after each request. Cancellation checkpoints the attempted call before
propagating. Three consecutive failures or an outstanding timed-out request stop the run. A retained
`.lock` reserves the prefix; existing results, temporary files or reservations are never reused. Exit
code 0 means selected requests finished successfully, 1 means input/setup failed, and 2 means request
failures or an early stop. Caller cancellation propagates to the host process.

When scoring, include failed and unrun cases in the denominator; an unanswered negative is not a
correct keep. Report wrong closures and missed closures separately, including source/kind slices and
distinct proposals. Repeated source rows are correlated. Count shared request costs once across all
threshold views. A fixed-pair result does not measure candidate discovery, persistence success,
complete-store stale memories, downstream answer quality or calibrated confidence.
