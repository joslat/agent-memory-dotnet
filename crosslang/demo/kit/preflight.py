"""Pre-demo check: is every dependency of the run sheet actually alive?

Run this fifteen minutes before, not five. It exists so a dead container is found now instead of in
front of the room, and so the decision to take Fallback A is made calmly rather than mid-sentence.

    python crosslang/demo/kit/preflight.py            # READY / NOT READY, exit code follows
    python crosslang/demo/kit/preflight.py --curl     # print the Fallback D commands and stop

It checks the things that have actually broken, in the order they break: the host, the schema, the
seed, and — last and most important — that the `as_of` beat gives two DIFFERENT answers. A host that
is up and answering identically at both instants passes every liveness check and fails the demo.
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

BASE = "http://localhost:5173"
OWNER = "preflight-alice"
NS = ("memories", OWNER)

EPOCH = datetime(2026, 1, 1, tzinfo=timezone.utc)
MARCH = EPOCH + timedelta(days=75)
JOB_CHANGE = EPOCH + timedelta(days=180)
SEPTEMBER = EPOCH + timedelta(days=250)

CURL = f"""Fallback D — the two as_of recalls by hand. One field differs.

Owner is {OWNER}: the one THIS script writes and verifies above. (It used to read a notebook-only
owner, which would have answered both curls identically if the notebook had not been run — two
matching answers in front of the room, the exact failure the beat check exists to prevent.)

curl -s {BASE}/v1/recall -H 'Content-Type: application/json' -d '{{
  "sessionId":"demo","userId":"{OWNER}","query":"where does alice work?",
  "maxFacts":5,"asOf":"2026-03-17T00:00:00Z"}}'

curl -s {BASE}/v1/recall -H 'Content-Type: application/json' -d '{{
  "sessionId":"demo","userId":"{OWNER}","query":"where does alice work?",
  "maxFacts":5,"asOf":"2026-09-08T00:00:00Z"}}'
"""

checks: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    checks.append((name, ok, detail))
    return ok


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--curl", action="store_true")
    args = parser.parse_args()
    if args.curl:
        print(CURL)
        return 0

    # 1. Is the host up at all?
    try:
        with urllib.request.urlopen(f"{BASE}/v1/meta", timeout=5) as response:
            meta = json.loads(response.read())
        check("host responding", True, meta["wire"])
    except (urllib.error.URLError, OSError) as error:
        check("host responding", False, str(error))
        return report()

    check("prototype label present", "PROTOTYPE" in meta.get("warning", ""),
          "the room must be able to see what this is")

    for capability in ("recall.asOf", "delta", "workingMemory.get", "history"):
        check(f"capability {capability}", capability in meta["capabilities"])

    # 2. Can it write and read back? This is also what proves the schema is bootstrapped -- an
    #    unmigrated database fails here with a 500 rather than at the worst possible moment.
    sys.path.insert(0, str(__import__("pathlib").Path(__file__).parent.parent))
    from agentmemory_store import AgentMemoryStore, AgentMemoryStoreError  # noqa: E402

    store = AgentMemoryStore(BASE)
    try:
        store.put(NS, "preflight-acme", {
            "subject": "alice", "predicate": "works_at", "object": "Acme Corp",
            "valid_from": EPOCH, "recorded_at": EPOCH,
        })
        store.put(NS, "preflight-initech", {
            "subject": "alice", "predicate": "works_at", "object": "Initech",
            "valid_from": JOB_CHANGE, "recorded_at": JOB_CHANGE,
            "supersedes": "preflight-acme",
        })
        # Said twice, exactly as the demo says it twice. The working-memory tier admits a fact only
        # once the world has re-asserted it, so a preflight that wrote once would report the block
        # missing and send you to a fallback you did not need.
        store.put(NS, "preflight-initech", {
            "subject": "alice", "predicate": "works_at", "object": "Initech",
            "valid_from": JOB_CHANGE, "recorded_at": JOB_CHANGE,
        })
        check("write + supersede", True)
    except (AgentMemoryStoreError, ValueError) as error:
        check("write + supersede", False, str(error))
        return report()

    check("read back typed", store.get(NS, "preflight-initech") is not None)

    # 3. THE BEAT. Last, because it is the only check that can pass every liveness test and still
    #    mean the demo is dead: two identical answers look exactly like success.
    def employers(items):
        return {i.value["object"] for i in items if i.value["predicate"] == "works_at"}

    march = employers(store.search(NS, query="where does alice work?", filter={"as_of": MARCH}))
    september = employers(store.search(NS, query="where does alice work?", filter={"as_of": SEPTEMBER}))

    check("as_of March non-empty", bool(march), str(march))
    check("as_of September non-empty", bool(september), str(september))
    check("THE BEAT: answers differ", march != september, f"{march} vs {september}")

    # 4. The two Wave-C reads the resume section shows.
    check("working-memory block compiled", bool(store.working_memory(OWNER)))
    brief = store.delta(OWNER, since=EPOCH + timedelta(hours=1))
    check("delta reports something",
          bool(brief["new_facts"] or brief["superseded"] or brief["invalidated"]))
    check("provenance walk has a closed fact",
          any(r["status"] == "Invalidated" for r in store.history(OWNER)))

    return report()


def report() -> int:
    width = max(len(name) for name, _, _ in checks)
    for name, ok, detail in checks:
        print(f"  {'✓' if ok else '✗'}  {name.ljust(width)}   {detail}")

    failed = [name for name, ok, _ in checks if not ok]
    print()
    if failed:
        print(f"NOT READY — {len(failed)} check(s) failed: {', '.join(failed)}")
        print("Take Fallback A (screencast). Do not improvise in the room.")
        return 1
    print("READY")
    return 0


if __name__ == "__main__":
    sys.exit(main())
