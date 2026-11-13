# Evidence-based identity: implementation and verification

> **13 November 2026: full Bariloche acceptance reopened.** Candidate-source context and partial writer operations remain active corrections; see the [root-cause audit and bounded validation matrix](evidence-based-identity-root-cause-review.md). Earlier component/source-cut results are retained, not full-scenario acceptance.

This describes `feature/evidence-based-identity`, not a published release. The
[design](evidence-based-identity-design.md), [test matrix](evidence-based-identity-test-matrix.md)
and [real-model protocol](evidence-based-identity-real-model-protocol.md) define the scope.
Identity is off by default; disabled paths do not add identity queries, model calls or prompt text.

The original49-statement Bariloche scenario has now been executed with the current tool.
All6storage checks pass, but prep/native expansion miss location and required presence through
a Pending identity. The full business scenario is not accepted. See the
[full result and cause](evidence-based-identity-evaluation-results.md).


## Current scope

The implemented core separates candidate discovery from identity authority, retains original
facts and relationship claims, persists atomic decisions and supports explicit review/revocation.
Event recall follows current permitted bindings. Complete identity export/import was explicitly
separated; legacy-format refusal, owner erasure and isolation remain in scope.

| Work | Implemented and checked behavior |
|---|---|
| IDER-01/02 | Finite typed calendar equality and contradiction vetoes; bounded fuzzy/vector candidates; explicit verified host keys, types, aliases and namespace policies. Scores alone do not establish identity. |
| IDER-03 | Atomic observation/decision/Fact-role support; exact owner isolation including literal `*`/`**`; immutable original relationship claims with independent endpoint decisions. |
| IDER-04 | Optional dedicated-client adjudicator, shared call budget, constrained JSON response schema, strict candidate/quote checks and durable freshness/replay checks. Invalid or uncertain output cannot silently confirm identity. |
| IDER-05 | Confirm/reject/revoke with expected versions, immutable history and operation retry. Only the decision's owned support changes; unrelated legacy links and original facts survive. |
| IDER-06 | Opt-in dated-event expansion to explicitly bound undated details; current real physical-event validation recovers both location and required presence. Pending identity does not authorize the hop. |
| IDER-07 | Four bounded synthetic cases executed with manual answer review; failures retained and relevant triggers reproduced/fixed. The remote control exposes the expected pending-identity recall limitation below. |

## Verification evidence

Counts describe the indicated source cuts and scopes; do not sum overlapping subsets.

| Check | Actual result | Limit |
|---|---|---|
| Current root |8,040/8,040, zero skips; clean current root build |Includes six new full-evaluation controls. The earlier complete solution build remains its own source cut. |
| Current identity matrix |662/662 on each .NET 8/9/10, zero skips, loaded-library hash guards |Finite component/mock cases; does not prove model accuracy. |
| Actual Neo4j |312/312 disjoint relevant cases, zero skips |136 decision/ingestion,158 other affected consumers,18 legacy GraphRAG/relationship compatibility. |
| Complete SDK |1,439/1,439, zero skips |Current unchanged SDK/Core/schema cut; actual confidence reservation mutation killed and source restored. |
| Current bounded tool controls |26/26, zero skips; earlier two retained-data reassessments pass on their own cut |Fake transport and offline audit; actual full-case quality is separately failed. |
| Complete solution / unit consumers |All targets build with zero warnings/errors;391 PackFormat,55 Semantic Kernel,1,062 LongMemEval pass with zero skips |SDK and native scopes are listed separately. |

The 219-query catalog and extension-owned labels are checked by unit guards; changed Cypher was
also executed against disposable owned Neo4j. Local runs monitor available RAM and preserve a
5 GiB host reserve. No production database, hosted workflow, source push, merge or release is involved.

## Real-model findings and outcomes

The four approved short scenarios and the user-requested original full scenario use Bitdeer GLM-5.3-Flash and bge-m3. The persistent
campaign reserves worst-case cost before each HTTP dispatch and keeps unknown/failed outcomes.
After the full run:330 HTTP admissions (117 chat,213 embedding), USD0.37292261 conservatively
reserved of the approved USD1 total. This is a reservation bound, not an invoice.

| Scenario | Actual outcome |
|---|---|
| Dates3/4/12 |Store-aware writer retains all three literal dates and normalized fields3/3; actual prompt and answer give wedding December12. Separate EVENT identities remain. No DATE entity was emitted, so typed date-entity comparison is covered by deterministic/native regressions. |
| Two Alex Kims |Independent source-root review finds distinct effective identities. Both locations reach the prompt; answer maps coworker to Cordoba and cousin to Bariloche. Ambiguous exact-name lookup abstains and contextual verdict remains uncertain. |
| Physical go-live |Native time-window-to-event read returns undated Cordoba location and in-person requirement. All three original prompts include their needs; all answers identify the December11 conflict with the December9-13 trip. |
| Remote participation |Explicit remote permission and negation are stored and reach the normal prompt. Answer allows remote participation without inventing an unavoidable physical conflict. Its event decision remains Pending, so the permission is **not** available through native event expansion; that hop is unqualified. |

