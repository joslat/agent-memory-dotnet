# Host document: the decision record

**Version 1, draft.** Predicate type: `urn:agentmemory:pack:trial:v1`. Requirement ids: `R-HOST-nnn`, numbered across the
three host documents. Key words as in Core §1.2.

A decision record is what a Trial Host (Trial profile) signs when it decides on a pack's trial, or when it admits a pack
on a certificate (Certify profile). The host keeps its records as activation records.

## 1. The record

**R-HOST-041.** A decision MUST be an envelope in the shape of Core §8.1, of an in-toto statement with the
predicate type `urn:agentmemory:pack:trial:v1`, signed by the host's own key.

**R-HOST-042.** The record MUST name the pack it trialled by its digest (`packDigest`, `sha256:` over its files
list), which is also the statement's subject.

**R-HOST-043.** The record MUST name the declarations by `manifestDigest`, written `jcs-sha256:` over their RFC
8785 canonical JSON, named apart from a pack digest.

**R-HOST-044.** The record MUST carry the declared `confidence`, `history` (`parent` or `head`), the `statistic` its
checks were computed with, and each arm's evidence: its counts, `maximumOffered`, `spacing` and `perTask`.

**R-HOST-045.** For an upgrade, the record MUST carry the comparison with the version it was measured against:
that version's id, version and both digests.

**R-HOST-046.** A decision made from a certificate MUST name the certificate it relied on as `basis`: the
certificate digest, the evaluator and its key, the evaluation time, the task source and the AEF run.

## 2. Keeping records

**R-HOST-047.** A host MUST read an activation record bounded to 1 MiB; a record over its bound is not a record.

**R-HOST-048.** A decision MUST be recorded once, and two decisions made in one second MUST NOT share a file.

**R-HOST-049.** The declarations' digest MUST be stable: the same declarations MUST give the same
`manifestDigest` on every host.

> Why: the host's records are host-owned state. An activation record deleted from the host's folder can re-enable an
> older passing decision; protecting that folder is the host's own concern.
