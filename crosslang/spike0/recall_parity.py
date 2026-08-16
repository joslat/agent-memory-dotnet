#!/usr/bin/env python3
"""Spike 0 — the stdlib-Python recall parity script (crosslang-architecture.md §5 Step 0).

THROWAWAY BY DESIGN. This is not an SDK and must never become one: it exists to answer one question
cheaply, before any contract package is written.

    Given only the wire JSON, can a non-.NET client reconstruct the same answer the .NET caller got?

The method is deliberately asymmetric. The host serves two endpoints:

  * ``POST /v1/recall``              — the draft am-wire/1 response.
  * ``POST /v1/spike/recall-direct`` — the CANONICAL projection, built in C# from the domain object.

This script builds the *same* canonical projection **in Python, from the wire JSON alone**, and
byte-compares it against the C# one. A field the DTO fails to carry is a field this script cannot
reconstruct, so the compare fails — which is precisely the failure worth finding now rather than after
a contract exists. Comparing the two responses directly would only ever report "different shapes".

Gate (from the architecture doc): parity on 5 varied fixtures **including an isolation case and an
as-of case**. Failure is not a bug to be patched around — it means the wire cannot express the .NET
result, and that finding, written up, ends the spike cheaply.

Standard library only (``urllib`` + ``json``): a spike that needs a dependency install before it can
tell you whether to proceed has already cost more than it was meant to.

Usage:
    python recall_parity.py [--base-url http://localhost:5170]
Exit code 0 on parity, 1 on any mismatch or transport failure.
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request

DEFAULT_BASE_URL = "http://localhost:5170"

# Anchored to the host's Fixtures.Epoch. Fixed constants, never "now": an as-of fixture pinned to
# wall-clock time asks a different question on every run, and a script that passes for that reason has
# measured nothing.
EPOCH = "2026-01-01T00:00:00Z"
BEFORE_JOB_CHANGE = "2026-03-01T00:00:00Z"
AFTER_JOB_CHANGE = "2026-09-01T00:00:00Z"


def post(base_url: str, path: str, payload: dict) -> dict:
    body = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        base_url.rstrip("/") + path,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def stamp(value: str | None) -> str:
    """Normalise an ISO-8601 instant to the host's canonical spelling, or '-' when absent.

    The two sides must agree on *format* before they can be compared on *content*. .NET emits
    offsets and fractional seconds that Python's raw string would not match, so both sides reduce to
    second precision in UTC. This is the one place where the comparison is allowed to be lenient, and
    it is lenient about spelling only — never about presence.
    """
    if value is None:
        return "-"
    text = value.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    from datetime import datetime, timezone

    parsed = datetime.fromisoformat(text).astimezone(timezone.utc)
    return parsed.strftime("%Y-%m-%dT%H:%M:%SZ")


def confidence(value: float) -> str:
    """Match the host's "0.####" formatting without importing anything to do it."""
    formatted = f"{value:.4f}".rstrip("0").rstrip(".")
    return formatted if formatted else "0"


def canonicalize(wire: dict) -> dict:
    """Build the canonical projection FROM THE WIRE JSON ALONE.

    Every field read here is a field the DTO had to carry. That is the whole point: this function is
    the client, and what it cannot see does not exist on the wire.
    """
    facts = sorted(
        "|".join(
            [
                f["id"],
                f["subject"],
                f["predicate"],
                f["object"],
                confidence(f["confidence"]),
                stamp(f.get("validFrom")),
                stamp(f.get("validUntil")),
                f.get("ownerId") or "-",
            ]
        )
        for f in wire["facts"]
    )
    entities = sorted(
        "|".join([e["id"], e["name"], e["type"], e.get("ownerId") or "-"])
        for e in wire["entities"]
    )
    preferences = sorted(
        "|".join([p["id"], p["category"], p["text"], p.get("ownerId") or "-"])
        for p in wire["preferences"]
    )
    return {
        "totalItemsRetrieved": wire["totalItemsRetrieved"],
        "truncated": wire["truncated"],
        "facts": facts,
        "entities": entities,
        "preferences": preferences,
    }


def as_bytes(projection: dict) -> bytes:
    """One canonical serialization, used for both sides, so the compare is genuinely byte-level."""
    return json.dumps(projection, sort_keys=True, separators=(",", ":")).encode("utf-8")


