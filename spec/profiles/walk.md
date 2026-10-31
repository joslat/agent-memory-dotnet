# Walk profile

**Version 1, draft.** Conformance class: **Walk Host**. Requirement ids: `R-WALK-nnn`. Key words as in Core §1.2.

## 1. Introduction

### 1.1 Scope

The Walk profile defines the module declarations a pack carries (`ontology.json`, `views.json`, `traversal.json`), how a
host maps them onto its module model, the kinds `definition` and `snapshot`, and the walk: a generic, bounded,
deterministic procedure that starts from the work in hand, follows only declared relations, and offers ranked records
with their reasons to the host's admission gate.

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Walk profile when it holds `traversal.json`. The kinds
`definition` and `snapshot` and the files `ontology.json` and `views.json` are defined here too, and a Walk Host
implements them.

### 1.3 Conformance

**R-WALK-001.** A Walk Host MUST implement Ampack Core as a Core Reader, and every requirement of this document.

**R-WALK-002.** Two Walk Hosts that follow §5 MUST offer the same records in the same order for the same
anchors, question and records (budgets aside), whatever store holds the records.

> Why: a trial result (Trial profile) carries from one host to another only if both walk alike.

## 2. The module files

### 2.1 Shape

**R-WALK-003.** `ontology.json`, `views.json`, `traversal.json` and `evals/trial.json` MUST be the matching members of
the module manifest, whose JSON Schema (draft 2020-12) is `agentmemory.module.v1alpha1.schema.json`; the schema is
normative for shape, names and ranges.

**R-WALK-004.** An integral number MUST be read as an integer whether written `2` or `2.0`, the numeric `min` and
`max` of an `int` property included.

**R-WALK-005.** The module files MUST be read strictly: a member the schema does not define MUST refuse the pack.

**R-WALK-006.** A host MUST also apply the rules the schema cannot state: an anchor names an owned string
property; anchors are unique by (type, property) and relations by (name, direction); a traversal relation is an owned
relation and never into `core:` entities; a hub names an owned type; `containableBy` names module ids or namespaces in
the id grammar; a view reads only declared types; the label and relation prefixes hold; reserved names are not used;
display texts hold no control characters.

**R-WALK-007.** The schema and the host's validator MUST reach the same verdicts on the edges: 16 and 17
traversal relations, 16 and 17 anchors, a traversal with neither, trial tasks 9, 10, 200 and 201, an upgrade tolerance of
-0.01, 0, 0.05, 0.5 and 0.6, `hops` written 2.0, an `int` bound written 2.0, and a `containableBy` entry that is not an id.

### 2.2 Contents and defaults

| File | Holds |
|---|---|
| `ontology.json` | `labelPrefix`, types (properties, render templates, scope), relations |
| `views.json` | Search and graph views |
| `traversal.json` | Anchors, walkable relations (direction, weight, fan-out), hubs, `maxItems`, `hops` |

**R-WALK-008.** Where a field is absent, a host MUST use these defaults: traversal `maxItems` 4 and `hops` 2;
anchor `match` `exact` and `kind` `path`; relation `direction` `out`, `weight` 0.8 and `fanOut` 8.

**R-WALK-009.** A traversal MUST declare at most 16 relations and at most 16 anchors; `maxItems` MUST be 1 to 16,
`hops` 1 to 3, `fanOut` 1 to 32, and `weight` greater than 0 and at most 1.

**R-WALK-010.** An anchor declaration's `kind` MUST be `path` or `symbol`; `key`, `entity` and `text` are
reserved and MUST be refused.

### 2.3 The module manifest

**R-WALK-011.** A host MUST assemble its module manifest from a pack by a pure mapping: `pack.json` (`id`, `version`,
`sdk`, `requires`, `display`, `containableBy`), `ontology.json`, `views.json`, `traversal.json`, `evals/trial.json`, and
the record files as seed; then validate the manifest as it validates any manifest.

**R-WALK-012.** An assembled module manifest MUST be at most 128 KiB and nest at most 32 deep.

**R-WALK-013.** A host MUST refuse a record its module's ontology does not admit, naming the file and line,
before any record is imported; a link through a relation the ontology does not declare MUST be such a problem, never a
failure of the host.

