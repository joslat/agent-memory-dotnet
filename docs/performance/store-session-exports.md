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
