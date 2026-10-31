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
