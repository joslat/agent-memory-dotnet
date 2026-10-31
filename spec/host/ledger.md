# Host document: the ledger

**Version 1, draft.** Format string: `agentmemory.pack.ledger/1.1`. Requirement ids: `R-HOST-nnn`, numbered across the
three host documents. Key words as in Core §1.2.

The ledger is a host's memory of what it installed and revoked. It implements Core §9.5. It survives uninstall, so a pack
removed and offered again in an older version is still known.

## 1. Requirements

**R-HOST-031.** A host MUST read its ledger bounded to 64 MiB, as Core §18 reads every file.

**R-HOST-032.** The ledger MUST record, for each id, the highest version ever installed, and for each (id,
version), its digest; it MUST keep them across a save and a load.

**R-HOST-033.** A host MUST refuse a downgrade, even after the pack was removed, unless the operator says so.

**R-HOST-034.** A host MUST refuse a known version with other content; versions that differ only in build
metadata are the same version.

**R-HOST-035.** The ledger MUST hold a pack set's members as it holds packs: a set rolled back to an older version
MUST be refused; where the operator allows a set downgrade, the ledger MUST still hold one content per version, and MAY
install a member older than the host had.

**R-HOST-036.** The ledger MUST keep what a live revoker's lists revoked, so that what a host revoked stays
revoked whatever list a later run is given; a revocation MUST outlive its revoker.

**R-HOST-037.** The ledger MUST keep the revocation freshness it applied, and a host with a ledger MUST stay
only as fresh as the newest list it applied; an operator MAY retire the kept revocations' freshness explicitly.

**R-HOST-038.** Co-signed and older lists MUST NOT lock a host out.

**R-HOST-039.** Two runs that save the ledger at once MUST lose nothing.

**R-HOST-040.** The ledger MUST be checked last in verification (Core §10).
