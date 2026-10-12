# Paired gate and context evaluation

`router-arena paired-gate` compares two recorded candidate pools with the shipped `SystemOneMemoryGate`.
Both requests must contain the same question, conversation, clock and ordered original candidates. The
second pool may insert additional candidates. Original text, type and key cannot change between arms.

This operator tool measures gate selection and context displacement. It does not search a graph, verify
the support of additional memories, generate answers, or establish end-to-end retrieval quality. It is
not the library context assembler: the library applies its own budget before gate selection. Record
search outcomes separately and evaluate actual answers before drawing a retrieval-quality conclusion.

## Prepare and execute

```bash
dotnet run --project tools/AgentMemory.RouterArena -- paired-gate \
  --data /path/to/private-data --file pair.json --config gate.json --out /path/to/new-run
```

Preparation is the default. It validates inputs without constructing clients, resolving credentials,
creating output files or issuing requests. Add `--execute` to dispatch. Every execution requires a new
output directory. Existing evidence is never overwritten or resumed.

`pair.json` has exactly these fields (the example is invented):

```json
{
  "schemaVersion": 1,
  "role": "synthetic-development",
  "maxContextCharacters": 1000,
  "baseline": {
    "turn": "What changed?",
    "conversation": [],
    "now": "2026-01-01T00:00:00Z",
    "candidates": [{"key":"a","memoryType":"semantic","text":"The task was planned."}]
  },
  "overlay": {
    "turn": "What changed?",
    "conversation": [],
    "now": "2026-01-01T00:00:00Z",
    "candidates": [
      {"key":"extra","memoryType":"semantic","text":"Model-derived: the task may have been cancelled."},
      {"key":"a","memoryType":"semantic","text":"The task was planned."}
    ]
  }
}
```

The other accepted role is `development`. Labels, holdout roles, duplicate fields and unknown fields
are rejected. Files are capped at 2 MiB; each request at 512 candidates, 32 conversation turns and
131,072 serialized characters. Bounds are harness limits, not accepted experiment settings.

`gate.json` uses `MemoryGateOptions`, with Judge mode, a finite threshold, a timeout up to one minute
and one to four uniquely named judges with finite positive weights. Example banks, compound planning
and update judging are not part of this comparison and must remain disabled. A local test configuration:

```json
{
  "mode": 1,
  "threshold": 0.23,
  "timeout": "00:00:03",
  "judges": [{"name":"synthetic","model":"fake","weight":1,
    "endpoint":"http://127.0.0.1:8765/v1/systemone","keyVariable":null}]
}
```

This configuration requires a separately supplied synthetic server. Synthetic inputs only permit
credential-free `http://127.0.0.1:<port>/v1/systemone`. Development inputs additionally permit
`https://api.typesafe.ai/v1/systemone` with `TYPESAFE_API_KEY`. Local judge configuration uses the same
loopback route. No inference-chat provider or OpenAI credential is used. The operator supplies the
chosen judge models, weights and availability; the tool does not silently select a different blend.

## Context rule and evidence

Gate scores are thresholded without reordering candidates. Whole admitted items are considered in
the supplied pool order; an item that exceeds the remaining budget is skipped, and consideration
continues. The budget counts the complete serialized JSON array, including keys, types, quoting and
escaping, in .NET UTF-16 character units. It is not a token count. Both arms use the identical rule
and limit. The returned `context` is the exact text to supply to the later answer stage; changing its
format or adding memory text requires new accounting. Additional candidates receive no free space.

Identical logical requests reuse one observation. Different pools receive fresh clients and an equal
per-arm timeout. The System One client's existing retry policy remains in effect; physical attempts
and provider-reported usage are counted beneath it. A missing blend member fails the arm even if the
remaining judges return scores. Missing usage is unknown, not zero. Budget/gate rejections are separate;
`displacedOriginals` is null when either arm failed, rather than an apparent zero-loss result.

The directory contains copied inputs, a manifest with input/configuration and gate/runner assembly
hashes, an append-only flushed event log, and the final result if the run reaches completion. A started
decision without a finished/failed record is unresolved; never assume its billing was zero or replay it
automatically. Cancellation is recorded and does not produce a successful result. The event log counts
physical requests but does not retain raw provider payloads or sign the evidence. Keep the input files
and manifest together. The role label and hashes establish neither source truth nor independent review.
