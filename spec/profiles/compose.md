# Compose profile

**Version 1, draft.** Conformance class: **Compose Host**. Requirement ids: `R-COMPOSE-nnn`. Key words as in Core §1.2.
Format strings: `agentmemory.pack.lock/1.1` (`META/lock.json`).

## 1. Introduction

### 1.1 Scope

The Compose profile defines the hierarchy of packs: the kind `composite`, which contains other packs pinned by digest;
nesting; consent, by which a pack names who may contain it and walk into it; host grants; and the one rule by which a
host holds several packs together.

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Compose profile when it holds `members.json` or
`META/lock.json`, holds the `pack.json` field `contains`, or has the kind `composite`.

### 1.3 Conformance

**R-COMPOSE-001.** A Compose Host MUST implement Ampack Core as a Core Reader, and every requirement of this document. A
verifier that implements this profile MUST apply its verification requirements (§2 to §4) as part of Core §10 step 4.

## 2. Composite packs

A composite holds `contains` and `META/lock.json`, and no records of its own; records reach the store through its members.

**R-COMPOSE-002.** A composite MUST name what it contains in `contains` and pin it in `META/lock.json`, and
MUST carry `members.json`.

**R-COMPOSE-003.** A composite MUST hold no records of its own under `records/`.

**R-COMPOSE-004.** A composite MUST contain modules or composites only; a snapshot pack MUST NOT be a member,
and travels beside a composite in a pack set (Core §14.2).

## 3. members.json

`members.json` holds `members` only. Each member holds these fields only:

<!-- registry:members.fields -->
| Field |
|---|
| `id` |
| `version` |
| `priority` |
| `enabled` |
| `contains` |
<!-- /registry -->

**R-COMPOSE-005.** `members.json` MUST hold `members` only, an array whose entries hold only the fields above,
typed: `id` and `version` non-empty strings (both required), `priority` an integer within 32 bits, `enabled` a boolean,
`contains` a list of non-empty strings; anything else MUST refuse the pack.

**R-COMPOSE-006.** `members.json`, the `contains` of `pack.json` and `META/lock.json` MUST name the same
members.

**R-COMPOSE-007.** A host MUST refuse, at load, a priority outside 0 to 10,000, a version that is not a range,
a `contains` that names no member or names itself, and a cycle; such a pack verifies and is refused by every host.

**R-COMPOSE-008.** A host MAY leave a member out, move it, or switch on a member that is off by default; it
MUST NOT name a member the composite lacks.

## 4. META/lock.json

**R-COMPOSE-009.** `META/lock.json` MUST be `{"format": "agentmemory.pack.lock/1.1", "members": [{"id", "version",
"digest"}]}` and nothing else, at the top level and in each member: `id` an id, `version` a version, `digest` a pack
digest; it MUST be in RFC 8785 canonical form, listed in `META/files.json` and so signed.

**R-COMPOSE-010.** A lock MUST pin each id at most once.

**R-COMPOSE-011.** A composite's lock MUST pin exactly its direct members.

**R-COMPOSE-012.** A host MUST install exactly the pinned set: a missing, extra or swapped member MUST refuse the
family.

## 5. Nesting

**R-COMPOSE-013.** Containment MUST be a directed acyclic graph at most four composites deep; a fifth level MUST be
refused.

**R-COMPOSE-014.** A nested composite's digest covers its own lock, so the outermost signature pins the whole tree;
two locks that pin different content for one id MUST refuse the family.

**R-COMPOSE-015.** A composite that contains itself by id MUST be refused.

**R-COMPOSE-016.** Two composites MAY share a member only as the same content, and it MUST be composed once,
from the first composite that adds it.

**R-COMPOSE-017.** A nested member's priority MUST be its slot's plus its own (at most 10,000): never ahead of its
slot, in its own order within it.

**R-COMPOSE-018.** Switching a slot off MUST switch off everything under it; a member MUST be on only when some path
to it has every composite on and the member enabled.

**R-COMPOSE-019.** A member MUST have what it depends on switched on and contained.

## 6. Holding packs together

**R-COMPOSE-020.** Every door that holds packs together (a host, a server that mounts packs, pack tests, a gate
report) MUST hold them by one rule: one version of a pack, and one live snapshot of a module, a later one replacing the
earlier whole.

**R-COMPOSE-021.** The same pack reached twice (the same digest, in a set and on its own) MUST be one pack, whatever
the order in which it is reached.

**R-COMPOSE-022.** Two versions or contents of one id, or two snapshots of one module, MUST be refused with both
named; two different manifests under one module id MUST be refused, never one silently kept.

**R-COMPOSE-023.** Each pack MUST come from the route that registered it, and a family member MUST count through
the route a host composes it by.

## 7. Consent and grants

**R-COMPOSE-024.** A walk into another pack MUST need that pack's `containableBy` to name the container.

**R-COMPOSE-025.** A link written into a pack's records at ingestion MUST need the same consent: a snapshot that
links into a pack without its consent MUST be refused before anything is written, and the store MUST refuse such a link
on any write path.

**R-COMPOSE-026.** A snapshot whose links reach a dependency that holds nothing MUST be refused, as the
store refuses it.

**R-COMPOSE-027.** A host MAY grant a pair (a pack and the pack it may reach into) by name; the grant MUST apply to
walks and links alike, and MUST join the module set's fingerprint.

**R-COMPOSE-028.** A server that mounts packs MUST take the host's grants as the host does, for walks and for
snapshot packs.

> Why: one rule for walks and links, so a pack cannot reach into another by the route the rule forgot.
