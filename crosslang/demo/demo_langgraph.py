"""Drives the AgentMemory LangGraph store end-to-end — the D2 beat (meeting-demo-track.md).

PROTOTYPE. Nothing here is published or packaged; the productized SDK follows the published designs.

What this shows, in the order the meeting demo runs it:

1. **Session one** — an agent writes what it learned. ``store.put(...)``, LangGraph's own method.
2. **It comes back** — ``store.get(...)``, and the fact is typed, not a blob.
3. **The world moves on** — an update *supersedes* rather than overwrites, so the old answer survives.
4. **Session two** — the two reads that make a resume different from a cold start: the compiled
   *working-memory block*, then the *delta*: "here is what changed since you left."
5. **The beat** — ``store.search(..., filter={"as_of": ...})``. The same query at two instants
   returns two different answers, because the engine underneath is bitemporal.
6. **Isolation** — Bob's fact is not in Alice's search, and the returned owner proves it.

Every write and read here goes through LangGraph's own ``BaseStore`` surface, unmodified — including
the ``filter`` dict that carries ``as_of``. That is the claim being demonstrated: an existing LangGraph
agent gets point-in-time recall by adding one key to a filter it already passes.

Run:  python crosslang/demo/demo_langgraph.py [--base-url http://localhost:5173]
"""

from __future__ import annotations

import argparse
import sys
from datetime import datetime, timedelta, timezone

from langgraph.store.base import BaseStore

from agentmemory_store import AgentMemoryStore

# Fixed instants, so the demo reads the same in August as in March. Real wall-clock time appears
# nowhere in the assertions -- a demo whose output depends on the day it is run is a demo that will
# fail on stage for a reason nobody can debug in front of an audience.
EPOCH = datetime(2026, 1, 1, tzinfo=timezone.utc)
MARCH = EPOCH + timedelta(days=75)
JOB_CHANGE = EPOCH + timedelta(days=180)
SEPTEMBER = EPOCH + timedelta(days=250)

# Session one ended an hour into January. The delta in step 3 asks "what changed since then", so this
# is the checkpoint an agent would have saved on its way out.
SESSION_ONE_END = EPOCH + timedelta(hours=1)

ALICE = ("memories", "demo-alice")
BOB = ("memories", "demo-bob")

# Tracks anything that made the run uninterpretable. A demo that prints five green checks while
# comparing nothing is worse than one that fails: the parity spike did exactly that on its first run.
VOIDS: list[str] = []


def heading(number: int, text: str) -> None:
    print(f"\n{'─' * 78}\n{number}. {text}\n{'─' * 78}")


