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
MUST offer only the records live at the request's valid time (`AsOf`), or now when it names none, by one definition of
liveness, decided where the walk seeds, steers and offers and where a view's records leave the store; a store given a
valid-time pin directly MAY refuse it.

> As built, a view's limit is applied in the Neo4j store before the records not live are left out, so a view can return fewer
> than its limit while records past their life fill it; the in-memory store filters first.
