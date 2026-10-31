# The AgentMemory host binding

How AgentMemory stores a pack's records (`spec/core.md` §15.1). Other hosts bind the record model their own way; nothing
here is a requirement on a pack's meaning.

## 1. Members of `ontology.json` this binding reads

| Member | Meaning in AgentMemory |
|---|---|
| `labelPrefix` | The prefix of every type and relation the module stores, claimed by one module only |
| a type's `scope` | `owner` (each owner's own records) or `shared` |
| a type's `render`, `color` | How a record is shown |
| a relation's `core:` target | A link into AgentMemory's own entities |

**R-HOST-050.** A type's name MUST begin with the module's `labelPrefix` and a relation's with its upper-snake form,
except built-in types and relations, which are named in the pack by their built-in names.

## 2. Built-in types

**R-HOST-051.** A built-in type MUST be stored as the prefix and the type's name in PascalCase (`note` as `<Prefix>Note`), a
built-in relation as the prefix's upper-snake form and the relation's (`part_of` as `<PREFIX>_PART_OF`), with `shared`
scope; a record's `type` and a link's `relation` MUST be mapped the same way as the record is read. A host that writes such
records into a pack MUST write their built-in names, and never its own binding (the `builtIn` member it keeps on them).

**R-HOST-052.** A pack that uses built-in types and declares no `ontology.json` MUST be given the prefix of its id's last
segment in PascalCase (`com.example.lantern-notes` as `LanternNotes`); when that prefix is another module's, the pack MUST
be refused, as any prefix two modules claim is; its author then declares a `labelPrefix` in `ontology.json`.

> Why: stored names stay one module's own, as every other type's are, and a minimal pack still needs no ontology.

## 3. A record's life in the store

**R-HOST-058.** A store MUST keep a pack record's effective life (R-CORE-128 to 130) as its instants, `valid_from`, `valid_to`,
`expires_at` and `review_by`, and a changed life MUST be a new version of the record, never left as it was. Every view and walk
MUST offer only the records live at the request's valid time (`AsOf`), or the host's now when it names none, by one definition
of liveness, applied where records leave the store, before any limit, anchor match or fan-out cut counts them, and again where
the walk seeds, steers and offers. A history view lists versions and is not filtered.

**R-HOST-059.** A valid-time pin alone reads the store's current knowledge as of that world time: what the host now holds
true then, including records it learned later; a record without a life is true at every time. To read what the host held
then, a request names its transaction time too (`SystemAsOf`), which views read and a walk refuses. Core memory recall reads
both clocks at the pin; module records keep their own lives, so the two may differ, by design.

> Why: a pack states when its facts are true; the host decides when it learned them. Mixing the two clocks would hide a fact
> learned yesterday about last year, which is exactly what a trial or an audit asks for.
