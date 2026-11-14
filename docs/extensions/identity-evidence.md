# Identity evidence

Host assertion storage, bounded candidate lookup and identity decisions. Registration does no database work.
Since 2.2 the extension follows evidence-based identity: it is added wherever identity runs, which by default is
wherever the store-aware writer runs on Neo4j (`IdentityResolutionOptions.Enabled` null; `true` always, `false` never),
and its scripts are applied by schema bootstrap with the rest of the migrations (since 2.3.0; before, only by an explicit
`IMigrationRunner` call, which nothing in the library made, so installed applications ran identity without its unique
constraints and lookup indexes). Adding it by hand does not by itself switch identity on. See [identity on by default](../getting-started.md#evidence-based-identity-is-on-by-default-22) and the
[versioned implementation checkpoints](../evidence-based-identity-implementation.md).

## Shape

`(Entity)-[:HAS_IDENTITY_EVIDENCE]->(IdentityEvidenceAssertion)` records explicit authority and source.
Entity properties: `identity_evidence_version`, `identity_calendar_key`.
Assertion properties: `id`, `assertion_kind`, `namespace`, `authority`, `source_id`, `owner_scope`,
`payload`, `lookup_key`, `surface`, `subject_kind`, `calendar_key`, `created_at`.

## Cypher

The extension owns `0001_identity_evidence.cypher`: unique assertion IDs and lookup, surface and
calendar-key indexes. Query text lives in `IdentityEvidenceQueries`. Every input is parameterized.
Exact IDs use K+1 to detect truncation before hydrating at most K contexts. Lexical/vector pools and
candidate context are bounded. Evidence loads at most 65 assertions; the extra row marks the
candidate incomplete, blocking confirmation and model adjudication.

## Semantics

`IIdentityEvidenceRepository` registers additive keys, aliases, kinds and name namespaces. Calling
code is responsible for verifying its authority/source. Extraction attributes and ordinary aliases
remain untrusted. Writes require the entity's exact owner; null selects shared entities only. Shared
scope and an owner literally named `*` have different scope encodings. No physical merge occurs.

Composite key values and component names use ordinal comparison. Extra supplied attributes do not
alter the configured key shape. Authority name comparison currently supports Ordinal only; an
OrdinalIgnoreCase policy requires another capable backend. Fuzzy/vector relevance never proves
identity. New calendar-value lookup requires explicit host kind registration; this increment does
not backfill old date entities or repair historical facts. Namespace assertions bind the exact
primary name at registration. Changed primary/canonical calendar names are rechecked by Core.

Assertions are idempotent by entity, content, authority and source. Repeating existing support leaves
the evidence revision unchanged, so it cannot invalidate a confirmed decision replay. New support
advances that revision. Conflicting kinds are rejected in the write transaction. Conflicting keys
are retained as contradictions for policy evaluation. Retirement and decision reversal belong to
the subsequent review/binding increment, which remains unactivated.

## Conformance

This increment adds backend contracts and isolated persistence coverage. It has no TCK bridge profile
and no measured model-accuracy claim. Default extraction does not resolve or call these services.
The extension is off by default; its APIs reject use while disabled before issuing queries.

## Parity delta

NET-only label `IdentityEvidenceAssertion`; NET-only relationship `HAS_IDENTITY_EVIDENCE`.
Superset properties: `identity_evidence_version`, `identity_calendar_key`, `assertion_kind`,
`namespace`, `authority`, `source_id`, `owner_scope`, `payload`, `lookup_key`, `surface`,
`subject_kind`, `calendar_key`. The shared names `id` and `created_at` retain their base meaning.

## Version 2: atomic decision storage under qualification

`0002_identity_decisions.cypher` owns unique decisions, observation keys, revisions, bindings and
evidence-scope locks. New labels: IdentityDecision, IdentityDecisionRevision, IdentityFactBinding,
IdentityEvidenceScopeLock. New relationships: HAS_IDENTITY_BINDING, DECIDED_BY, RESOLVES_TO,
HAS_IDENTITY_REVISION, POSSIBLE_SAME_AS. These are separate from legacy ABOUT and SAME_AS.

Owned properties: identity_observation_key (also on Entity), identity_record_json,
identity_target_snapshot, identity_revision, identity_lock_version, identity_decision_id,
identity_fact_role, identity_observed_surface, identity_source_ids. Nodes retain id, owner_scope and
created_at with their established meanings. These shapes are also the explicit parity delta.

Host evidence assertions and initial decisions serialize their owner/shared evidence scopes to
detect competing exact-key proofs at persistence. Original observations and stable stored fact-role
support are retained. A confirmed target is not upserted. Payloads freeze before awaits and are
bounded in UTF8. Rollback/replay/concurrency, consumer reads, review/revocation and erasure pass the
newer native checkpoints. See the implementation status for exact current evidence and model limits.

The atomic receipt stores the actual bounded candidate evidence and policy outcome observed inside
the write transaction. The supplied client receipt remains separate untrusted data. A replay also
refreshes the complete exact-key universe before appending support; an unchanged target alone is
not sufficient when a competing identity has appeared. Reserved-ID collisions fail explicitly.
These corrections reproduced real-store failures; their green qualification remains required.


Evidence-based ingestion wiring: identity runs by default where the store-aware writer runs on Neo4j, and
`services.Configure<IdentityResolutionOptions>(o => o.Enabled = true)` runs it everywhere; the schema extension is
added in both cases. With `Enabled = true`, pipeline construction rejects a backend without bounded candidates or
atomic scoped ingestion. Switched off (`false`), it resolves no identity backend and issues no identity query. In the enabled mode,
model/candidate/embedding work precedes the transaction; owner/shared identity locks precede
fact writes. Original subject/object words remain stored, confirmed targets are not upserted,
and decision-owned bindings use the repository-returned final Fact ID. A failure rolls back
the whole enabled ingestion; there is no best-effort replay. Same-surface ambiguous observations
remain separate and receive no guessed fact or relationship endpoint binding. Source context
and mentions are bounded (MaxContextUtf8Bytes, MaxMentionsPerIngestion). Review/revocation and
durable contextual persistence are qualified. Positive event expansion requires confirmed identity;
the current implementation status records both the physical positive case and the remote Pending limit.

The enabled ingestion owns its outer transaction boundary: starting it inside a caller-owned
write transaction is rejected before extraction, embedding or contextual model work. Idempotent
identity scope-lock rows are infrastructure and may remain after an unsuccessful attempt. New
facts, observations, decisions and fact-role support commit together. Optional writer-named
closings are a separate best-effort follow-up after that commit, like other post-write consequences:
model failure or cancellation can leave the new facts committed and the older facts still live.
A missing commit acknowledgement does not start that follow-up; retry uses stable source keys.
The initial composition and lock-acquisition correction have accepted unit/live checkpoints;
those historical results do not qualify later contextual/lifecycle work or the entire delivery.

## Version 3: native ownership and erasure

`0003_identity_owner_stamps.cypher` fills missing private `owner_id` stamps on
IdentityEvidenceAssertion, IdentityDecision, IdentityDecisionRevision, IdentityFactBinding and
IdentityEvidenceScopeLock. `owner_id` retains its existing base-schema meaning and is declared on
each added label. Shared records have no private stamp. The exact `owned:` scope prefix preserves
private IDs such as `*` and `**`; assertion IDs are unchanged by the upgrade.

All new records carry the stamp at creation, so the existing owner-erasure path removes receipts,
revisions, assertions and support as well as memories. Disabled mode adds no erasure query.
Apply the explicit extension upgrade before relying on erasure for older identity records. The
upgrade fills absent ownership; it does not reinterpret contradictory manually stamped records or
provide a concurrent-ingestion erasure barrier. These paths require their real-store acceptance.

## Module vocabulary boundary

The module SDK reserves this extension's five storage labels, six relationship types, ten index/
constraint names and the Entity properties `identity_evidence_version`, `identity_calendar_key`
and `identity_observation_key`, even while the extension is inactive. A module cannot redeclare
these owned shapes or write those identity properties. Other domain names under an `Identity`
prefix remain valid. The reservation catalog has no runtime Neo4j dependency; SDK conformance
tests compare it to the actual storage declarations and shipped migrations.


### Initial host assertions

Core can obtain explicit host keys/kind/name-namespace provenance independently of extracted
attributes. The new-observation indexing increment writes eligible assertions through the decision's
same Neo4j runner under the existing scope locks, together with the observation, receipt and facts.
It never upserts an existing confirmed target and replay does not re-register original assertions.
Incomplete calendar declarations remain in their receipt without an active calendar lookup key.
The initial increment passes the current 230-case real-store consumer checkpoint, including new-key
rediscovery, assertion rollback, replay and incomplete-calendar behavior. Subsequent contextual
confirmation and review/revoke checkpoints are described below and in the current implementation status.


### Optional contextual interpretation

Core invokes an explicitly registered IIdentityAdjudicator outside the write transaction for
eligible pending reference candidates. Calendar literals and deterministic vetoes never use it.
One call budget is shared by each ingestion's mentions; later/concurrent ingestions get independent
budgets. MaxAdjudicationsPerIngestion counts logical requests, not provider-internal retries.
The host controls SDK retry behavior. Requested output tokens and UTF8 context are bounded.

IdentityDecision.ContextualEvidence retains a typed verdict, offered candidate and exact citations.
Opaque caller EvidenceJson cannot authorize confirmation. Same/different require supporting
quotes; semantic model correctness is not proven by an exact quote. By default a same verdict
remains pending. AllowContextualConfirmation is an explicit per-type authority choice.

The persistence follow-up rechecks the complete source text and ownership, source invalidation,
candidate snapshots and fresh deterministic vetoes before admitting a contextual confirmation.
Only complete message fragments with their own origin ID can acquire contextual authority.
Generated/aggregated fragments remain conservative. A changed alternative or new exact candidate
invalidates the original comparison. Backend-generated candidate hashes in the stored receipt
support later freshness checks; supplied JSON is nested separately and cannot provide those hashes.
Confirmed replay rechecks the stored comparison and refuses to append support after relevant changes.

These source/candidate review corrections pass the current 249-case real-store consumer checkpoint. Existing
links are not automatically revoked when evidence later changes: scoped review/revocation and
its consumer checks remain a separate open phase. Idempotent persistence does not imply a retried
ingestion spends no further model calls, because planning runs before decision replay.


## Version 4: original relationship observations under implementation

The extension additionally declares IdentityRelationshipObservation, SOURCE_IDENTITY and
TARGET_IDENTITY. The original assertion has optional links to its source/target decisions;
RESOLVES_TO can also connect those decisions to their current permitted Entity targets.
No ordinary RELATED_TO edge is adopted or rewritten. The two new parity-delta property names
are identity_source_decision_id and identity_target_decision_id. The record also uses the owned
id, owner_id, owner_scope, identity_record_json, identity_source_ids, identity_lock_version,
created_at and valid_until properties. Migration 0004_identity_relationship_observations.cypher
owns identity_relationship_observation_id and identity_relationship_observation_owner.

IIdentityDecisionRepository has default-safe SupportsRelationshipIngestion and original-record
save/read methods. Existing backends compile and explicitly reject this unsupported capability.
Payloads freeze bounded JSON without arbitrary attribute getters, retain original names/provenance
and use a fingerprint independent of confidence, time and endpoint interpretations. Value-type
arrays use the same finite-value, rank and collection bounds as other allowed JSON collections.
The current 22 focused payload controls pass after reproducing the primitive-array rejection.

The isolated Neo4j storage passes a 57-case decision checkpoint including seven new relationship
storage/role/rollback/isolation/bound cases. Subsequent pipeline, scoped readers, reviewed owner erasure and safe format refusal pass the newer
312-case native and1,439-case SDK checkpoints. Full portability remains separate. The new schema vocabulary is reserved from module declarations even while off.
These changes do not close the relationship/revocation phase or supersede the earlier separately
accepted contextual snapshot. Ambiguous relations must stay inspectable without guessed identity.


Projection-readable mirrors on IdentityRelationshipObservation use the existing base property
names relation_type, confidence, description and attributes. They are declared by this extension
for its record label; no base shape is repurposed. The original JSON remains independently retained.


The version-4 migration also owns identity_relationship_legacy_id, an additive RELATED_TO.id
index for collision checks. Original-record writes reject an already used ordinary relationship
ID. A later out-of-band collision makes single-record projection reads fail explicitly; ending
an observed record never ends a colliding ordinary edge. These checks do not repair arbitrary
external writes or turn the separate storage shapes into one global database uniqueness constraint.


The original-claim ingestion and logical reader now have a 108-case real-Neo4j checkpoint and
581 focused passes per supported runtime after query/collision/retired-target review corrections.
Forgotten or merged-away Entity anchors cannot gain new identity support or remain in the active
projection. Original claim inspection remains available. This is bounded scoped acceptance;
broader module/full-root/current-consumer qualification remains open at that historical source cut. See the newer scope below: full identity portability is separate.


### Explicit review revisions (under qualification)

`IIdentityDecisionRepository.ReviewAsync` now uses the existing extension-owned
`IdentityDecisionRevision`, `HAS_IDENTITY_REVISION`, `IdentityFactBinding`, `DECIDED_BY`
and `RESOLVES_TO` shapes. This increment adds no schema label, edge type or property.
The current decision JSON carries a backend-generated `LatestReview` receipt. The original
observation is unchanged in each immutable revision. Caller-supplied initial review receipts
are rejected; arbitrary JSON is not review authority.

Confirm/reject/revoke use the exact owner scope and an expected revision. A stable operation
key plus a backend-computed request hash separates idempotent retry from a changed request.
The transaction revalidates a selected target and changes only that decision's owned fact
bindings and current endpoint pointer. Logical relationship projection follows its two
decision pointers; the raw relationship assertion stays separate. Review does not physically
merge Entity nodes. A confirmed observation's former provisional Entity is excluded from
identity candidate lookup while its decision points elsewhere, avoiding duplicate authority.
Revocation can return the decision pointer to that original Entity.

The first review backend passes129 real decision/ingestion cases: review/revoke/history,
rejection surviving replay, later operation retry, stale/foreign refusal, concurrency, rollback,
restart, subsequent support and domain-veto combinations. Both later stale-alternative defects now pass with the correction. Final core acceptance remains open; full identity portability was subsequently separated by the owner. Existing contextual links are not automatically
revoked when later evidence changes; the host must explicitly review that interpretation.


### Event details through decision-owned support

`RecallOptions.EventDetails` is an opt-in live recall capability, off by default. After date-window,
upcoming and overlap selection, it takes at most10 dated seed facts and follows their current
`HAS_IDENTITY_BINDING` / `DECIDED_BY` / `RESOLVES_TO` support to live `EVENT` entities. One scoped
read considers at most20 event identities and returns at most `MaxEventDetailFacts` additional facts
(default8, maximum64). It does not traverse `POSSIBLE_SAME_AS`, generic `ABOUT`, names, people or
locations to guess that two events are equal. Closed facts and retired events are excluded.

Details such as Córdoba or required physical presence need no date of their own. They must already
have explicit fact-role support for that event; missing extraction/binding evidence is not invented.
This increment does not expand undated relationship-only claims or infer an event from words buried
inside a sentence. The representative model cases will measure this coverage and any required fix.

Extra facts enter `RelevantFacts` without invented vector scores, are de-duplicated against the date
sections, respect the latency allowance and share the existing total context budget. `MaxFacts=0`
or a zero detail cap suppresses the read. Point-in-time recall rejects the live-only option explicitly.
An unsupported backend rejects requested use instead of returning silent success. Neo4j requires
the enabled and migrated `identity-evidence` extension. No new schema shape is introduced.

The implementation compiles on .NET8/9/10 and has focused unit plus actual Neo4j qualification.
The physical-event model case proves the native hop; conservative Pending identities can omit
details. See [current evidence and limits](../evidence-based-identity-implementation.md).

### Recall for people

`RecallOptions.PersonDetails` (null follows identity, like `EventDetails`) adds the facts about a confirmed person the
question names. The owner's confirmed PERSON identities are read with the names each is known by (its name, its aliases
and the names facts bound to it used), at most 1,000. A name appears in the question as whole words, case and accents
aside; a name two identities share names neither, a one-letter name or a pronoun names nobody, and at most 3 people are
taken in the order the question names them. Their live facts come through decision-owned fact bindings, newest first
and taken in turn from each person, at most `MaxPersonDetailFacts` (8; 0..64), excluding facts already in the context.
A decision that still has `POSSIBLE_SAME_AS` candidates (an open proposal) is never followed, but its name counts:
a confirmed person whose name an open proposal shares is ambiguous too. The facts carry no
invented similarity score and share the context budget and the latency allowance. Choosing between two people with the
same name from the question's context, and questions that name nobody ("what does she like?"), are out of scope.

