# Protected local knowledge copies

The optional `pack-local-state` extension owns local pack policies and amendment history. It is
off by default. Registering the extension does not activate it or add a query to an ordinary pack read.

## Shape

`PackLocalMount` is keyed by `id`, derived from the host application, resolved owner and pack.
It writes `application_id`, `owner_id`, `pack_id`, `base_digest`, `base_version`, `write_protected`,
`automatic_learning`, `active`, and `revision`. A transient `lock_token` forces the transaction lock
and is removed in the same statement.

`PackLocalChange` writes `id`, `mount_id`, `type`, `key`, `actor`, `base_digest`, `payload`,
`created_at`, `invalidated_at`, and `valid_until`. The typed assertion in `payload` is bounded to
64 KiB and JSON depth 32. It cannot claim a publisher source, confidence ceiling or authority to
close unrelated keys. `SUPERSEDED_BY` reuses the existing relationship type; no new relationship
type is introduced.

`PackLocalRevision` (extension version 2) stores `id`, `mount_id`, `base_digest`, `base_version`,
`write_protected`, `automatic_learning`, `active`, `revision`, `created_at`, `invalidated_at` and
`valid_until`. Each mount/use, policy change, rebase and amendment commit records the effective policy
and published base pin in the same transaction. Superseded pins use `SUPERSEDED_BY` and exclusive ends.

## Cypher

`PackLocalGate` (extension version 3) declares `id` and transient `lock_token`. It serializes
local writes/mounts and trusted canonical withdrawal across every owner of a pack.
`0003_pack_local_gate.cypher` creates `pack_local_gate_id`. It adds the .NET-only label
`PackLocalGate`; the property delta reuses `lock_token`.

`0001_pack_local_state.cypher` creates `pack_local_mount_id`, `pack_local_change_id` and
`pack_local_change_scope`. The migration is additive and runs only while this extension is active.
Runtime statements live in [PackLocalQueries](../../src/AgentMemory.Neo4j/Queries/PackLocalQueries.cs).
`0002_pack_local_revision.cypher` adds `pack_local_revision_id` and `pack_local_revision_scope`.

Enable through `Neo4jOptions.Extensions.Add("pack-local-state")`, then bootstrap schema or run
`migrate --extensions pack-local-state`. The local writer refuses mounting until its unique constraint
is present; it never applies DDL itself.

## Semantics

An imported publisher pack remains shared and unchanged. Local assertions are scoped by resolved
store, application/brain and owner. A mount captures its initial protection; later library-default
changes do not change the installed policy. A detach protects the copy while it is out; mounting it again keeps the
protection the owner had set before the detach, and where the owner had kept it writable, the host's request at that
mount applies (a mount with no revision history from before the detach stays protected). Policy changes and every writer acquire the same mount
lock before checking revision, protection, active state and base digest.
Writes also resolve write ownership before database work. Reads reject a mounted digest that changed
after the host bound its verified pack; the host must reconnect with the new verified revision.

Protection blocks user and automatic writes. Automatic learning additionally requires its own
permission and persisted user-message provenance. Sources naming messages are checked against actual
conversation membership and owner. Policy changes increment the revision, so work queued under an
older policy cannot commit after protection wins the lock. A write that already serialized before
the protection change may complete; the UI shows the acknowledged committed policy.

The source excerpt must occur in an actual persisted user message. When this extension is active,
canonical withdrawal acquires the pack gate and removes local payload/history copies of withdrawn
keys (and assertions linking to them) in the same transaction as base erasure and tombstones.
This is explicit publisher withdrawal, not ordinary amendment supersession. With the feature off,
canonical erasure issues no local-copy query or lock. Tombstones also filter historical reads and
block reintroduction. Maintenance refuses atomically above 100,000 stored amendment versions.

Updates retain the old version with `invalidated_at`, `valid_until` and `SUPERSEDED_BY`. Restore
appends a marker that reveals the published value; it does not delete history. Protecting an edited
copy retains existing local values. Detaching one mount retains shared records and personal history.
Automatic cleanup of shared baselines is intentionally not performed by detach.
`PeekAsync` and restrictive `DetachAsync` use only the resolved owner/brain policy metadata. They
do not require a readable or loaded published base and cannot unlock, enable learning or rebase it.
Detach uses the expected revision under the mount lock. Removing a conflicting upgrade, a missing
archive, or an expired/unreadable base therefore never attempts to activate that content first.
`PeekManyAsync` reads at most 32 current policies and assertion-presence flags in one owner/brain
scope. `PackLocalStatus.HasAssertions` is a derived query result, not a stored property or new shape;
it excludes superseded assertions and restore markers and returns no record keys or values.

The effective graph is projected before lookup, lexical search, meaning ranking and traversal.
Local values never inherit publisher provenance. Compatible upgrades revalidate all amendments before
rebasing; incompatible keys/types/links refuse activation, preserving the prior policy/history.
Historical storage reads select both the policy/base pin and amendments at the requested clock;
they never return the present policy with past values. Existing v1 mounts start pin history on their
first post-migration mount; earlier unknown pins are unavailable rather than fabricated. Reconstructing
an effective historical graph additionally requires the exact retained verified published revision.
DemoBrain's adapter resolves that exact retained revision under current trust before applying the past
assertions. Missing/expired/revoked bases are explicitly unavailable. Automatic learning is disabled
while viewing the past. Startup validates desired upgrades before composition and uses the retained
base on conflict. Rebase requires the prior verified base whenever personal assertions exist, rejects
downgrades and refuses converting an overridden published key into a local addition.

## Conformance

`Local_copy_protection_rechecks_queued_writes_and_keeps_owner_history_separate` runs against real Neo4j:
protected writes, automatic permission, stale queued work, independent owners, restore and retained
history. Its first run without the protection guard failed because a protected write succeeded.
The `Local_foundation_*` integration fixtures additionally exercise real extension migrations,
two owners and two brains over an unchanged shared baseline, identical identities in two physical
stores, reconnect, compatible/incompatible upgrades and stale-digest reads. Effective lookup,
lexical search, host-scored meaning matches, typed filters, anchors and changed-link traversal
are checked against persisted amendments. The queued-writer fixture holds a real transaction open
and waits for Neo4j to report the blocked transaction; deliberately removing its early lock allowed
the write after protection and failed the test. Write-owner refusal is checked before any IO.
`Local_changes_replace_before_search_and_do_not_mutate_the_verified_base` checks projection before
search. DemoBrain also checks captured defaults, scoped stack state and pure inspection.

This is implemented and locally tested; it is not an upstream compatibility-kit feature or a claim
that applicability pruning has a calibrated recall guarantee.

## Parity delta

Adds .NET-only labels `PackLocalMount`, `PackLocalChange` and `PackLocalRevision`. The extension's additional-property
delta names `application_id`, `pack_id`, `base_digest`, `base_version`, `write_protected`, `automatic_learning`,
`active`, `revision`, `lock_token`, `mount_id`, `type`, `key`, `actor`, `payload`, `valid_until`.
Common version and ownership properties `id`, `owner_id`, `created_at`, `invalidated_at` remain
explicitly declared on the shapes. No upstream label is adopted or removed, no new relationship
type is added, and no upstream property reservation is requested.
