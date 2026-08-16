# `arithmetic`

**Answers that must be computed rather than found.**

16% of LongMemEval questions have a derived answer — a count, a difference, a latest-of-chain, a
duration, a list. The store holds `800` and `50`; the answer is `750`, and nothing ever wrote it down.
Every retrieval-side idea in this project died against a saturated coverage ceiling (0.965–0.980); what
remains alive is the class of answers retrieval structurally cannot produce, because they are properties
of a **set** and retrieval returns a sample of it.

The **session accountant** is a deterministic post-persistence pass. After an extraction batch commits,
it looks only at the `(subject_key, predicate_key, owner)` groups that batch touched, and materialises
aggregates as ordinary facts.

## Shape

**No new label.** A derived fact is a `:Fact` carrying `fact_kind='derived'`.

| Piece | Value |
|---|---|
| Properties on `:Fact` | `fact_kind`, `derivation_key`, `derivation_operator`, `derivation`, `derived_at` |
| Relationship type | `DERIVED_FROM` (Fact → Fact) |
| Migration | `0001_derived_fact.cypher` — `fact_derivation_key_idx`, `fact_kind_idx` |

A `:DerivedFact` label was rejected: it costs a label allowlist entry **and** forfeits free recall, since
every fact query matches `:Fact`. Strictly more parity risk for strictly less function.

### Why `DERIVED_FROM` earns its allowlist entry

Two parity-free alternatives were considered and both fail on something specific.

Reusing `EXTRACTED_FROM` is wrong twice over: it points at `:Message`, not `:Fact`, and it would poison
the provenance instrument with edges that are not extraction provenance at all.

A JSON property listing input fact ids is parity-free but **not traversable in the direction that
matters**. The staleness cascade needs *"every derived fact whose inputs include the fact being
superseded"* — evaluated **inside** the supersede statement. With a JSON list that is a full `:Fact`
scan per supersession. With an edge it is one relationship expansion. The cascade is the safety property
of the whole feature, so it gets the edge.

## Cypher

### Reading a group

```cypher
MATCH (f:Fact)
WHERE f.subject_key = $subjectKey
  AND f.predicate_key = $predicateKey
  AND f.invalidated_at IS NULL
  AND coalesce(f.fact_kind, '') <> 'derived'
RETURN f
ORDER BY coalesce(f.valid_from, f.created_at) ASC, f.id ASC
LIMIT $limit
```

**The order is the arithmetic.** A delta computed over an unordered group subtracts two arbitrary
members and reports the result as a change. Valid time first, so a fact learned yesterday about 2019
sorts as 2019; the `created_at` fallback matters as much, because most extracted facts carry no valid
time at all and dropping them would leave every group too small to aggregate.

**`fact_kind <> 'derived'` keeps the DAG one level deep.** Aggregating aggregates would make the cascade
recursive, and a recursive cascade inside a supersede statement is one that eventually gets moved out of
the transaction "for performance" — at which point stale derived values become retrievable.

### Writing an aggregate

Identity is `derivation_key`, a SHA-256 of `subject_key|predicate_key|operator|owner_key` computed **in
C#, never in Cypher** (`MemoryTripleCanonicalizer` lowercases *and* collapses whitespace runs; Cypher's
`toLower` does neither, and the two disagree outright on U+0130). The **object is deliberately absent
from the key**: an aggregate's value changes on every recompute, so including it would spawn a fresh
node per observation and leave one dead aggregate behind each time.

`invalidated_at = null` on every write **re-arms** a previously cascaded-out aggregate whose group
became live again. That is what lets the cascade afford to be blunt.

### The cascade

Appended to `FactQueries.Supersede` and `FactQueries.Invalidate`, in the **same statement**:

```cypher
WITH DISTINCT loser
OPTIONAL MATCH (derived:Fact)-[:DERIVED_FROM]->(loser)
SET derived.invalidated_at = coalesce(derived.invalidated_at, datetime($now))
WITH DISTINCT loser
```

A derived `750` whose input `800` was superseded is a manufactured confident-wrong answer — stored,
embedded, recallable, and carrying inline provenance that makes it look verified. An
eventually-consistent sweep would leave a window in which exactly that is retrievable, so this is
same-statement or it is nothing.

**Unconditional, not gated on this extension.** If the accountant is switched off while derived facts
exist, staleness protection has to survive the flag — otherwise turning the feature off would freeze
every aggregate it ever wrote into permanent truth.

## Semantics

Six operators, all **LLM-free**. Answer-time decomposition died 0/29 on perfect context and the answer
model is the noisiest component in the stack; moving arithmetic from a stochastic reader to a
deterministic writer is the entire bet. An LLM-assisted operator would reintroduce exactly the
hallucination surface this exists to remove.

| Operator | Derived predicate | Default | Notes |
|---|---|:-:|---|
| Count | `count_of:<p>` | on | Works on non-numeric objects; counting never needed the number |
| Delta | `delta_of:<p>` | on | Last minus first, in chain order |
| Latest | `latest_of:<p>` | on | Distinct from supersession, which needs a writer to have *noticed* |
| SetEnumeration | `set_of:<p>` | on | Case-insensitive dedup, capped, truncation stated |
| Sum | `sum_of:<p>` | **off** | Allowlisted predicate keys only |
| Duration | `interval_of:<p>` | **off** | Real `valid_from` on both ends |

**Sum is allowlisted, not inferred.** Summing is meaningful only for additive quantities, and there is
no way to tell an additive predicate from a non-additive one by looking at it. Adding three temperature
readings produces a number whose arithmetic is exactly right and whose meaning is nonsense — the kind of
error no audit of the arithmetic can catch.

