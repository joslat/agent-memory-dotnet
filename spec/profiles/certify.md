# Certify profile

**Version 1, draft.** Conformance classes: **Evaluator** (issues certificates) and **Certify Host** (relies on them).
Requirement ids: `R-CERTIFY-nnn`. Key words as in Core §1.2. Predicate type: `urn:agentmemory:pack:certificate:v1`.

## 1. Introduction

### 1.1 Scope

An evaluation certificate records that an evaluator ran a pack's declared trial and what it found, with the evidence. It
travels beside the pack in the Core attestation slot (Core §17), outside the files list, so it can be added after release
without changing the pack's digest. A host that requires trials can, in namespaces it names, rely on a trusted
certificate instead of running the trial itself.

### 1.2 When a pack uses it

The registry's profile map (Core §13) has no entry for this profile: certificates lie outside the files list, so they do
not decide which profiles a pack uses. A host that does not implement this profile treats the attestation slot as Core
requires (inert, bounded), and proven-here, by its own trials, stays its only basis for activation.

### 1.3 Conformance

**R-CERTIFY-001.** An Evaluator MUST implement Ampack Core and the Trial profile, and the requirements of
this document addressed to an evaluator.

**R-CERTIFY-002.** A Certify Host MUST implement Ampack Core, the Trial profile, and the requirements of this
document addressed to a host or a verifier.

## 2. The certificate

**R-CERTIFY-003.** A certificate MUST be an envelope in the shape of Core §8.1 with 1 to 16 signatures, whose
statement has the predicate type `urn:agentmemory:pack:certificate:v1` and exactly one subject: `META/files.json` and its
SHA-256, so that it is bound to exactly one pack.

**R-CERTIFY-004.** A certificate MUST be signed by a key the host's trust policy names as `evaluator` for the
pack's id, under the counting rule of Core §9.2 with the evaluation time as the signing time; an evaluator key holds that
role alone, so the key that signs a pack never certifies it.

### 2.1 The predicate

The predicate is read strictly and holds integers only.

