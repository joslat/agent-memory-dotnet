# Spike 0 — recall parity over a draft wire

> **PROTOTYPE. Throwaway by design.** This is not an SDK, not a server, and not a preview of one. The
> productized cross-language SDK follows the published designs; nothing here is published, packaged, or
> announced. `crosslang-architecture.md` §5 Step 0, executed under the private-demo carve-out
> (`meeting-demo-track.md` D1).

## The question, and why it is worth days rather than weeks

> Given only the wire JSON, can a non-.NET client reconstruct the same answer the .NET caller got?

If the answer is no, that finding — written up — **ends the spike cheaply**, and no contract package
gets built on a wire that cannot carry the result. Answering it needs a prototype host and a throwaway
script, not an SDK.

The real `am-wire/1` contract deliberately waits: it needs response shapes Wave C and 31.1 are still
landing (projection blocks, delta, certificates), and building it first means shipping the wire twice.

## How the comparison works

The naive design — call the wire, call the engine, byte-compare — cannot work: the two produce
different shapes, so the compare would only ever report "different types". Instead:

| Endpoint | Returns | Built by |
|---|---|---|
| `POST /v1/recall` | the draft `am-wire/1` response | the host, from the domain result |
| `POST /v1/spike/recall-direct` | the **canonical projection** | the host, from the same domain result |

and `recall_parity.py` builds *the same canonical projection* **in Python, from the wire JSON alone**,
then byte-compares.

That asymmetry is the point. Every field the script reads is a field the DTO had to carry; a field the
DTO drops is one Python cannot reconstruct, and the compare fails. This asks whether the wire is
*sufficient*, which is the question — not whether two serializers agree.

## The five fixtures

| Fixture | What it proves |
|---|---|
| `plain-recall` | the ordinary case: scoped recall, no temporal or ownership subtlety |
| `isolation` **(gate)** | Bob's fact is absent from Alice's recall — and `ownerId` is on the wire, so a client can *verify* that rather than trust it |
| `as-of-before-job-change` **(gate)** | point-in-time recall at March: `works_at Acme` |
| `as-of-after-job-change` | the same query at September: `works_at Initech` — a different answer, or `as_of` is decorative |
| `supersession` | a superseded fact is closed; live recall returns the winner without the loser |

The isolation fixture deliberately asks *"who does bob work for?"* — the query most likely to pull
Bob's fact in. Getting Alice's facts back, and only Alice's, is the strongest form of that test.

## Running it

```bash
# a throwaway Neo4j
docker run -d --name spike0-neo4j -p 7688:7687 -e NEO4J_AUTH=neo4j/spikepassword neo4j:5.26

# the prototype host (bootstraps schema at startup, then serves)
NEO4J_URI=bolt://localhost:7688 NEO4J_USERNAME=neo4j NEO4J_PASSWORD=spikepassword \
ASPNETCORE_URLS=http://localhost:5173 \
dotnet run --project crosslang/spike0/Spike0.Host -c Release

# the parity script — stdlib only, no install step
python crosslang/spike0/recall_parity.py --base-url http://localhost:5173
```

## Result (2026-08-16)

```
seeded 7 fixture facts
PASS  plain-recall  (5 facts)
PASS  isolation  (5 facts)
PASS  as-of-before-job-change  (5 facts)
PASS  as-of-after-job-change  (4 facts)
PASS  supersession  (5 facts)

parity on all 5 fixtures, including the isolation and as-of cases.
```

Spot-checked for meaning, not just agreement — the two paths agreeing on the wrong answer would pass a
parity test and teach nothing:

- **isolation** — Alice's recall returns five facts, all `ownerId: alice`. Bob's `works_at Globex` is
  absent despite the query naming him.
- **as-of March** — `works_at Acme`, and `lives_in Zurich` is *present*: the supersession happened in
  August, so at March it had not occurred. Correct on both clocks.
- **as-of September** — `works_at Initech`, and Zurich is gone. The valid-time clock moved the employer
  answer; the transaction clock removed the superseded city.

**Gate met.** The wire can express the .NET recall result, including owner isolation and bitemporal
as-of, and a stdlib Python client reconstructs it byte-for-byte.

## Two findings about the harness, recorded because they nearly weren't

**1. The first run passed all five fixtures while comparing nothing.** Every fixture returned zero
items, and two empty results are byte-identical. The script reported parity.

The cause was `StubEmbeddingGenerator`: its vectors are deterministic but semantically meaningless, so
nothing cleared the shipped `MinSimilarityScore` of 0.7. The spike now recalls at floor 0 — Spike 0 asks
whether the wire carries a result, not whether retrieval ranks well, and retrieval quality is measured
elsewhere with a real provider.

The script now carries a **void witness**: a fixture whose comparison has nothing in it is reported
`VOID` and fails the run. *A gate that passes on empty results is not a gate.* There is a second
witness across the as-of pair — if both instants give the same answer, the clock had no effect and
those two fixtures tested nothing, however cleanly they byte-matched.

**2. A transport error is not a finding.** The first failing run printed "the wire cannot express the
.NET result" when the actual cause was an unbootstrapped database returning HTTP 500. Instrument
failure and result are now counted and reported separately, because they lead to opposite conclusions:
one says fix the harness and re-run, the other says stop and write it up.

## Rules this respects

- **Zero diff to `src/`** — checked at every commit (`git diff --stat -- src/` empty). The host
  references the shipped packages and changes none of them.
- **Not in `AgentMemory.slnx`** — a prototype must never gate the repository's build or CI. Build by
  path.
- **Named `Spike0.Host`, not `AgentMemory.Spike0.Host`** — the root `Directory.Build.props` attaches
  multi-targeting and NuGet packaging metadata to every `AgentMemory*` project. The name keeps a
  throwaway out of the packaging story without editing shared build configuration.
- **No publish, no README claim, no announcement.** `/v1/meta` says `PROTOTYPE` on its face, so anyone
  who finds the port knows what they found.

## Deviation from the design, stated

`crosslang-architecture.md` §5 specifies a dedicated worktree on branch `spike/crosslang-server`. This
was built in place on the working branch instead, under `crosslang/` with the zero-`src/`-diff
invariant checked at commit — which is the checkable property `meeting-demo-track.md`'s binding Rules
section actually names. Everything lives under `crosslang/`, so moving it to a worktree or branch later
is a directory move and nothing else.
