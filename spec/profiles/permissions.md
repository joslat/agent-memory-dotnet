# Permissions profile

**Version 1, draft.** Conformance class: **Permissions Host**. Requirement ids: `R-PERM-nnn`. Key words as in Core §1.2.

**This draft defines the declaration only.** A conforming host refuses every pack that uses this profile until a later
version of this document defines enforcement.

## 1. Introduction

### 1.1 Scope

A usage policy, `policy.json`, states how a pack's content may be used: its purpose, the regions it may be used in, the
model endpoints allowed, no export to external models, and retention. In the design it is enforced through the host's
agent hooks and access control, with evidence of each decision. Record labels (`public`, `internal`, `confidential`,
`secret`), enforced against the caller's attributes on top of owner and session isolation, belong to the same design;
Core reserves the record member `labels` and refuses it until they are enforced.

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Permissions profile when it holds `policy.json`.

## 2. Requirements

**R-PERM-001.** A host that cannot enforce a usage policy MUST refuse a pack that carries one, with the refusal words
the registry gives this profile; a declared policy MUST NOT load as if it were absent.
