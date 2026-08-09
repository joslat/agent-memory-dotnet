# Relation vocabulary

`relation-vocabulary.json` is the single reviewed source for **which relations this library knows**.
It is embedded into `AgentMemory.Core` and parsed once at first use.

## Why it exists

Extraction previously invented a relation name per phrasing. One measured graph held **700 facts under
421 distinct predicates**, with a single birth arriving as `was born`, `was born in`, `were born in`,
`had` and `welcomed`. Offering a controlled vocabulary at extraction time normalises this at the point
of writing, which is the only place it can be done safely — merging predicates *after* the fact would
eventually merge `bought` onto `sold` and invert the meaning.

## The one-table rule

```
canonical relation  ──►  surface forms
      │                        │
      │                        └── read side: the query lexicon, derived at load
      └── write side: the extraction vocabulary, injected into the extraction prompt
```

**Keys are the extraction vocabulary. The inverse index is the query lexicon, and it is derived, never
authored.** Both sides come from this one file, and tests enforce that they agree in both directions.

This rule exists because it was broken. The two sides were briefly maintained as separate lists and
drifted: **13 relations became resolvable at query time that the extractor was never offered**, so the
graph could not contain them however well retrieval worked. `assembled` was one of them — which is why
assembly was filed under `completed`, and why a benchmark question about furniture bought, assembled,
sold or fixed could not be answered from the graph at all.

**Only keys cross over.** Surface forms stay read-side: they never enter an extraction prompt, where
they would cost tokens on every call and invite the extractor to choose inconsistently between `buy`,
`buys` and `purchased` — the opposite of the consolidation the vocabulary exists to produce.

## Where the data comes from