The first two paid results precede the later event-prompt/schema corrections; their exact source
cuts are retained. Current component regressions cover their deterministic boundaries. This is
not four repeated model runs on one final source snapshot or a numerical precision benchmark.

Diagnosed failures led to four concrete corrections: bound the validation model's reasoning effort;
remove conflicting owner-subject/known-mention instructions only in the event-aware writer mode;
provide a constrained adjudication response schema instead of generic JSON; and fix an evaluation
false positive that mistook `remote` in an event name for permission evidence. The stronger checker
uses structured subject/predicate/object and reclassifies retained remote-hop evidence as missing.
It does not rewrite the old report or turn a conservative Pending result into confirmation.

## Guarantees and limits

Original facts, quotes and provenance are separate from identity decisions. `POSSIBLE_SAME_AS`
is not equality. Pending observations keep their own identities. Review is reversible at the
decision-support level; no physical Entity merge substitutes for undo. Model claims do not become
verified host identifiers. A valid exact quote establishes provenance, not semantic truth.

Enabled ingestion owns its outer transaction. Models run before the callback; observations,
decisions and role support commit together. Unknown commit acknowledgement has explicit idempotent
retry semantics. Optional writer-named closings are a separate best-effort follow-up after an
acknowledged commit; failure may leave old facts live. Host code authorizes reviewers: an `Actor`
audit string is not authentication. External writes do not participate in identity scope locks.

Calendar parsing is finite EN/ES/ISO Gregorian dates/ranges. Missing precision is not invented from
the machine clock. General relative dates, arbitrary languages, time-zone instants and quantities
are outside that parser. Event identity is separate from date equality.

Unresolved relationship endpoints remain inspectable but are not invented or projected. In the
date case, the sister-of claim is retained with an unresolved Bruna endpoint; the answer therefore
qualifies that family link. Generic GraphRAG still traverses legacy `RELATED_TO` for its configured
knowledge corpus; this work does not add identity-claim traversal there. Automatic revocation after
later evidence changes is not implemented; explicit review is required.

EventDetails requires the enabled/migrated identity-evidence extension and explicit current
Fact-role bindings. It does not infer links from similar names or embedded object sentences.
Uncertain identity can therefore omit event details, as the real remote control demonstrates.
Broader historical candidate-source context is a future quality improvement; hosts can provide
verified identity evidence instead of demanding model confirmation from insufficient context.

## Configuration and compatibility

On by default since 2.2 (ILC-25): `IdentityResolutionOptions.Enabled` is null, which runs evidence-based identity
wherever the store-aware writer runs on a backend with identity storage (Neo4j) and the previous resolver elsewhere; one
decision (`IdentityActivation`) drives ingestion, the `identity-evidence` schema extension, context turns, the writer's
event identities, alias capture, event details and relationship reads, and the memory tiers line reports it
(`identity: evidence-based`, or `identity: previous resolver (why; it can merge different things that share a name)`).
`true` always runs it; `false` is the previous behaviour. The option defaults are the measured arm (4 candidates, 2
adjudications of 1,024 tokens within 45 s, `ContextTurns` 4; conversational policies in automatic mode), pinned by
`IdentityDefaultOnTests`. The adjudicator defaults to the extraction model; `AddIdentityAdjudication` replaces it.

`RecallOptions.EventDetails` defaults to null (follows identity; an explicit value wins); `MaxEventDetailFacts` defaults 8 (0-64). The read uses
at most10 dated seeds,20 events and64 additional facts. It shares latency/context budgets, has no
invented vector scores, and is suppressed by MaxFacts0 or a zero detail cap. Point-in-time recall
and unsupported backends reject requested use explicitly.

`LlmExtractionOptions.MemoryWriterEventIdentities` defaults to null (follows identity). Enabled, it uses coherent
event-aware base instructions plus generic source-grounded event/date/place/participation shapes,
preserving negation and original quotes. Default writer prompt bytes remain unchanged. The
dedicated chat adjudicator requests constrained JSON; providers refusing it safely leave Pending.
The synthetic arm composes these options explicitly and is not full Recommended/JEV.

With identity-evidence enabled, export writes `agentmemory-export/2` (ILC-30): `IdentityState` carries the five
identity record types, the nine identity link types and the entities' identity properties, typed. Import into an
identity store recreates them for the new owner: each observation key becomes `sha256(newOwner, oldKey)` (same shape, so
decision and observation ids follow it), other records get new ids, and references are rewritten by exact value, inside
stored JSON too; the owner only in owner-named properties. Messages are not carried. A format-1 export (ILC-18) or an
import into a store without identity folds each mention into its confirmed identity (ILC-26). Extension-off behavior
remains unchanged. Owner erasure is supported.

Finite successful tests support these guarantees within their scope. They do not establish
perfect extraction, automatic identity confirmation or full model accuracy.
