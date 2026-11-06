# Proposed patent non-assertion pledge for the ampack specification

**Status: draft, not issued.** This is the canonical draft of a unilateral statement the patent applicant intends to issue. It binds nobody and grants nothing until the applicant issues it in this repository without this warning, after a check by patent counsel. Until then, read every "will" below as "intends to". The patent licence that the Apache License 2.0 (section 3) grants to users of the Apache-2.0 code in this repository is separate, already in force, and not affected by the status of this draft. Not legal advice.

Version 1 draft, 5 November 2026. A later version can only widen the pledge for implementations made while an earlier issued version was in force.

## Background

Jose Luis Latorre Millas (the "pledgor") filed Swiss patent application JLM-2026-01 (the applicant's reference; the office's application number is pending) on 3 November 2026. It is an application, not a granted patent: whether any claim is granted, in what form, with what validity and in which territories is not decided. Its claims as filed are directed to a host procedure described in `spec/`: trial-gated activation of a memory module on the host's own history (with host floors and bounds, code-repository history, upgrade no-regression, cost targets and expiry), the declared walk under host budgets, and the admission of federated modules under a trust policy; and to the declarations a module carries for the walk and for federation. They do not claim the container, identity or signature format as such. The pledgor may file further applications claiming priority from it.

The reference implementations in this repository are licensed under the Apache License 2.0. Its section 3 grants every user of that code a patent licence under the pledgor's claims that the code necessarily infringes; this pledge does not restrict, condition or replace it. The pledge exists for implementations that do not use that code: a reader, writer or host written from the specification alone, in any language.

## The pledge (as proposed)

Once issued: the pledgor will not assert any Covered Claim against anyone for making, having made, using, selling, offering for sale, importing, distributing or running a Conforming Implementation, commercially or not. No royalty, registration, notice or licence agreement is required.

**Covered Claims** are the claims of Swiss patent application JLM-2026-01, of any application claiming priority from it, and of any later application the pledgor lists in this file, that are necessarily infringed by implementing the normative requirements of a Covered Version. A claim that is infringed only by a feature the specification does not require, by an implementation's own additions, or by using the pledgor's products or services is not covered.

**Covered Version** is a version of the specification listed in the table under "Covered versions" below, identified by an immutable tag or commit. No other version, and no later commit of `main`, is covered by being newer.

**Conforming Implementation** is an implementation that, for each conformance class it claims (the classes of `spec/core.md` sections 2 and 13, the profiles in `spec/profiles/`, and the host requirements in `spec/host/trust-policy.md`, `spec/host/ledger.md` and `spec/host/decision-record.md`), meets every MUST and MUST NOT requirement of that class listed in `spec/requirements.json` of the Covered Version, and claims no class it does not implement. The normative text decides. The conformance kit in `conformance/` is evidence of conformance, free to run; a defect in the kit does not take an implementation outside the pledge. Certification is separate and optional (below).

## Covered versions

| Specification version | Immutable identifier | Conformance classes and profiles covered | Status |
|---|---|---|---|
| ampack specification, version 1 (the `spec/` folder; marked draft) | [pending: the repository release tag that first ships it, expected `v2.0.0`, and the commit it points to; to be entered when the pledge is issued, never guessed] | Core Reader and Core Writer (core.md section 2); the profiles the registry marks implemented: `walk`, `trial`, `compose`, `federation`, `permissions`, `embeddings` (core.md section 13); the host documents `spec/host/trust-policy.md`, `ledger.md`, `decision-record.md` | pending issue |

Profiles the registry marks not implemented (`protection`, `code`) are not covered by this row. How a later version becomes covered: by a new version of this file that adds a row for it, with its own immutable identifier and its own list of classes and profiles. A new version of this file never removes a row that an issued version carried, so an implementation made while a row was in force stays covered.

Difference from the previous wording of this draft (4 November 2026): that wording covered any release tag or any named `main` commit, so every future version would have been covered automatically; this table covers only what it lists.

## What the pledge does not cover

- Claims that are not Covered Claims, and uses outside a Conforming Implementation. Whether such a use infringes is a question of the claims as granted, their validity and the territory; not conforming to the specification does not by itself establish infringement, and a user of the Apache-2.0 code keeps the rights that licence grants.
- The pledgor's names, logos, certification badge and certification service, which are governed by their own terms. The pledgor claims no right over the ordinary word "compatible".
- Access to or use of the pledgor's hosted services, proprietary software and curated or premium packs, which are governed by their own terms. An implementer may build the format into its own commercial products and services.
- Any patent of a third party.

## Termination

The pledge ends for a party on the day that party, or a company it controls, files or joins a lawsuit or other legal proceeding alleging that a Conforming Implementation, a Covered Version, or the Apache-2.0 reference implementations in this repository infringe a patent, except as a counterclaim in a proceeding that party did not start. Copies made before that day stay covered for their continued use. The patent licence of Apache-2.0 has its own termination rule (section 3): it ends for a party that institutes patent litigation, including a cross-claim or counterclaim, alleging that the Work infringes; that rule is unaffected by this pledge and is not widened by it.

## Duration and transfer

The pledge lasts for the life of the Covered Claims. The pledgor intends any transfer of those patents to be made subject to this pledge and will require the transferee to honour it for Conforming Implementations.

## How this fits the licences in this repository

The complete licence map of the repository is `LICENSING.md` at the root.

| What | Licence | Patent position |
|---|---|---|
| The module SDK and its store and host adapters, the format libraries, the ampack tool, the Rust reader, the conformance kit and Python verifier (the Apache-2.0 list in the README, "Licensing") | Apache-2.0 | Apache-2.0 section 3 licence for users of the code; this pledge in addition |
| The specification in `spec/` | CC BY 4.0 | CC BY 4.0 licenses no patent rights (its section 2(b)(2)); this pledge |
| The rest of the library (core, store, the other adapters, samples, benchmarks) | MIT | Not covered by this pledge. MIT has no express patent-grant clause; whether any claim reads on a component is a question of the claims, not of the copyright licence |

## In one paragraph

Once issued: anyone may implement a Covered Version of the ampack specification, commercially or not, without patent royalties under the Covered Claims. Certification, trademarks, proprietary products and hosted services remain separately governed. This pledge does not restrict rights granted under Apache-2.0.

Questions about the pledge: open an issue in this repository.
