# Operating the speech server with AgentMemory: runbook

For the operator of the recipe (`recipe.sh`). Each entry says how the problem shows, what it means, and what to do.
Two rules hold everywhere: **never delete or edit the journal** (`JOURNAL_VOLUME`) to make a problem go away, and
**never repeat an uncertain write by hand**. The journal is the evidence of what was asked; the library decides from
it and from the store what happened.

Where to look: `recipe.sh <settings> logs speech` (lines that start `memory:` hold states, ids and times, never what
anyone said) and `GET /v1/realtime/capabilities` (the `memory` section).

| Status field | Meaning |
|---|---|
| `state: ready` | Recall works |
| `state: recovering` | A recall failed or was late; the server warms the store up every 2 s until it answers |
| `writes.saving` | Remembers, forgets or erases accepted and not finished |
| `writes.needsAttention: true` | A write ended `partial` or `unknown`; recall is paused until it is resolved |
| `writes.observing` | Learning unasked is on (profile `learning`, `MEMORY_OBSERVE=1`) |

## The server does not start

- **"S2S_MEMORY_CONFIG is set but AgentMemory cannot start"** names the reason. A configuration error names the field
  (never its value). Fix the settings file and `up` again.
- **The library does not load** (`NativeLibraryError`: another architecture, a damaged file, a library whose bytes
  differ from the wheel's manifest). The image was built for another platform or was changed after it was built:
  rebuild it with `build-images.sh` for linux-x64. Nothing else is tried in its place.
- **`unsupported_abi`**: the wheel and the library come from different builds. Rebuild the image from one commit.
- **`state_locked` (BUSY)**: another process holds the journal's state directory. Only one server may own a journal;
  stop the other one. After a crash the lock is released by the operating system.
- **`journal_corrupt`**: a line before the journal's last was changed by something other than the library. Do not
  edit it further. Stop the server, keep a copy of the volume, and restore the journal from the last backup taken with
  the server stopped (see Backups). An unfinished last line is not corruption: the library cuts it off by itself.
- **`journal_owner_mismatch`**: `MEMORY_OWNER` changed while the journal belongs to another owner. Each owner needs
  its own `JOURNAL_VOLUME`; changing the owner of an existing deployment is a migration, not a setting.

## The database

- **Neo4j down or restarting**: turns are answered without memory, at the usual speed; `state: recovering` until it
  answers again, then `store answers again` in the log. With memory writes on, remembers wait in the journal and run
  when it is back (profile `learning`: retried with a growing pause; `explicit_write`: a remember whose store call
  failed ends `unknown`, see below).
- **Disk full on the journal volume**: a remember is refused with `journal_unavailable` and nothing is accepted (the
  robot is told it was not saved). Free space; nothing needs repairing.

## Writes that need attention (`needsAttention: true`)

A write ended `partial` (some of it was written, not all verified) or `unknown` (the store failed while writing, the
process ended during it, or the evidence could not be read). Recall stays paused so the robot never answers from a
half-known state. Find it with the library (`memory_operation_get` by id or source id) or the log line
`memory: remember for <item> ended unknown (<reason>)`. Then choose:

- **Keep what it wrote:** `memory_operation_resolve` with `"resolution": "keep"`. Recall resumes.
- **Stop using what it wrote:** `memory_forget` with its `operationId`. Recall resumes.
- **Erase the owner:** `recipe.sh <settings> erase` (below), which also ends every uncertain write.

The `learning` profile avoids most of these: every remember is applied once with a receipt in the store, so a crash
or a failed store call is simply retried. `unknown` there means the store failed ten times in a row.

## Deleting

- **Forget** (by voice, "forget what I just told you", or `memory_forget`) closes memories; they stay as history and
  are not deleted. Say "I'll stop using that", never "deleted".
- **Erase** deletes everything of `MEMORY_OWNER`: memories, conversations, messages, and the remembered words in the
  journal. `recipe.sh <settings> erase` asks you to type `erase`. It is refused while a write is still being saved: wait
  until `writes.saving` is 0. It reports `applied` only when nothing of the owner is left in the store; `partial` means
  some nodes remained: run it again. It does not touch backups (see Backups).

## Upgrades

Build new images, change the tags in the settings file, `down`, `up`. The Neo4j and journal volumes are kept; the
schema step completes the schema; accepted writes resume. The library cannot be replaced under a running server, and
an older library must not be pointed at a journal a newer one wrote: roll back by restoring the volumes taken before
the upgrade, not by changing the image tag back.

## Backups

Back up the Neo4j volume and the journal volume together, with the server stopped (`down`), so the two agree. A
restored backup brings back what was erased after it was taken: after restoring, run `erase` again for every owner
erased since that backup, before the robot is used. Delete backups on the same schedule you promise owners.

## Logs and privacy

The server prints every user transcript and assistant reply to its console (upstream behaviour, with or without
memory), so container logs hold conversation text: restrict who can read them and set a retention. The library's own
lines hold no content. The journal holds the words owners asked to remember until they are erased.
