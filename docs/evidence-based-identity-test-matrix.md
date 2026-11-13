# Evidence-based identity: acceptance matrix

> **13 November 2026: full Bariloche acceptance reopened.** Candidate-source context and partial writer operations remain active corrections; see the [root-cause audit and bounded validation matrix](evidence-based-identity-root-cause-review.md). Earlier component/source-cut results are retained, not full-scenario acceptance.

This matrix specifies observable guarantees, not a claim of perfect accuracy. A finite test suite can establish its stated cases and invariants; model generalisation remains a separate measurement.

## Execution status

The [implementation evidence](evidence-based-identity-implementation.md) records the qualified
core/source cuts:8,040 current full-root cases,662 focused cases per.NET8/9/10,312disjoint affected actual
Neo4j cases and1,439completeSDK cases, plus391PackFormat/55SemanticKernel/1,062LongMemEval.
Do not sum overlapping subsets or relabel older source cuts current after a new tool change.

All four short real scenarios were executed and reviewed. The physical event qualifies native
location/presence expansion; the remote answer respects permission, but Pending identity leaves
its native permission hop unqualified. The checker false positive was reproduced/corrected.

The original full49-statement Bariloche evaluation has now run. Its26tool controls and
8,040current root cases pass, while the real scenario fails strengthened Q3/native event context:
all6store checks pass, but the location/presence identity is Pending. See the
[actual result](evidence-based-identity-evaluation-results.md) and
[executable entry point](../tools/AgentMemory.Scenarios/README.md).
The cumulative USD1ceiling remains. This is not an exhaustive model matrix or a numeric
precision benchmark; unit success does not override a failed business-scenario result.

## IDER-01: temporal integrity

| Family | Cases | Observable requirement |
|---|---:|---|
| Similar spellings, different dates | 5 | Fuzzy cannot authorise date identity, including Spanish and ISO forms. |
| Missing year or day | 7 | Do not invent missing precision or choose an existing identity from a matching name. |
| Incorrect historical alias | 1 | An alias cannot override a different canonical calendar value. |
| Equivalent complete dates | 4 | Compare calendar components, including leap day, without embeddings. |
| Original fact value | 3 | Canonical names cannot replace the extracted calendar literal, even with an equivalent display format. |
| Ordinary person canonicalisation | 1 | Existing person resolution still works. |
| Matcher entry points | 4 | Exact, fuzzy, partial and semantic name matchers cannot authorise calendar identity. |
| Index × batching × auto-merge | 8 | The temporal rule holds across all eight combinations. |
| Invalid or ambiguous calendar input | 4 | Preserve input without silently repairing or interpreting an ambiguous numeric date. |
| Semantic target is a calendar value | 1 | An ordinary object cannot become a date because vectors coincide. |
| Batch embedding preparation | 1 | Calendar literals do not trigger name embeddings during pre-warming. |
| Cancellation | 1 | A cancelled resolution does not continue to persist. |
| Month word in an ordinary name | 2 | A person named May and a product containing December remain ordinary entities. |

The mock embedder deliberately returns the same vector for different names. This makes similarity an adversarial input rather than a hidden source of correct answers. Repository mocks return concrete candidates and support both indexed and non-indexed paths.

Typed-value regressions exercise the supported grammar, precision, invalid dates, leap boundaries, type boundaries, cultures and unknown shapes. See [CalendarValuesTests](../tests/AgentMemory.Tests.Unit/Resolution/CalendarValuesTests.cs) and the additional temporal increment below. Missing calendar identity precision must not come from the machine clock; writer date-field inference separately uses the original message timestamp.

## IDER-02: policy and bounded candidates

- Complete verified composite keys: equal, different, missing component, namespace mismatch, duplicate key in multiple records, and numeric versus string representations.
- Unverified extractor attributes cannot acquire the authority of application-provided identity.
- Exact names, confirmed/unconfirmed aliases, ambiguous homonyms, conflicting given names, prefix names and numeric identifiers.
- Hard contradiction × exact/fuzzy/vector score × adjudicator outcome: contradiction always wins.
- Equal scores, input reordering, within-batch versus cross-batch arrival, repeated source IDs and repeated summaries.
- Correlated name signals are not independent evidence; ubiquitous graph neighbours do not establish identity.
- Owner, shared/private, entity type and namespace boundaries, including a backend returning an out-of-scope candidate.
- Limits are applied by the candidate provider, not after an unbounded type scan; unsupported backend capabilities fail configuration.
- Disabled mode makes no new repository or model calls and preserves the previous bytes and side effects.

## IDER-03: durable proposals