## 3. Kinds definition and snapshot

**R-WALK-014.** A `definition` pack MUST hold declarations only (ontology, traversal, views, trial); its records
MUST come from the host's importer for that kind, never from `records/`.

**R-WALK-015.** A `snapshot` pack MUST hold a definition's records for one source at one commit, at most 250,000
records, of shared types only, and MUST require exactly its definition; a host MUST refuse a snapshot for another
definition or a version outside the definition's range.

**R-WALK-016.** A host MUST import a snapshot's records through its snapshot import path, which writes only
what changed, and MUST NOT compose a snapshot as a module.

**R-WALK-017.** A snapshot pack without a source commit MUST take its version as its source.

**R-WALK-018.** A host SHOULD report a snapshot pack against its own commit as fresh, stale (with how many commits
behind) or unknown; only a snapshot that names a commit can be judged.

**R-WALK-019.** Records imported from a snapshot MUST keep the pack that carried them, with its signer.

**R-WALK-020.** A module whose `validUntil` passes while it is loaded MUST contribute nothing from then on, and
SHOULD report an omission naming the date.

> Why: a definition (the schema, the walk and the trial) is published once; a snapshot of it per repository and commit
> is what anyone can share, signed.

## 4. Ordering and text

### 4.1 Numbers and text

**R-WALK-021.** Every number in the walk MUST be an IEEE 754 double.

**R-WALK-022.** Case folding MUST be ASCII only: `A`–`Z` fold to `a`–`z` and nothing else folds (so `Ä` and `ä`,
`µ` and `μ`, `İ` and `i` stay apart). Every implementation MUST fold this way, store filters included.

**R-WALK-023.** White space MUST be space, tab, CR, LF, form feed and vertical tab only (a non-breaking space is a
character like any other); a digit MUST be `0`–`9`; anything else compares as it is.

**R-WALK-024.** The walk MUST run for live requests only, never for a request with an as-of time.

### 4.2 The format's order

**R-WALK-025.** Wherever records tie, they MUST be ordered by module id, then type, then key, each compared by
UTF-16 code unit, then by version id. Every tie in §5 MUST be broken by this order.

> Why: version ids are each store's own, so the order is the same in every store while (module, type, key) is unique
> among the live records in scope; that holds unless an owner's record and a shared record share a key, or, read with no
> owner filter, two owners' records do.

## 5. The walk

### 5.1 Anchors

An anchor is `{kind: path|symbol, value}`.

**R-WALK-026.** Anchors MUST be taken by one intake at every entry point (a host, a server mounting packs, its
remote operations, pack tests), in request order; an entry point MUST pass anchors as given and leave every step to the
intake, and what it echoes, or asks a question about, MUST be the list the intake took.

**R-WALK-027.** The intake MUST take each value in turn: (1) a value longer than 2,048 UTF-16 code units is
dropped before any work; (2) the value is normalized by its kind, a path as in R-WALK-037 and a symbol
trimmed of ASCII white space; (3) it is put in NFC by the format's own Unicode 15.0 tables, the same ones the JSON profile
checks with, whatever the host's globalization mode; (4) a value that is then empty, longer than 512 UTF-16 code units,
or holds a lone surrogate is dropped; (5) the values are made distinct by (kind, value); (6) the first 32 are kept.

**R-WALK-028.** The intake MUST be idempotent: what it takes, taken again, is the same list.

**R-WALK-029.** An entry point MAY refuse a request of more than 256 anchors.

> Why: NFC shortens text at most four to one under Unicode 15.0, so a value longer than 2,048 code units cannot come
> within 512 after normalization.

**R-WALK-030.** Stored text MUST be in NFC: a record's key, its string properties, its link targets and the keys
it replaces, so that an anchor in any normal form reaches the record it names.

### 5.2 Taking part

**R-WALK-031.** Only modules that declare `traversal` and an ontology, and are live (not past
`validUntil`: valid through that instant, off after it), MUST take part; a module that is not live MUST neither seed,
steer nor offer.

### 5.3 Question words and matches

The question is optional.

**R-WALK-032.** The question's words MUST be found thus: put the question in NFC by the format's tables (as given when
it holds a lone surrogate); split identifiers where a lower-case letter or digit meets an upper-case letter; lower-case;
take the runs of `[a-z0-9]` (after ASCII folding); keep those of 3 to 40 characters that are not in the common list
below; distinct, in order, at most 16.

