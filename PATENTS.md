# Patent pledge for the ampack specification

Version 1, 2026-11-04. A unilateral statement by the patent applicant. It is not a contract and not legal advice. A later version can only widen it for implementations made while an earlier version was in force. The wording is to be checked by patent counsel before the first public release of this repository; until then it is a draft that binds nobody.

## Background

Jose Luis Latorre Millas (the "pledgor") filed Swiss patent application JLM-2026-01 (the applicant's reference; the office's number is pending) on 3 November 2026. Its claims are directed to a host procedure described in `spec/`: trial-gated activation of a memory module on the host's own history (with host floors and bounds, code-repository history, upgrade no-regression, cost targets and expiry), the declared walk under host budgets, and the admission of federated modules under a trust policy; and to the declarations a module carries for the walk and for federation. It does not claim the container, identity or signature format as such. The pledgor may file further applications claiming priority from it.

The reference implementations in this repository are licensed under the Apache License 2.0. Its section 3 grants every user of that code a patent licence, and this pledge does not restrict, condition or replace it. The pledge exists for implementations that do not use that code: a reader, writer or host written from the specification alone, in any language.

## The pledge

The pledgor will not assert any Covered Claim against anyone for making, having made, using, selling, offering for sale, importing, distributing or running a Conforming Implementation, commercially or not. No royalty, registration, notice or licence agreement is required.

**Covered Claims** are the claims of Swiss patent application JLM-2026-01, of any application claiming priority from it, and of any later application the pledgor lists in this file, that are necessarily infringed by implementing the normative requirements of a Covered Version. A claim that is infringed only by a feature the specification does not require, by an implementation's own additions, or by using the pledgor's products or services is not covered.

**Covered Version** is the specification as it appears in a release tag of this repository, or on its `main` branch at a commit the implementer names in its conformance statement, including version 1 while it is marked draft.

**Conforming Implementation** is an implementation that, for each conformance class it claims (the classes of `spec/core.md` sections 2 and 13, the profiles in `spec/profiles/`, and the host requirements in `spec/host/trust-policy.md`, `spec/host/ledger.md` and `spec/host/decision-record.md`), meets every MUST and MUST NOT requirement of that class listed in `spec/requirements.json` of the Covered Version, and claims no class it does not implement. The normative text decides. The conformance kit in `conformance/` is evidence of conformance, free to run; a defect in the kit does not take an implementation outside the pledge. Certification is separate and optional (below).

## What the pledge does not cover

- Claims that are not Covered Claims, and uses of the patented procedures outside a Conforming Implementation.
- The pledgor's names, logos, certification badge and certification service, which are governed by their own terms. The pledgor claims no right over the ordinary word "compatible".
- Access to or use of the pledgor's hosted services, proprietary software and curated or premium packs, which are governed by their own terms. An implementer may build the format into its own commercial products and services.
- Any patent of a third party.

## Termination

The pledge ends for a party on the day that party, or a company it controls, files or joins a lawsuit or other legal proceeding alleging that a Conforming Implementation, a Covered Version, or the Apache-2.0 reference implementations in this repository infringe a patent, except as a counterclaim in a proceeding that party did not start. Copies made before that day stay covered for their continued use. The patent licence of Apache-2.0 has its own termination rule (section 3) and is unaffected by this pledge.

## Duration and transfer

The pledge lasts for the life of the Covered Claims. The pledgor intends any transfer of those patents to be made subject to this pledge and will require the transferee to honour it for Conforming Implementations.

## How this fits the licences in this repository

| What | Licence | Patent position |
|---|---|---|
| The module SDK and its store and host adapters, the format libraries, the ampack tool, the Rust reader, the conformance kit and Python verifier (the Apache-2.0 list in the README, "Licensing") | Apache-2.0 | Apache-2.0 section 3 licence for users of the code; this pledge in addition |
| The specification in `spec/` | CC BY 4.0 | CC BY 4.0 licenses no patent rights (its section 2(b)(2)); this pledge |
| The rest of the library (core, store, the other adapters, samples, benchmarks) | MIT | Outside the scope of this pledge |

## In one paragraph

Anyone may implement a Covered Version of the ampack specification, commercially or not, without patent royalties under the Covered Claims. Certification, trademarks, proprietary products and hosted services remain separately governed. This pledge does not restrict rights granted under Apache-2.0.

Questions about the pledge: open an issue in this repository.