**Duration is off because of the data, not the code.** The current evaluation corpus stamps
`UnixEpoch + counter`, so durations computed there are fiction with a plausible shape. It also refuses
the `created_at` fallback the rest of the group ordering accepts: an interval between two extraction
timestamps measures when the system was *told* things, not when they happened.

**Refusal is the recurring theme.** Every numeric operator refuses a group containing any unparsable
object rather than computing over the parsable subset — the change between two values that happened to
be readable is not the change over the chain. Nothing aggregates a single fact: an "aggregate" of one is
the fact restated, occupying a second slot in the same budget its input already occupies, and carrying
derived provenance for arithmetic never performed.

The number parser is the **only** hallucination surface in the feature. It strips a leading currency
symbol and thousands separators and then defers entirely to `decimal.TryParse` under the invariant
culture. It does not attempt "twice a week", "a couple", "about 800", or unit normalisation — each of
those is a guess, and a guess here becomes a stored number wearing provenance that makes it look
verified.

### Rendering

`17 — derived: 12 (a1) + 5 (b2)` — the inputs and operator inline, so the model can **check** the
arithmetic rather than trust it. A derived number presented bare is a claim; presented with its inputs it
is an argument.

### Guard G2, structurally

A derived fact carries **no merge-key quadruple at all** — no `subject_key`, `predicate_key`,
`object_key` or `owner_key`. The write path MERGEs extracted facts on those four properties and
`FindByTriple` looks them up the same way, so a derived node carrying them could be matched by either: a
user restating a number would silently merge *into* an aggregate, overwriting its value while leaving its
`DERIVED_FROM` edges and derivation string in place — a fact wearing provenance for arithmetic that never
produced it. Omitting the properties makes that **unreachable rather than unlikely**, since MERGE and the
lookup both require a non-null match on every column. Nothing is lost: the group read excludes derived
nodes by design, recall reaches them by vector, and isolation reads `owner_id`, which is still set.

## Conformance

**Guard G1 — the cascade is cardinality-safe.** `OPTIONAL MATCH` binds nothing on a store with no
derived facts, and the `SET` then applies to a null row: a no-op producing no extra rows, so the
surrounding statement's `count()` and `RETURN` are unchanged. The `WITH DISTINCT` before it is what makes
that true — without it, a fact with N derived dependants would multiply the outer row N times and the
caller's "did it work" count would report N instead of 1.

**Guard G2 — the fact upsert cannot merge into a derived node.** See above; enforced by omission rather
than by a filter, because MERGE cannot carry a `WHERE`.

The TCK audit confirms `FactQueries.Supersede`/`Invalidate` are unreachable from every bridge endpoint,
so the cascade cannot be observed by a conformance run.

## Parity delta

| Kind | Entry | Why |
|---|---|---|
| Net-only relationship type | `DERIVED_FROM` | The cascade needs graph traversal; see above |
| Net-superset property | `fact_kind` | Marks computed rather than observed. **Not `kind`** — upstream already has a `kind` property meaning "audit-node discriminator", and overloading a name whose meaning another implementation owns is the changed-semantics hazard a parity check cannot catch. The first draft used `kind` and the verifier rejected it |
| Net-superset property | `derivation_key` | Recompute-in-place identity |
| Net-superset property | `derivation_operator` | Which arithmetic produced the value |
| Net-superset property | `derivation` | The inline, checkable provenance string |
| Net-superset property | `derived_at` | When it was last recomputed |

One deliberate relationship-type entry, five documented properties, **zero labels**.

## Host wiring

Off by default, and off is byte-identical: the accountant runs post-persistence, is LLM-free, and
touches no prompt bytes in either state; with the flag off it is never invoked, so the graph is
byte-identical too.

```csharp
services.AddNeo4jAgentMemory(neo4j => neo4j.Extensions.Add("arithmetic"),
    configureMemory: memory =>
    {
        memory.Extraction.DerivedMemory.Enabled = true;
        memory.Extraction.DerivedMemory.AdditivePredicateKeys.Add("fish_count");
    });
```

| Option | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | master flag |
| `Operators` | Count, Delta, Latest, SetEnumeration | Sum and Duration are opt-in |
| `AdditivePredicateKeys` | empty | Sum's allowlist |
| `MaxDerivedFactsPerBatch` | 32 | ceiling per extraction batch |
| `MaxGroupFanIn` | 200 | ceiling on facts read per group |
| `MaxEnumerationItems` | 10 | ceiling on listed values |
| `DerivedFactConfidence` | 0.9 | an admitted guess; the audit data should calibrate it |

## Prerequisite: predicate vocabulary

This feature **hard-depends** on `LlmExtractionOptions.UsePredicateVocabulary`. Aggregation requires two
facts to agree they are instances of the same predicate; with 421 distinct predicates over ~700 facts,
they never do, and every operator computes garbage groups.

That is the **V1 void witness**: if `distinct predicate_key / live fact count > 0.5` over the built
corpus, no operator result is interpretable and the run declares itself void rather than reporting a
number. Measure it with `--extraction-compare --vocabulary-ab`, read against a `--repeat` run on the
same arm.

## Known gaps

- **Cross-predicate duration pairs** ("how long between the interview and the offer") are out of scope:
  the pair space is unbounded and needs a question-driven, retrieval-time selector, which is a different
  design.
- **Unit normalisation** ("twice a week" → 2/week) is Phase 2, LLM-assisted, behind its own flag.
- **Recompute overwrites** rather than superseding the previous derived value. Revisit if
  `IMemoryHistoryService` consumers want the chain.
- **Budget competition**: derived facts claim `MaxFacts` slots alongside their own inputs. Bounded by
  merge-in-place identity and `MaxDerivedFactsPerBatch`; if measurement shows crowding, the projection
  layer can collapse inputs into the derived line.
