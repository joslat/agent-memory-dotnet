# Host document: a pack's lifecycle

**Version 1, draft.** Requirement ids: `R-HOST-nnn`, numbered across the host documents. Key words as in Core §1.2.

What a host does with a pack's records and names when it loads the pack, removes it, loads it again, and loads a newer
version. The ledger ([ledger.md](ledger.md)) keeps which versions were installed and refuses a rollback; this document
keeps what happens in the store. Section 2 holds what the reference host does and tests; section 3 lists what is decided
for the kinds of pack this version does not yet cover, each to become a requirement here when it is built and tested.

## 1. Words

- **Load**: verify a pack (Core §9) and import it: its schema first, then its records, each a version keyed by type and key.
- **Remove** (unload): take a loaded pack out of the store.
- **Reload**: load a removed pack again, at the same version.
- **Upgrade**: load a newer version of a loaded pack. An older one is a downgrade, which the ledger refuses (R-HOST-033).

## 2. Requirements

**R-HOST-060.** A host MUST remove a pack by closing its records: each leaves every live view at the removal and keeps
its history. The pack's schema and its claim on its names MUST stay; a removal by the pack's id, when its archive is gone,
MUST close the same records.

**R-HOST-061.** A pack removed and loaded again at the same version MUST be imported again: every record it holds is
written again and is live.

**R-HOST-062.** A newer version of a pack MUST replace the older one record by record, by key: a changed record becomes
a new version and keeps the old one as history, an unchanged one MUST stay as it is, and a record the newer version no
longer holds MUST be closed.

**R-HOST-063.** A pack under a new id MUST be refused a name another id's pack claims, even after that pack was removed,
and the refusal MUST name the pack, the step and the name's owner. An operator MAY transfer a removed pack's names,
records and history to its new id; a transfer MUST change nothing unless the old id is removed and the new pack claims
every name the old one did, as it did.

**R-HOST-064.** Where a host keeps a local copy of a pack for each owner, removing the pack MUST detach the owner's copy,
inactive and protected, with the owner's own changes kept, and loading it again MUST make it live with every value the
owner had.

**R-HOST-065.** A host MUST name what an upgrade drops of what its store holds live of the pack: every type and relation
the pack's live records use that the newer version does not define, and every property they carry that the newer
version's types do not declare, before it imports, so that it can refuse the upgrade, and in the import's result. A live
record that carries a dropped property or link MUST be written again as a new version without it. A name the pack keeps
after its records closed is not a drop.

## 3. Decided, not yet required (informative)

Decided on 24 November 2026. Each becomes a requirement in section 2 when the reference host builds and tests it.

- **An upgrade whose ontology changed** (beyond R-HOST-065). A property whose type changed is named too. A declaration
  inside the pack of what it drops or renames, so that a drop is refused unless declared and a renamed type keeps its
  records' history, needs a later minor version of the format.
- **Sets.** A member a newer version of a set no longer carries is closed, unless another loaded set or family holds it.
  One member is switched off, never removed alone. A member two loaded sets carry is held by each and closed when the
  last one goes; two contents for one id are refused.
- **Minds.** Removing a mind switches it off at once; its own members follow the rule for sets. What a mind needs from
  knowledge is checked when it is switched on: every need the loaded knowledge does not meet is named, and the mind is
  not switched on in full while one is unmet (a character-only mode stays the user's explicit choice).
- **Hierarchies.** Any member of a family, at any depth, can be switched off by its path. Removing or upgrading the
  outermost family acts on every member it holds as one unit.
- **Local copies.** A copy loaded again takes the host's request; a protection the owner set before the removal is kept.
- **A pack that requires another** is resolved against the packs the host has loaded, and refused, naming the missing
  pack, otherwise.

Not decided in this version: several brains sharing one store without local copies, where removing a pack in one closes
its records for all.