| Field | Meaning |
|---|---|
| `id`, `version` | The pack's |
| `evaluator`, `tool {name, version}` | Who evaluated, with what (one line each) |
| `evaluatedAt` | UTC, seconds |
| `trial {measure, baseline, confidenceMilli?, taskSource}` | The declared trial it ran, and where its tasks came from (the evaluator's own) |
| `checks[]` | `{target, requiredMilli, observedMilli, met, estimateMilli?}` |
| `statistic` | The statistic its checks were computed with (one line) |
| `with`, `without?` | `{tasks, hits, offered, correct, p95Micros, maximumOffered, spacing, perTask}` |
| `passed` | Whether every check was met on at least 10 tasks |
| `aef? {runId, runHash}` | The AEF run it summarises: AEF 1.0's run hash |

Measures, baselines and confidences (in thousandths) a certificate may name:

<!-- registry:certificate.measures -->
| Measure |
|---|
| `test-impact` |
| `retrieval` |
<!-- /registry -->

<!-- registry:certificate.baselines -->
| Baseline |
|---|
| `host` |
| `none` |
<!-- /registry -->

<!-- registry:certificate.confidences -->
| Confidence (thousandths) |
|---|
| 900 |
| 950 |
| 975 |
| 990 |
<!-- /registry -->

**R-CERTIFY-005.** The predicate MUST hold no field other than those above, and every number in it MUST be an
integer.

**R-CERTIFY-006.** `id` and `version` MUST be this pack's, and the subject's digest this pack's digest.

**R-CERTIFY-007.** `evaluator` MUST be one line of 1 to 200 characters; `tool.name` one line of 1 to 100 and
`tool.version` one line of 1 to 64; `trial.taskSource` one line of 1 to 500; `statistic` one line of 1 to 100. A line
holds no line feed, no tab and no other control character.

**R-CERTIFY-008.** `evaluatedAt` MUST be a UTC time written `YYYY-MM-DDTHH:MM:SSZ`, within the key's window, and not
more than 5 minutes after the time of verification (the one clock allowance of R-CORE-061).

**R-CERTIFY-009.** `trial.measure` and `trial.baseline` MUST be among the values above, and `confidenceMilli`, when
present, among the confidences above.

**R-CERTIFY-010.** `checks` MUST hold 1 to 16 checks, each naming a known target of the Trial profile once, with
`requiredMilli` and `observedMilli` integers, `met` a boolean, and `estimateMilli` an integer when present; a
no-regression comparison MUST NOT appear as a check, since it is a host's own.

**R-CERTIFY-011.** An arm MUST hold exactly its fields: at most 200 tasks; `maximumOffered` 0 (nothing offered by design)
to 16; `spacing` 1 to 2,000; and `perTask`, one `[offered, correct]` per task with `correct` ≤ `offered` ≤ the cap,
summing to `offered` and `correct`, with a right item on exactly `hits` tasks.

**R-CERTIFY-012.** `without` MUST run the same number of tasks as `with`, at the same spacing; the tasks MUST span
fewer than the 2,000 eligible commits a trial draws from (spacing × (tasks − 1) < 2,000).

**R-CERTIFY-013.** `without` MUST be present for a `host` baseline, with a cap of at least 1; for a `none`
baseline, a `without` arm, when present, MUST offer nothing and have a cap of 0.

**R-CERTIFY-014.** `passed` MUST be true exactly when every check was met and `with` ran at least 10 tasks: an honest
failure is a valid certificate.

**R-CERTIFY-015.** `aef`, when present, MUST hold exactly `runId` (1 to 128 characters of `A`–`Z`, `a`–`z`, `0`–`9`,
`.`, `_`, `:`, `-`) and `runHash` (64 lowercase hexadecimal digits).

> Why: the evidence travels with the verdict, so every host can derive the verdict again under its own statistic.

## 3. Where it travels

**R-CERTIFY-016.** A carried certificate MUST travel as `META/attestations/<stem>.certificate.dsse.json`, where the stem is
the first 16 hexadecimal digits of its evaluator key's id, `-`, and the evaluation time in UTC written
`yyyymmddthhmmssz`; a certificate under another stem MUST be refused.

**R-CERTIFY-017.** A certificate MUST be known by what its evaluator signed, the SHA-256 of its payload's
pre-authentication encoding, never by its envelope's bytes; respellings of one envelope are one certificate.

**R-CERTIFY-018.** An untrusted or malformed certificate MUST NOT refuse the pack: what it claims MAY be shown, read
strictly, and it never stops a load.

**R-CERTIFY-019.** A certificate MAY also travel as a separate file beside the pack, and MUST then verify by the same
rules.

**R-CERTIFY-020.** A certified set MUST verify whole, member by member, before its certificate is read.

## 4. The AEF run

**R-CERTIFY-021.** An AEF run MAY travel beside its certificate under `META/attestations/<stem>.aef/`; a run without
its certificate MUST refuse the pack.

**R-CERTIFY-022.** A run that travels with a certificate MUST hash, by AEF 1.0's run hash (SEAL-1 to SEAL-4), to the
`runHash` the certificate names; a certificate that names no run MUST NOT travel with one.

**R-CERTIFY-023.** A run's `run.json`, read in the JSON profile, MUST name the same run id and, as its subject,
this pack: `ref` is `pack:<id>@<digest>` and `version` is the pack's version. A run of another version or content is the
evidence of another decision and MUST be refused, at issue and by every verifier.

**R-CERTIFY-024.** A run file named as a certificate's evidence that the pack does not carry MUST refuse the
certificate.

## 5. Issuing

**R-CERTIFY-025.** An Evaluator MUST issue a certificate only from its own signed decision for exactly that pack, whose
checks are the declared targets, with the checks derived again under its statistic and both arms' per-task evidence.

**R-CERTIFY-026.** An Evaluator MUST certify only a decision whose tasks were drawn in `parent` mode; it MUST
refuse `head` and a decision that does not say.

**R-CERTIFY-027.** A writer MUST refuse to sign a certificate whose verdict or text breaks this document.

## 6. Certificate trust

**R-CERTIFY-028.** A host MUST rely only on its own trials unless it names namespaces in which it trusts certificates
(by whole segments); certificate trust MUST need the host's decisions and a namespace.

**R-CERTIFY-029.** In such a namespace, a host that requires trials MAY record a decision from the latest trusted,
passing certificate that covers exactly the trial the pack declares: the same measure, baseline and confidence; every
declared target checked at the declared value and met; at least 10 tasks; and the host's own statistic, on the evidence
the certificate carries, agreeing that every target is met. It MUST sign that decision with its own key and name the
certificate it relied on (`basis`: certificate digest, evaluator and key, time, task source, AEF run).

**R-CERTIFY-030.** Admission MUST hold a certificate's arms to the caps the pack declares.

**R-CERTIFY-031.** One rule MUST decide which of a host's decisions about exactly one content counts, for the
trial gate and the upgrade gate alike: the host's own latest decision whenever it has one, pass or fail; otherwise, only
in a namespace it trusts certificates for, its latest decision made from a certificate. A host MUST refuse to admit from a
certificate where it has its own trial.

**R-CERTIFY-032.** A decision made from a certificate MUST count only for a pack composed on its own (not a
family member, not compiled code).

**R-CERTIFY-033.** A decision made from a certificate MUST count only while the certificate it names is the latest
trusted one the pack carries: when its evaluator is revoked, expired, re-scoped or removed, the certificate stripped, or a
newer trusted one is beside it, it MUST no longer count.

**R-CERTIFY-034.** Admission MUST follow the latest trusted certificate: a newer failure MUST NOT be passed over for an
older pass; when two trusted certificates share the latest evaluation second and one did not pass, admission MUST follow
that one.

**R-CERTIFY-035.** A broken copy of a trusted certificate MUST NOT hide it, whatever its name.

**R-CERTIFY-036.** An upgrade that must not regress MUST still need the host's own trial: a certificate MUST NOT skip
the upgrade gate.

**R-CERTIFY-037.** An evaluator key the host revoked before MUST NOT be trusted, even when the current run names
no revocation.

**R-CERTIFY-038.** A certificate moved onto other content MUST NOT be trusted there.

> Why: proven here stays the default. Attestations are unsigned beside the pack, so a distributor can strip a
> certificate (a newer failing one included) before a host admits; a host that wants every certificate gets them from the
> evaluator. An evaluator key's expiry is checked when the host loads the pack, not while it runs.