| source | licence | what it contributed | fetched |
|---|---|---|---|
| [schema.org Action hierarchy](https://schema.org/Action) | CC BY-SA 3.0 | canonical keys for the **event** family — 34 relations, incl. the whole trade/transfer group | 2026-08-08 |
| [Wikidata](https://query.wikidata.org/) property aliases (`skos:altLabel`, SPARQL) | CC0 | surface forms for the **state** family — 6 relations, 141 alias rows over 10 targeted properties | 2026-08-08 |
| [PARAREL](https://github.com/yanaiela/pararel) paraphrase patterns | MIT | verb phrases per relation — **4 relations**; best-shaped source, its patterns *are* verb phrases (`is originally from` → `was born`, `passed away in` → `died`) | 2026-08-08 |
| [Rel2Text](https://github.com/kasnerz/rel2text) crowd verbalisations | Apache-2.0 | delexicalised phrases, `state==ok` rows only — **7 relations** | 2026-08-08 |
| [FewRel `pid2name.json`](https://github.com/thunlp/FewRel) | MIT | **surveyed, near-zero yield** — see below | 2026-08-08 |
| hand-authored | — | **60 relations** — the majority, and the opposite of what the plan assumed. No surveyed source covers domestic life | — |

### Why the two families are seeded differently

**schema.org Actions model events; a memory graph stores events *and* states.** Measured against the
top-50 predicates of a real graph, schema.org covers **38.0% by relation and 40.7% by fact mass**, and
**44.7% of fact mass has no Action mapping at all**. The unmapped set is not a tail of oddities — it is
the state/identity backbone: `is`, `is a`, `owns`, `has`, `works at`, `knows`, `belongs to`. The single
most common predicate, `is`, is **26% of every fact in the graph** and schema.org has no Action for it,
because it is the copula.

So the state family is seeded from schema.org and Wikidata **properties** instead, which is where
relations of that shape actually live: `P108 employer`, `P551 residence`, `P1830 owner of`, `P26 spouse`.

### Why FewRel contributed almost nothing

Its 744 Wikidata properties were matched against our relations and produced **28 apparent hits of which
all but one are spurious substring collisions** — `has pet`, `has melody`, `has grammatical mood`,
`has superpartner`, `has anatomical branch`, and `studied by` matching `died`. The one genuine match is
`P2283 uses`.

This is the same finding as the wider dataset survey, now verified directly: every relation-extraction
corpus reviewed (TACRED, DocRED, REDFM, FewRel, T-REx, NYT10, Google-RE, Wiki-NRE) is **encyclopedic or
newswire**. Their inventories describe grammar, physics, anatomy and geography — not what a person
bought, planned or fixed. **The hand-authored delta is therefore the largest component, which is the
opposite of what was originally assumed.**

### Sources deliberately excluded

| source | reason |
|---|---|
| Wiki-NRE, Google-RE, NYT10 | no licence stated anywhere |
| REBEL | README self-contradicts on non-commercial use |
| TACRED | paid ($25 for non-members) |

## What was done to the data

1. **schema.org** — Action hierarchy fetched in full (~110 descendant types) and mapped by hand to our
   relations. Each mapping is recorded per relation in `sources` as `schema.org:<ActionType>`.
2. **Wikidata** — English `skos:altLabel` aliases fetched by SPARQL for ten targeted properties.
   Filtered to **verb-like forms only**: at most three words, letters and spaces only, and not starting
   with `of`. A question contains "worked at"; it does not contain "alma mater" or "of employer".
3. **Provenance is earned, not asserted.** A `wikidata:` source is recorded only when that property
   actually contributed a form not already present. An early build claimed Wikidata provenance while
   containing none of its data, because a file-path error was silently swallowed; the generator now
   fails closed instead.
4. **Determinism** — keys and forms are sorted; regenerating from unchanged inputs is byte-identical.
   A vocabulary that varied per run would reintroduce the non-determinism it exists to remove.

## Invariants, enforced by tests

These fail CI rather than throwing inside a consumer's process on first use:

- every relation declares at least one source, and a `family` of `event` or `state`
- canonical keys are already in stored `predicate_key` form, so resolution produces keys the graph can match
- no surface form is claimed by two relations; ambiguity is dropped, never guessed
- no surface form collides with a *different* relation's canonical key
- opposing pairs are both present — `bought`/`sold`, `likes`/`dislikes`, `borrowed`/`lent`, `gave`/`received`
- everything offered to extraction resolves at query time, and everything resolvable is offered, unless
  explicitly listed in `retired`

## `retired`

Currently **`finished`, `welcomed`**. A graph does not rewrite itself when a vocabulary
changes, so removing a key must stop new writes without making facts already stored under it
unreachable.

Two mechanisms. **Demotion** removes the key and keeps its name as a surface form of the relation it
merged into, so old facts are reached through the survivor — this is what `finished` (into `completed`)
and `welcomed` (into `was born`) did. **Flagging** keeps the key and excludes it from the extraction
vocabulary. Demotion is preferred where a survivor exists, because it leaves one name for one meaning.

## `storedOnly`

32 forms across the table. These are fetched by expansion but never trigger retrieval from a
question. Two groups: the copulas (`is`, `was`, `had`, …), which appear in nearly every question and
would expand `is` — 26% of the measured graph — on all of them; and bare verbs that double as
assistant boilerplate (`plan`, `give`, `tell`, `know`, `need`, `want`, `find`, `work`, `order`, `own`,
`used`, `change`, `go to`). Each is re-admitted through a question-anchored phrase such as
`do i work` / `did i work`, so recall survives while the boilerplate stays silent. Bare `plan` was the
worst case: `planned` is the largest measured bucket at 839 facts, so *"which phone plan am I on?"*
could displace most of a retrieval budget on a question that had nothing to do with plans.

## Known limitations

- **Mined aliases are noisy.** Wikidata contributed nouns as well as verbs — `works at` acquired
  `location` and `organisation`, `belongs to` acquired `club`. A question rarely contains these, and a
  wrong surface form is not harmless: resolution expands a **whole relation** into a fixed retrieval
  budget, so one bad alias can displace correct items. These are under review.
- **Size.** 101 relations against a ~400 reviewability ceiling, with 619 surface forms. The ceiling applies to *keys*, which cost
  prompt tokens on every extraction call; surface forms are read-side and far cheaper.
- **Changing this file changes what gets extracted**, and only takes effect on a fresh build of the
  memory graph. Its content hash is recorded in evaluation reports so two graphs built under different
  vocabularies are never compared as though equivalent.
