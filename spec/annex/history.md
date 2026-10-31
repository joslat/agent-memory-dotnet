# Annex: history (informative)

Where the rules of this draft came from. Informative; nothing here is a requirement.

## Versions of the format

| Date | Version | What changed |
|---|---|---|
| 26 Oct 2026 | v1 | The first design: signed packs of records and declarations, the walk, the trial |
| 26 Oct 2026 | v1.1 | After an adversarial review: one identity (`sha256:` over the canonical `META/files.json`), a strict ZIP profile, scoped keys, a host ledger, signed revocation, one JSON profile, Git rules, bounded walks, consent for containment, locks for composites, reason paths in a strict character set |
| 26 Oct 2026 | v1.2 | The content layout, five kinds, pack sets, the trial in the pack with a host baseline and confidence bounds, file shapes for protection levels and code tiers, serving, lifecycle |
| 27 Oct 2026 | v1.2 | Strict `pack.json`, locks and members; nesting; consent for links; content-bound decisions; mandatory trials per namespace; no-regression upgrades; staleness |
| 28 Oct 2026 | v1.2 | The federated profile and publication receipts; the pack card; evaluation certificates and certificate trust |
| 29 Oct 2026 | v1.2 | Readable minors, `critical`, closed values and the timestamp slot; the walk made normative (§6.1) and the module files (§6.2) |
| 31 Oct 2026 | draft 1 | One registry for every list (`registry.json`); profiles derived from what a pack holds; this split into Core, profiles and host documents with numbered requirements |

## Review rounds

Each round was an independent review of the design against the code. Findings were fixed at their roots, with vectors or
tests, before the next round.

| Round | Date | Main outcomes |
|---|---|---|
| Red team 1 | 26 Oct | v1.1: identity, strict ZIP, scoped keys, ledger, revocation, JSON profile |
| Red team 2 | 27 Oct | Divergences and crashes between the verifiers given one verdict each |
| Federation 1 to 4 | 28 Oct | Verifier agreement, whole-set checks, origins pinned, probes; ACP denies, publisher reach; undo order |
| Certificates 1 and 2 | 29 Oct | Unsigned attestations never stop a load; one rule for which decision counts, in the gate and the upgrade gate |
| 1 | 29 Oct | A strict trust policy, typos never fail open, a bounded Turtle reader; readable minors (M18), the timestamp slot (M15), reserved trial measures and anchor kinds (M16), the walk made normative (H5) |
| 2 | 29 Oct | Walk determinism across stores, a gated-off pack no longer breaks the host, closed shapes for every envelope |
| 3 | 29 Oct | ASCII-only folding and trimming everywhere, hub degrees as distinct live records, canonical P-256 keys, NFC anchors at every entry point |
| 4 | 29 Oct | The walk's degrees and anchors the same in every store and host; honest precision bounds |
| 5 | 29 Oct | One anchor intake for every entry point, stored text in NFC, authenticate first, bounded inputs, a statistic chosen by a harness, vectors held to their reason |
| 6 | 29 Oct | An exact statistic for correlated tasks (the unseen task and its weights, tasks in history order), authenticate first in every reader, bounded walk work, a host floor |
| 7 | 30 Oct | A registry of rules, doors and guards; one writer that reads its output back; the walk's work bounded by one cost model; a server deadline for reads; one mount plan |
| 8 | 30 Oct | Statistic `hedged/4` (neighbouring clusters correlated); one trust-free check at every door; verify whole at every act; one gate |
| 9 | 30 Oct | Federated set rules without a key; one trial policy check |
| 10 | 30 Oct | One rule for which copy of a family member counts; an AEF run tied to its decision; no future signing time |
| 11 | 30 Oct | Every door holds what the host composes, by running the host's own composition |
| 12 | 30 Oct | One decider of what packs are held together |
| 13 | 30 Oct | The snapshot rules inside the one decider |
| 14 | 30 Oct | Install runs the snapshot imports serve runs (the last round, a bounded stop) |

## Where this draft departs from the earlier text

- The earlier text named a field `profiles` in `pack.json`; it was dropped before it was built, because the files and
  fields a pack holds already show its profiles. Profiles are derived by the registry's map.
- v1.1 required timestamp tokens for official and curated packs. v1.2 reserves the slot only and judges no token; a
  revoked or expired key's signatures do not count.
- The `.NET` API names the earlier text used for host behaviour (composition, certificate trust) are described here by
  behaviour.