def show(label: str, items) -> None:
    print(f"  {label}: {len(items)} item(s)")
    for item in items:
        v = item.value
        window = f"  [{v.get('valid_from') or '…'} → {v.get('valid_until') or 'now'}]"
        print(f"    · {v['subject']} {v['predicate']} {v['object']}"
              f"   (owner {v.get('owner_id')}){window}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://localhost:5173")
    args = parser.parse_args()

    store = AgentMemoryStore(args.base_url)

    # Not decoration. If this adapter is not a BaseStore, "works with any LangGraph agent" is false,
    # and everything below is a demo of a bespoke client rather than of an integration.
    assert isinstance(store, BaseStore), "adapter is not a LangGraph BaseStore"

    # Reachability guard, in the repo's own idiom. The demo's claim is that put/get/search are
    # LANGGRAPH's methods dispatching into our batch() -- not ours. Override any of them later for a
    # quick win and the claim quietly becomes false while every print below still reads the same. This
    # fails the moment that happens.
    for method in ("get", "put", "search", "delete"):
        assert getattr(type(store), method) is getattr(BaseStore, method), (
            f"AgentMemoryStore overrides BaseStore.{method}() — the demo claims LangGraph's own "
            f"surface is used unmodified, and that is no longer true"
        )

    # ── 1. session one: the agent writes what it learned ──────────────
    heading(1, "Session one, January — the agent stores what it learned  (store.put)")

    # `recorded_at` is set because this demo compresses eight months into one process. The transaction
    # clock is not decoration: with everything recorded at "now", step 4's March query correctly returns
    # NOTHING -- in March the system knew nothing -- and the demo would look broken while the engine was
    # being exactly right. Stating when each thing was learned is what a real deployment gets for free.
    store.put(ALICE, "demo-employer-acme", {
        "subject": "alice", "predicate": "works_at", "object": "Acme Corp",
        "valid_from": EPOCH, "recorded_at": EPOCH,
    })
    store.put(ALICE, "demo-diet", {
        "subject": "alice", "predicate": "dietary_restriction", "object": "vegetarian",
        "recorded_at": EPOCH,
    })
    store.put(BOB, "demo-bob-employer", {
        "subject": "bob", "predicate": "works_at", "object": "Globex",
        "recorded_at": EPOCH,
    })
    print("  stored 3 facts across 2 owners — as TRIPLES, not opaque documents")

    # ── 2. it comes back, and it is still typed ───────────────────────
    heading(2, "It comes back typed  (store.get)")

    item = store.get(ALICE, "demo-diet")
    if item is None:
        print("  VOID: put-then-get returned nothing")
        VOIDS.append("get returned None")
    else:
        print(f"  {item.value['subject']} · {item.value['predicate']} · {item.value['object']}")
        print("  subject/predicate/object survived the round trip — a blob store returns a blob")

    # ── the world moves on, between sessions ──────────────────────────
    heading(3, "June — Alice changes jobs. An update, not an overwrite.")

    store.put(ALICE, "demo-employer-initech", {
        "subject": "alice", "predicate": "works_at", "object": "Initech",
        "valid_from": JOB_CHANGE, "recorded_at": JOB_CHANGE,
        # THE DIFFERENCE FROM A KEY-VALUE STORE. `supersedes` closes the old fact on the transaction
        # clock rather than deleting it, so "what did we believe in March" stays answerable. An
        # overwrite would have destroyed the only copy of that answer.
        "supersedes": "demo-employer-acme",
    })
    print("  Initech supersedes Acme — the old fact is CLOSED, not deleted")

    # Session two: the same things get said again. Not padding — the working-memory tier admits a fact
    # only once the world has re-asserted it (MinFactMentionCount = 2 by default), so a demo that
    # lowered that threshold to make the block appear would be demonstrating a setting, not a tier.
    store.put(ALICE, "demo-employer-initech", {
        "subject": "alice", "predicate": "works_at", "object": "Initech",
        "valid_from": JOB_CHANGE, "recorded_at": JOB_CHANGE,
    })
    store.put(ALICE, "demo-diet", {
        "subject": "alice", "predicate": "dietary_restriction", "object": "vegetarian",
    })

    # ── 4. session two: the resume, not a cold start ──────────────────
    heading(4, "Session two — the resume brief, not a cold start")

    block = store.working_memory("demo-alice")
    if block:
        print("  WORKING-MEMORY BLOCK (compiled, point-read — a top-K cannot starve it):")
        for line in block.splitlines():
            print(f"    │ {line}")
    else:
        # Not fatal: the tier can legitimately have nothing to compile. Reported so nobody reads a
        # silent gap as a feature that ran and produced nothing.
        print("  (no block compiled for this owner)")
        VOIDS.append("working-memory block empty — the first half of the resume showed nothing")

    brief = store.delta("demo-alice", since=SESSION_ONE_END)
    print(f"\n  DELTA since {brief['since']:%Y-%m-%d}  →  checkpoint {brief['taken_at']:%Y-%m-%d %H:%M:%S}")
    print(f"    new: {len(brief['new_facts'])}   superseded: {len(brief['superseded'])}"
          f"   invalidated: {len(brief['invalidated'])}")
    for fact in brief["new_facts"]:
        print(f"      + {fact['subject']} {fact['predicate']} {fact['object']}")
    for pair in brief["superseded"]:
        print(f"      ~ was \"{pair['old']['object']}\", now \"{pair['new']['object']}\"")
    if brief["truncated_sections"]:
        print(f"    (truncated: {', '.join(brief['truncated_sections'])})")
    if not (brief["new_facts"] or brief["superseded"] or brief["invalidated"]):
        VOIDS.append("delta was empty — the resume brief had nothing to report")

    # ── 5. the beat: same question, two instants ──────────────────────
    heading(5, "THE BEAT — the same query at two instants  (store.search filter={'as_of': …})")

    question = "where does alice work?"
    march = store.search(ALICE, query=question, filter={"as_of": MARCH}, limit=10)
    september = store.search(ALICE, query=question, filter={"as_of": SEPTEMBER}, limit=10)

    print(f"  as_of {MARCH:%Y-%m-%d}:")
    show("recalled", march)
    print(f"\n  as_of {SEPTEMBER:%Y-%m-%d}:")
    show("recalled", september)

    def employers(items) -> set[str]:
        return {i.value["object"] for i in items if i.value["predicate"] == "works_at"}

    march_employer, september_employer = employers(march), employers(september)
    print(f"\n  March → {march_employer or '∅'}    September → {september_employer or '∅'}")

    # The void witness carried over from the parity spike. Two identical answers byte-match perfectly
    # and demonstrate nothing: if the clock had no effect, this step tested nothing however green it
    # looked. Same for two empty answers.
    if not march_employer or not september_employer:
        VOIDS.append("an as_of arm returned no employer — the clock could not be observed")
    elif march_employer == september_employer:
        VOIDS.append(
            f"both instants answered {march_employer} — as_of made no difference, so it proved nothing")
    else:
        print("  ✓ different answers at different instants. A store that overwrote the only copy "
              "cannot do this: the earlier value is gone.")

    # ── 6. isolation, verifiable rather than asserted ─────────────────
    heading(6, "Isolation — Bob's fact is not in Alice's search")

    leaked = [i for i in store.search(ALICE, query="who does bob work for?", limit=20)
              if i.value.get("owner_id") not in (None, "demo-alice")]
    if leaked:
        print(f"  ✗ LEAK: {len(leaked)} fact(s) from another owner")
        for i in leaked:
            print(f"      {i.value}")
        VOIDS.append("owner isolation leaked")
    else:
        print("  ✓ nothing from another owner — and the owner rides on the wire, so the client can "
              "CHECK that rather than trust it")

    # ── verdict ───────────────────────────────────────────────────────
    print(f"\n{'═' * 78}")
    if VOIDS:
        print("RUN VOID — something here demonstrated nothing:")
        for void in VOIDS:
            print(f"  · {void}")
        print("\nA demo that prints green while comparing nothing is worse than one that fails.")
        return 1

    print("Every beat ran and each showed something. Prototype host, draft wire.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
