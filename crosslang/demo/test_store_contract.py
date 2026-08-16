"""Contract tests for the LangGraph store adapter — the three things a review caught.

Not a substitute for a suite; these cover exactly the defects that were found by reviewing the D2
build, so a later edit that reintroduces one fails here instead of in front of a room.

    python crosslang/demo/test_store_contract.py     # needs the prototype host on :5173

Each test states what the pre-fix behaviour was, so red-probing is a matter of reverting the named
line and watching only that test fail.
"""

from __future__ import annotations

import sys
from datetime import datetime, timezone

from langgraph.store.base import BaseStore

from agentmemory_store import AgentMemoryStore

BASE = "http://localhost:5173"
EPOCH = datetime(2026, 1, 1, tzinfo=timezone.utc)

ALICE = ("memories", "contract-alice")
BOB = ("memories", "contract-bob")

failures: list[str] = []


def expect(name: str, condition: bool, detail: str = "") -> None:
    print(f"  {'✓' if condition else '✗'}  {name}" + (f"   {detail}" if detail else ""))
    if not condition:
        failures.append(name)


def main() -> int:
    store = AgentMemoryStore(BASE)

    store.put(ALICE, "contract-alice-fact", {
        "subject": "alice", "predicate": "works_at", "object": "AliceCo", "recorded_at": EPOCH})
    store.put(BOB, "contract-bob-fact", {
        "subject": "bob", "predicate": "works_at", "object": "BobCo", "recorded_at": EPOCH})

    # 1. get() must honour the namespace.
    #    Pre-fix: _get ignored op.namespace entirely and returned whatever the by-id read gave back,
    #    so Alice's namespace happily returned Bob's fact. The engine's by-id read is unscoped by
    #    design; the STORE contract is not.
    expect("get() finds a fact in its own namespace",
           store.get(ALICE, "contract-alice-fact") is not None)
    expect("get() does NOT cross namespaces",
           store.get(ALICE, "contract-bob-fact") is None,
           "Bob's fact must be invisible from Alice's namespace")

    # 2. A namespace with no owner segment is an error, not a guess.
    #    Pre-fix: `namespace[-1]` turned ("memories",) into an owner literally named "memories" --
    #    failing closed, but silently, leaving the caller with an empty store and no reason why.
    try:
        store.get(("memories",), "contract-alice-fact")
        expect("ownerless namespace rejected", False, "no error was raised")
    except ValueError as error:
        expect("ownerless namespace rejected", "last segment" in str(error))

    # 3. search() offset must not eat the limit.
    #    Pre-fix: the host was asked for `limit` rows and the first `offset` were then dropped
    #    client-side, so page 2 of a 1-row page came back empty and looked like "no more results".
    for index in range(4):
        store.put(ALICE, f"contract-page-{index}", {
            "subject": "alice", "predicate": "owns", "object": f"item-{index}",
            "recorded_at": EPOCH})

    page1 = store.search(ALICE, query="what does alice own?", limit=2)
    page2 = store.search(ALICE, query="what does alice own?", limit=2, offset=2)
    expect("search() page 1 is full", len(page1) == 2, f"got {len(page1)}")
    expect("search() page 2 is non-empty", len(page2) > 0, f"got {len(page2)}")
    expect("search() pages do not overlap",
           not ({i.key for i in page1} & {i.key for i in page2}))

    # 4. Still a real BaseStore, with LangGraph's own methods.
    expect("is a BaseStore", isinstance(store, BaseStore))
    expect("does not override LangGraph's public methods",
           all(getattr(type(store), m) is getattr(BaseStore, m)
               for m in ("get", "put", "search", "delete")))

    print()
    if failures:
        print(f"FAILED — {len(failures)}: {', '.join(failures)}")
        return 1
    print("All contract tests pass.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