- Separate entity plus pending proposal and decision receipt commit atomically on real Neo4j.
- Inject failures before each write and at commit; confirm rollback and absence of partial proposals.
- Retry/idempotence, concurrent ingestion, restart, owner isolation and evidence/policy version round trips.
- Existing SAME_AS readers, graph expansion and exports do not treat a proposal as equality.
- Legacy records are not reclassified or repaired automatically.
- A backend without the declared capability rejects enabled configuration.

Additional consumer regressions have executed: four shared-source permission
combinations (with/without extracted mentions, shared access allowed/denied), one identical assertion
replay case and one genuinely changed key control. The fact-only shared-access and identical-key
replay cases initially failed; corrected code passes them in the current 230-case live checkpoint.

## IDER-04: contextual adjudicator

Use an in-memory IChatClient with controlled completions; no paid provider is required for deterministic tests.

- Confirm, decline and uncertain results; no candidates; multiple indistinguishable candidates.
- Malformed/truncated JSON, invented IDs, duplicate IDs, invalid enum, non-finite/out-of-range scores, missing or fabricated evidence.
- Source content contains instructions aimed at the model; application policy remains authoritative.
- No LLM on a deterministic confirmation or veto; one eligible call per decision and bounded calls per ingestion.
- Context byte and requested output-token limits, timeout, cancellation and a client that ignores cancellation.
- Late completions never persist; ordinary provider failures retain the original observation and a conservative decision.
- Model confirmation requires the type policy to allow it. Finding a quote alone does not verify the inferred conclusion.

## IDER-05: review and revocation

- Confirm/reject/revoke with expected version checks and audit evidence.
- Rejected evidence is not silently proposed again; genuinely new evidence can be distinguished.
- Subsequent writes, concurrent changes and restart preserve which links came from which decision.
- Revocation changes authorised links while preserving original observations and their source messages.

Three erasure cases are prepared for ordinary and reserved private owner IDs. They require deletion
of the owner's decision receipts, revisions, bindings, assertions and infrastructure while retaining
other owners and shared records. The initial three native-erasure cases failed because baseline
erasure selects owner_id while identity records only had owner_scope. Native owner stamps and the
explicit repeatable v2-to-v3 upgrade corrected that mismatch; all six native/upgrade cases now pass
in the 80-case real-store checkpoint. Review/revoke now has the qualified127-case increment described above; the stale-alternative
correction subsequently passes in the129-case checkpoint. The current136decision/ingestion
selection includes reviewed-owner erasure and safe legacy-format refusal;312disjoint affected
native cases and1,439complete SDK cases pass. Full identity portability is explicitly separate.

## IDER-06: event evidence

- Bruna Q3 requires go-live date, Cordoba location, required physical presence and the Bariloche interval.
- Undated location is recovered through a confirmed event link within explicit hop/item/byte limits.
- Pending proposals do not confer event identity, while original facts remain independently searchable.
- Include unrelated nearby events, conflicting locations, same date/different event, owner boundaries and evidence that exceeds the context budget.
- Retrieval tests inspect source facts, and response tests are scored separately. A required word in the prompt is not proof of a correct answer.

## IDER-07: qualification

Use the [bounded real-model protocol](evidence-based-identity-real-model-protocol.md): dates3/4/12,
ambiguous identity, go-live/Córdoba/on-site/Bariloche and remote participation. One initial run
per short case; add targeted reruns for actual findings or material variability. Inspect storage,
the actual prompt and the answer separately. A deciding model is not its own sole judge.

Retain failure evidence, identify the root cause, fix reproducible defects, keep a relevant
deterministic regression and rerun that trigger within the persistent1USD campaign ceiling.
Existing correctness/contract/DB regressions remain; the large historical/busy/model/repetition
matrix is optional future benchmark work. Complete required relevant checks before source push.
No numerical accuracy, perfect resolution or default-rollout claim follows from this small series.

## Additional temporal increment

Twelve persistence-path cases guard both roles against old incompatible aliases, wrong external
resolver types and contradictory primary/canonical values, while preserving valid links. Twenty-one
interval cases cover distinct/partial/invalid/reversed/equivalent ranges, year/leap boundaries,
canonical names and bad aliases; three bounded-planner cases require a range key containing both
endpoints and no name search for incomplete ranges. A separate runtime guard validates loaded binaries.

## Current bounded outcomes

See [implementation evidence and limits](evidence-based-identity-implementation.md). The physical
event proves native location/presence expansion. Remote participation is correctly stored and
answered, but its event identity remains Pending and its native permission hop is unqualified.
A name-word false positive was reproduced and removed from the tool checker; current controls
require structured event-subject property claims. Conservative uncertainty must not be forced
into confirmation to satisfy an evaluation. Source-cut boundaries remain explicit.