The common list: the, and, for, with, from, that, this, into, when, are, not, but, its, has, have, was, were, make, made,
add, adds, fix, fixes, test, tests, one, two, three, all, any, only, now, new, also, than, then, them, they, their, our,
out, off.

**R-WALK-033.** A record's *matches* MUST be how many of the words occur (ordinal substring) in the ASCII-folded
join, by single spaces, of its type's text properties: the string properties its `render` template names, in template
order, distinct; without a template, every string property in ordinal name order.

### 5.4 Seeds

**R-WALK-034.** Seeds MUST be found for each module in dependency order (levels of the dependency graph, ordinal
by id within a level) and, within it, for each declared anchor `{type, property, match, kind}` in declaration order: the
anchors of that kind are matched against the property's value of every live record of the type in the request's scope.

**R-WALK-035.** A store MUST scan every candidate, none left out before matching (it MAY scan a superset); only the
time budget MUST stop the scan, in every store alike.

**R-WALK-036.** A store MUST return the matches only, best first (the match quality below, then key and id,
ordinal), at most 257: the 256 a walk keeps and one to say there are more.

**R-WALK-037.** A path MUST be normalized before it is compared, in one pass: each `\` read as `/`, trailing
ASCII white space dropped, then the leading run of ASCII white space, `/` and `./` dropped (so `./ a` is `a`). A
normalized path MUST normalize to itself.

> Why: the one pass equals the loop (trim, `\` made `/`, leading `./` then leading `/` removed, the whole repeated until
> nothing changes), which stays its oracle; as a loop it was quadratic in a run of `./`.

**R-WALK-038.** Two paths MUST be equal when they are equal after ASCII folding. A value, or a piece of one, that
normalizes to nothing MUST match nothing.

The quality of one pattern against one anchor:

- **R-WALK-039.** `exact` MUST give 1.0 when the normalized value is not empty and equal to the normalized anchor,
  else 0. Every `exact` declaration MUST normalize as a path and compare ASCII-folded, a symbol's included (so
  `removepackasync` seeds `RemovePackAsync`).
- **R-WALK-040.** `prefix` MUST split the value on space, tab, CR, LF, `,` and `;`, take each pattern without its
  trailing slashes, and give 1.0 when equal; when the anchor starts with `pattern/`, `0.5 + 0.3 × min(1, depth / total)`,
  where depth = slashes in the pattern + 1 and total = slashes in the anchor + 1; else 0.
- **R-WALK-041.** `glob` MUST split as `prefix` does. Without `*` or `?`, a pattern MUST be a prefix match (a pattern
  ending `/`) or an exact match. With them, `*` MUST match within a segment, `?` one UTF-16 code unit in a segment, `**/`
  zero or more folders and a bare `**` anything, matched over the ASCII-folded pattern and anchor, the whole anchor from
  its first code unit to its last; a match MUST give 0.7.
- **R-WALK-042.** `name` MUST split on CR, LF, `,` and `;` only, trim each piece of ASCII white space, and compare
  ordinally and case-sensitively: 1.0 when equal; 0.9 when the simple names are equal and not empty.

**R-WALK-043.** A simple name MUST be found thus: the text after the last space; without trailing `#`, `.`,
`/`, `:` and `!`; cut at the last `(` when it ends with `)`; then what follows the last `/`, `#`, `.` or `:` (so
`…/Neo4jModuleStore#` gives `Neo4jModuleStore`, and `…#RemovePackAsync().` gives `RemovePackAsync`).

**R-WALK-044.** A record's seed score MUST be its best quality over every pattern and anchor; 0 is no seed.

### 5.5 The cost of seeding

A seed's work is bounded by one cost model, charged where the work happens and the same in every store.

**R-WALK-045.** Anchors MUST be normalized once; a value's pieces MUST be read and prepared as the first anchor
reaches them and kept for the others; a glob MUST be matched by stepping positions through the anchor, with no
regular-expression engine.

**R-WALK-046.** A blank value (empty or ASCII white space only) MUST cost nothing and match nothing. Any other value
MUST be read piece by piece from its start, as below.