### Identity portability boundary

With `identity-evidence` enabled, export writes format `agentmemory-export/2`: the memories as in format 1, plus
`IdentityState`, the owner's identity records as stored (decisions with their evidence and quotes, review revisions,
fact bindings, host assertions and relationship observations), the links between them and to the exported entities and
facts, and the entities' identity properties, each value with its type. Messages stay out: decisions keep their quotes,
and their links back to the messages point nowhere after a move. Import into a store with identity storage brings the
owner back whole: each observation gets a new key for the new owner (the same shape, so its decision and entity ids
follow it as the ingestion makes them), every other record a new id, and every reference is rewritten, inside stored
JSON too: record, entity and fact ids, observation keys and the owner's scope; the owner itself only where a property
names the owner. Open proposals, review history and event details work as they did. A link to an entity that was not
imported (a shared one) is made to it as it is. Importing the same export into several owners gives each its own keys.

A format-1 export of an identity store (2.2.0), or a format-2 export imported into a store without identity storage,
comes back as the current state: each mention's entity is folded into its confirmed identity, which keeps the mention's
name as an alias, and an open proposal's two entities come back apart. Either way imported entities are never joined
to what the destination already holds, so an import cannot merge two different things. Import while the owner is not
being written. With the extension disabled the format and its queries are unchanged. Owner erasure remains supported.

### Writer event-reference option

`LlmExtractionOptions.MemoryWriterEventIdentities` defaults to null: on where evidence-based identity runs, off
elsewhere (an explicit `true` or `false` wins). With the store-aware writer
enabled, it appends instructions to emit source-grounded `EVENT` observations and separate atomic
event-subject facts for the stated date, place and participation. The writer's default prompt
remains unchanged. This addresses the write/read seam: a retrieval hop cannot recover event details
if the writer never emits an event reference. It does not confirm identity, invent missing sources
or bypass the resolver's domain vetoes; model compliance still requires the bounded actual runs.

Enable this option alongside the existing identity writer configuration and `Recall.EventDetails`.
It is explicitly enabled in the synthetic `graph-identity-writer` arm and is not a new global
Recommended default. Unit controls for the unchanged default prompt and event-shaped writer output
pass in the current .NET8/9/10 matrix. The real physical-event hop passes; a remote identity remains Pending, so automatic expansion is not guaranteed.