# The five fixtures. Two are named by the gate; the other three cover the ordinary shapes, so a wire
# that happens to work only on the interesting cases is still caught.
FIXTURES: list[tuple[str, dict, str]] = [
    (
        "plain-recall",
        {"sessionId": "spike", "userId": "alice", "query": "what do you know about alice?"},
        "the ordinary case: a scoped recall with no temporal or ownership subtlety",
    ),
    (
        "isolation",
        {"sessionId": "spike", "userId": "alice", "query": "who does bob work for?"},
        "GATE CASE: bob's fact must not appear in alice's recall, and the wire must carry ownerId "
        "so a client can verify that rather than take it on trust",
    ),
    (
        "as-of-before-job-change",
        {
            "sessionId": "spike",
            "userId": "alice",
            "query": "where does alice work?",
            "asOf": BEFORE_JOB_CHANGE,
        },
        "GATE CASE: point-in-time recall before the employer change — Acme, not Initech",
    ),
    (
        "as-of-after-job-change",
        {
            "sessionId": "spike",
            "userId": "alice",
            "query": "where does alice work?",
            "asOf": AFTER_JOB_CHANGE,
        },
        "the same query at a later instant must give a different answer, or as-of is decorative",
    ),
    (
        "supersession",
        {"sessionId": "spike", "userId": "alice", "query": "where does alice live?"},
        "a superseded fact is closed, and live recall must return the winner without the loser",
    ),
]


def main() -> int:
    parser = argparse.ArgumentParser(description="Spike 0 recall parity")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument(
        "--no-seed",
        action="store_true",
        help="skip seeding (the fixtures must already be present)",
    )
    args = parser.parse_args()

    try:
        if not args.no_seed:
            seeded = post(args.base_url, "/v1/spike/seed", {})
            print(f"seeded {seeded.get('seeded')} fixture facts")
    except urllib.error.URLError as error:
        print(f"FAIL  cannot reach the prototype host at {args.base_url}: {error}", file=sys.stderr)
        return 1

    mismatches = 0
    voids = 0
    broken = 0
    observed: dict[str, list[str]] = {}

    for name, request, why in FIXTURES:
        try:
            wire = post(args.base_url, "/v1/recall", request)
            direct = post(args.base_url, "/v1/spike/recall-direct", request)
        except urllib.error.URLError as error:
            # A transport failure is INSTRUMENT failure, not a parity result. Counted separately,
            # because "the host was down" and "the wire cannot express the .NET result" are opposite
            # conclusions and lumping them together would let a broken run masquerade as a finding.
            print(f"BROKEN {name}: transport error: {error}", file=sys.stderr)
            broken += 1
            continue

        reconstructed = as_bytes(canonicalize(wire))
        authoritative = as_bytes(direct)
        projection = canonicalize(wire)
        observed[name] = projection["facts"]

        if reconstructed != authoritative:
            mismatches += 1
            print(f"FAIL  {name}", file=sys.stderr)
            print(f"      why this fixture exists: {why}", file=sys.stderr)
            print(f"      from wire:   {reconstructed.decode('utf-8')}", file=sys.stderr)
            print(f"      .NET direct: {authoritative.decode('utf-8')}", file=sys.stderr)
            continue

        # VOID WITNESS. Two empty results are byte-identical, so a fixture that retrieves nothing
        # "passes" while comparing nothing at all. The first run of this spike did exactly that on all
        # five, and reported parity. An empty fixture is uninterpretable, not a pass.
        total = len(projection["facts"]) + len(projection["entities"]) + len(projection["preferences"])
        if total == 0:
            voids += 1
            print(f"VOID  {name}: both sides empty — nothing was compared", file=sys.stderr)
            print(f"      why this fixture exists: {why}", file=sys.stderr)
            continue

        print(f"PASS  {name}  ({len(projection['facts'])} facts)")

    # The as-of pair has to DISAGREE with each other, or point-in-time recall is decorative: two
    # identical answers at two instants would satisfy every byte-compare above and still mean the
    # clock was ignored. This is the one cross-fixture check, and it is the demo's whole beat.
    before = observed.get("as-of-before-job-change")
    after = observed.get("as-of-after-job-change")
    if before is not None and after is not None and before == after:
        voids += 1
        print(
            "VOID  as-of pair: the same answer at both instants — the valid-time clock had no effect, "
            "so these two fixtures tested nothing",
            file=sys.stderr,
        )

    print()
    if broken:
        print(
            f"{broken} fixture(s) could not run. This is an INSTRUMENT failure, not a result: fix the "
            "harness and re-run. Nothing about the wire has been learned either way.",
            file=sys.stderr,
        )
        return 1
    if mismatches:
        print(
            f"{mismatches} of {len(FIXTURES)} fixtures MISMATCHED.\n"
            "Per the gate, this is a FINDING and not a bug to patch around: the wire cannot express "
            "the .NET result. Write it up and stop — that ends the spike cheaply, which is what it "
            "was for.",
            file=sys.stderr,
        )
        return 1
    if voids:
        print(
            f"{voids} check(s) VOID — the comparison ran but had nothing to compare.\n"
            "This is not parity. A gate that passes on empty results is not a gate; fix the fixtures "
            "so they retrieve something, then re-run.",
            file=sys.stderr,
        )
        return 1

    print(f"parity on all {len(FIXTURES)} fixtures, including the isolation and as-of cases.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