- **R-WALK-047.** *Reading.* A piece MUST run from where reading stopped to the next separator of its match
  (`glob`, `prefix`: space, tab, CR, LF, `,` and `;`; `name`: CR, LF, `,` and `;`; `exact`: none, so the whole value is
  one piece); form feed and vertical tab MUST NOT separate. Reading a piece MUST cost its characters, separator excluded,
  plus 1. A piece MUST then be trimmed of the six ASCII white-space characters; one empty after trimming is read and
  charged, never prepared or compared. Reading MUST stop at the value's end: a separator at the very end opens no piece.
- **R-WALK-048.** *Preparing.* Preparing a non-empty piece MUST cost 32 plus 4 for each character of the trimmed piece,
  or plus 6 for each when the match is `glob`, whether or not that piece holds a wildcard.
- **R-WALK-049.** *An equality.* An equality of two strings MUST cost 1, plus their length when their lengths are equal.
- **R-WALK-050.** *Comparing.* Comparing a prepared piece with an anchor MUST cost: for `exact`, an equality of the folded
  paths when the piece's path is not empty, nothing otherwise; for `prefix`, 1, then nothing more when the piece's folder
  (folded path, trailing `/` removed) or the anchor's path is empty, else an equality, then twice the folder's length when
  the anchor's folded path is longer and has `/` just past it; for `glob`, 1, then, for a piece with `*` or `?`, the glob
  steps below when the anchor's path is not empty, and for a piece without, nothing more when the piece's folded path or
  the anchor's path is empty, else a `prefix` comparison when the piece ends with `/`, else an equality; for `name`, an
  equality of the piece and the trimmed anchor, and when that fails and the piece's simple name is not empty, an equality
  of the simple names.
- **R-WALK-051.** *Glob steps.* A pattern's tokens MUST be what it compiles to: each literal character and each `?`
  is one token; `**/` is one token, `**` not before `/` one, `*` one. Of two wildcard tokens side by side, a `**` not
  before `/` absorbs the other, and two of one kind are one; other neighbours stay two tokens. If the pattern has more
  literal and `?` tokens than the anchor has characters, it MUST fail with no steps. Otherwise each anchor character
  stepped MUST cost the token count plus 1, until the pattern can match no further (it fails then) or the anchor ends.

**R-WALK-052.** The total MUST be checked after each piece is read, again once its preparation is charged and before
it is prepared, after each comparison, and before each character a glob steps; once it passes 2^20, nothing more MUST be
begun. The value then keeps the best match found so far, in the order of this section (anchors in intake order, then
pieces in order).

**R-WALK-053.** Where several anchors and pieces reach a seed's score, the first anchor in intake order, and
for it the first piece of the value, MUST name the seed's reason.

### 5.6 Offering seeds

**R-WALK-054.** A declaration's seeds MUST be offered at hop 0, best score first, then in the format's order, each
with specificity 1, until 256 records are kept.

**R-WALK-055.** A seed's reason path MUST be the anchor (shortened as in R-WALK-067) and, when the
matching pattern is not the anchor itself, ` matches <property> <pattern>`, with the pattern trimmed, in the strict
character set and at most 80 characters. The pattern is the anchor itself when, for a name, the two are equal (ordinal,
after trimming), and for a path when they are the same path (normalized and folded as above).

### 5.7 Keeping records

**R-WALK-056.** A record MUST be kept once, by its version id, with its best score; a later path MUST replace it only
with a strictly higher score.

**R-WALK-057.** At most 256 records MUST be kept in all and 192 per module; a record offered past either MUST NOT be
kept. Offers MUST happen in the order this section states, so the caps keep the same records in every store.

### 5.8 Hops

**R-WALK-058.** `hops` MUST be the largest any taking-part module declares (1 to 3, default 2). At hop *h*, the walk
MUST take each module whose own `hops` ≥ *h*, in the order of R-WALK-034, and each of its declared relations in
declaration order; the starting records are those of the frontier (what the previous hop kept, or the seeds) of the
relation's start type (`from` for `out`; the target type for `in`), each at its best score.

**R-WALK-059.** A relation into another module MUST be followed only with that module's consent, as the Compose
profile defines it; the validator MUST refuse a traversal relation into `core:` entities.

