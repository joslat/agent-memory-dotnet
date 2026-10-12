# Store-session exports

The RouterArena development tool can record a sequence of messages through the real memory write
path in a fresh Neo4j container. Its existing `store-sessions/1` output contains turn results and
session-end live stores. Properties in that legacy format are strings, including array properties;
it cannot establish original message provenance.

Add `--record-provenance` to an existing `store-sessions` command to opt into `store-sessions/2`.
Without this flag, the existing output format and query path remain unchanged. This is a tool export
option, not a library capability or a change to memory defaults.

Version 2 retains arrays, numbers and booleans in graph properties. Temporal driver values remain
ISO strings and embeddings remain excluded. It adds:

- Each session's logical `capturedAt` instant, after its optional dream pass.
- A run ID, isolated-container scope, owner ID, explicit execution options and a completion marker.
- SHA-256 fingerprints for the pack and both turn-set files, and for every DLL in the tool directory.
  DLL informational versions are included. They are revision metadata, not proof of a clean build;
  comparison requires an independently retained build manifest. Input files are checked for changes
  after loading and before writing each result.
- Actual persisted seed/turn message IDs, session IDs, timestamps, roles, text, assigned owners and
  origins (`seed`, `turn-context`, `turn-input`). Context copied into successive turns is identified
  as context; a distinct message ID does not establish an independent observation.

`complete` means execution reached the end of the selected turns and optional consolidation. It does
not mean success: consumers must also check per-turn errors, recall errors, dream errors and any
consolidation error. An explicit turn limit still produces a limited run. An unsourced seed, such as
`--owner-name`, remains unsourced; the exporter does not invent a message for it.

These session snapshots contain live facts, entities, preferences and connections. They are not a
full history export, and the source-message registry is evidence rather than an episodic retrieval
corpus. `capturedAt` is an observation bound for the exported version. Creation timestamps cannot
establish when a subsequently overwritten version became available; some update timestamps use the
database's wall clock while the recorder uses logical time.

Exports include message text. Retain them with the same access controls as their source inputs.

## Search captures

Add `--search-cases <queries.json>` alongside `--record-provenance` to search the actual isolated
store after selected sessions. Without `--search-cases`, no capture queries run and the export stays
unchanged. The input contains questions, not answer labels:

```json
{
  "schemaVersion": 1,
  "role": "development",
  "wideLimit": 50,
  "cases": [
    { "id": "current-plan", "session": 1, "question": "What is the plan?" },
    { "id": "plan-at-session-one", "session": 1, "question": "What was the plan then?", "historical": true }
  ]
}
```

Only `development` and `synthetic-development` roles are accepted. Plans are bounded to 2 MiB and
128 cases; IDs must be unique, sessions must occur in the selected turns, and unknown or duplicate
fields are rejected. The wide limit is 1–512; existing larger section limits remain in force. The
plan's raw-byte SHA-256 is retained and checked before capture and export.

Current captures use the library's wide retrieval path with owner-only scope and diagnostics, without
calling a relevance judge. Historical captures use the library's as-of assembler, with both valid and
knowledge time set to that session's capture instant and the configured ordinary recall limits.
They do not reconstruct an earlier session from a later store. Questions are queried, not ingested;
embedding requests still use the recorder's configured embedding service.

The optional `searchCapture` block retains each full assembled context, recall options, capture time,
elapsed time, candidate-key-to-item-ID mapping, and current gate request. Historical captures have no
gate request. Full context includes ungated material such as recent messages and the working-memory
block when present. Candidate IDs may refer to messages or reasoning traces as well as live semantic
records; they must not all be treated as IDs in the session's semantic snapshot.

Library context budgeting happens **before** capture. Preserve `truncated`, section diagnostics and
ranked items when analysing missing evidence: section returned counts can precede budget trimming.
A missing candidate alone does not prove a search miss. Timeouts retain partial context for diagnosis,
set an error, and omit the gate request; failed captures cannot become successful empty pools.

`searchCapture.complete` requires all planned captures to finish without errors. It is separate from
writer `provenance.complete`; consumers must check both and the writer's per-turn errors. Capture
errors produce exit code 2. This records original retrieval evidence, not overlay ranking, answer
generation or answer quality. The separate [paired gate tool](paired-gate-evaluation.md) can compare
recorded pools under its explicitly different, post-gate rendered-context budget.
