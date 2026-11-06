# Permissions profile

**Version 2, draft.** Conformance class: **Permissions Host**. Requirement ids: `R-PERM-nnn`. Key words as in Core §1.2.

## 1. Introduction

### 1.1 Scope

A usage policy, `policy.json`, states what a receiver may do with a pack: per audience (everyone, or WebIDs), the verbs it
allows and denies (`read`, `derive`, `export`, `redact`, `rehydrate:local`, `rehydrate:private`, `rehydrate:external`,
`train`), and when a rule's grants end. Its shape, its one rule (deny wins; an ended rule grants nothing and still refuses)
and the doors where a host asks it are Core §17c (R-CORE-138, R-CORE-139). This profile says what a host that implements it,
and one that does not, MUST do. Record labels (`public`, `internal`, `confidential`, `secret`) belong to a later version; Core
reserves the record member `labels` and refuses it until they are enforced.

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Permissions profile when it holds `policy.json`.

## 2. Requirements

**R-PERM-001.** A reader or host that does not implement this profile MUST refuse a pack that carries a usage policy; a
declared policy MUST NOT load as if it were absent.

**R-PERM-002.** A Permissions Host MUST meet R-CORE-138 and R-CORE-139: it reads the policy strictly, asks it at the time it
acts at every door it has, and offers no record whose policy it does not hold.