**R-WALK-060.** A starting record with more than 16,384 links of the relation MUST NOT be expanded through it, and
the walk MUST report that it "has more than 16,384 … links", in every store. The links are counted as distinct live
records in scope at the other end: for `out`, what the record's own links lead to; for `in`, the records linking to any
version of it.

**R-WALK-061.** The link limit SHOULD be checked from the store's degree first, so that only a record past
it there has its live links counted.

**R-WALK-062.** Otherwise every one of the record's live neighbours in scope MUST be ranked: most matches
first, then fewest records sharing the link, then the format's order; the first `fanOut` (1 to 32, default 8) are taken.

**R-WALK-063.** A neighbour's *degree*, counted up to 1,024, MUST be: for an `out` hop, the distinct live records in
scope whose same relation leads to any version of it; for an `in` hop, the distinct live keys in scope its links of the
relation lead to (a link to an older version of a live record counts once; one whose key has no live version does not).
Specificity MUST be 1 / degree.

**R-WALK-064.** A store MAY return more than the first `fanOut` neighbours (every neighbour that ties the last one
kept, for instance); the walk itself MUST then make the cut by the order of R-WALK-062, so two stores keep
the same neighbours.

**R-WALK-065.** Each neighbour's score MUST be the parent's score × `weight` (0 < weight ≤ 1, default 0.8); a
neighbour below 0.1 MUST be dropped.

**R-WALK-066.** A neighbour's reason path MUST be the parent's, then ` → ` and `<verb> <name>` (`out`) or
`<name> <verb> it` (`in`), where the verb is the relation name without the module's prefix, in lower case, with `_` as
spaces.

**R-WALK-067.** The name in a reason MUST be the record's `name`, else its `path` (each only when it is not blank
after trimming ASCII white space), else its key, shortened: a SCIP symbol to its simple name, a path to its last segment.
A value, trimmed of ASCII white space, is a symbol when it holds both a space and `#`, or ends with `#`, `)` or `.`, and
its simple name is not empty; otherwise it is a path, shortened only when something follows its last `/`.

**R-WALK-068.** In every segment of a reason path (the anchor, a pattern, each name), characters outside ASCII
letters, digits, `._-/:#*()<>,+@` and space MUST be replaced by `_` (each UTF-16 code unit on its own), and the segment
MUST be at most 80 characters (77 and `...`). The cap applies to each segment, not to the whole path.

**R-WALK-069.** The neighbours of one relation MUST be offered by score (descending), matches (descending),
specificity (descending), then the format's order of the neighbour and of the record it was reached from.

**R-WALK-070.** An expansion MUST start from at most 256 records.

> Why: reasons reach a model. Built from relation verbs and record names, paths or keys in a strict character set, never
> from free text, a reason cannot carry instructions.

### 5.9 Order and admission

**R-WALK-071.** A record's rank MUST be score × (1 + matches). Records MUST be ordered by rank (descending), specificity
(descending), hops (ascending), then the format's order.

**R-WALK-072.** Each module MUST then offer, in that order, its own records whose type is not a hub, at most its
`maxItems` (1 to 16, default 4), each with its reason path, to the host's admission gate.

**R-WALK-073.** A server that mounts packs, its remote operations and pack tests MUST show exactly this
selection.

**R-WALK-074.** The admission gate MUST refuse text that is not in NFC.

### 5.10 Budgets

**R-WALK-075.** The walk MUST stop, keeping what it has, at 256 kept records, 128 store reads, or 5 s. A store read
MUST be counted for each anchor declaration with anchors of its kind and each relation with starting records, per read
issued.

**R-WALK-076.** A stop MUST change nothing in §5 for the records already kept.

**R-WALK-077.** The time budget MUST be checked before each read and on every record a store scans or row it
fetches, and each read MUST carry the time the walk has left; a store that can stop a statement on its server MUST set
that time as the transaction's timeout, and a stop there is a stop by time.

**R-WALK-078.** The walk MUST report what it spent and why it stopped, if it did.

**R-WALK-079.** A read that fails MUST be that module's omission, with a problem, never the walk's.

**R-WALK-080.** What the walk could not do (a record past the link limit) MUST be listed in one order, whatever the
store's.
