"""Generates the demo notebook from a single source of truth.

The notebook is generated rather than hand-edited because a `.ipynb` is JSON with embedded outputs,
and hand-editing one is how a demo ends up with stale printed output that no longer matches the code
above it. That failure is invisible until someone in the room reads carefully.

    python crosslang/demo/kit/build_notebook.py

Writes `agentmemory_langgraph.ipynb` beside this file, with all outputs cleared. Execute it live, or
run `demo_langgraph.py` for the same beats in one shot.
"""

from __future__ import annotations

import json
import pathlib

MD = "markdown"
CODE = "code"

CELLS: list[tuple[str, str]] = [
    (MD, """# AgentMemory as a LangGraph store

> **Prototype.** This talks to a throwaway spike host over a draft wire. The productized SDK follows
> the published designs. Nothing here is on PyPI.

Four beats, in order:

1. **Store** — writes are typed triples, through LangGraph's own `store.put`.
2. **Resume** — the working-memory block plus a delta: *"here's what changed since your last session."*
3. **`as_of`** — the same query at two instants, two different answers. **This is the one we don't
   think any other `BaseStore` backend can do** — we have not surveyed them all, so take it as our
   claim about ours, not a proven claim about theirs.
4. **Provenance** — *why* do you believe that, and what did it replace?

## Before you run

```bash
docker run -d --name spike0-neo4j -p 7688:7687 -e NEO4J_AUTH=neo4j/spikepassword neo4j:5.26
NEO4J_URI=bolt://localhost:7688 NEO4J_USERNAME=neo4j NEO4J_PASSWORD=spikepassword \\
  ASPNETCORE_URLS=http://localhost:5173 dotnet run --project crosslang/spike0/Spike0.Host -c Release
pip install langgraph
```"""),

    (CODE, """import sys, pathlib
from datetime import datetime, timedelta, timezone

# The adapter lives one directory up. No install step: it is stdlib plus langgraph.
sys.path.insert(0, str(pathlib.Path.cwd().parent))
from agentmemory_store import AgentMemoryStore
from langgraph.store.base import BaseStore

store = AgentMemoryStore("http://localhost:5173")
assert isinstance(store, BaseStore)   # it IS a LangGraph store, not a lookalike

# Fixed instants. A demo whose output depends on the day it runs will fail on stage.
EPOCH      = datetime(2026, 1, 1, tzinfo=timezone.utc)
MARCH      = EPOCH + timedelta(days=75)
JOB_CHANGE = EPOCH + timedelta(days=180)
SEPTEMBER  = EPOCH + timedelta(days=250)
ALICE      = ("memories", "nb-alice")

store"""),

    (MD, """## 1 · Store — typed, not a blob

`put` is LangGraph's own method. What arrives at the other end is a **fact**: subject, predicate,
object, with a real-world validity window and the instant the system learned it.

`recorded_at` is here because this notebook compresses eight months into one cell. Bitemporality is
two clocks — *when it was true* and *when we learned it* — and a real deployment gets the second one
by having actually been running."""),

    (CODE, """store.put(ALICE, "nb-employer-acme", {
    "subject": "alice", "predicate": "works_at", "object": "Acme Corp",
    "valid_from": EPOCH, "recorded_at": EPOCH,
})
store.put(ALICE, "nb-diet", {
    "subject": "alice", "predicate": "dietary_restriction", "object": "vegetarian",
    "recorded_at": EPOCH,
})

store.get(ALICE, "nb-diet").value"""),

    (MD, """### The world moves on — an update that does not overwrite

Alice changes jobs in June. `supersedes` **closes** the old fact on the transaction clock instead of
deleting it.

This cell is what makes beat 3 possible. A key-value store overwrites here, and the March answer stops
existing — not "slow to find", *gone*."""),

    (CODE, """store.put(ALICE, "nb-employer-initech", {
    "subject": "alice", "predicate": "works_at", "object": "Initech",
    "valid_from": JOB_CHANGE, "recorded_at": JOB_CHANGE,
    "supersedes": "nb-employer-acme",
})

# Said again in a later session. The working-memory tier admits a fact only once the world has
# re-asserted it, so this is the conversation repeating itself, not padding.
store.put(ALICE, "nb-employer-initech", {
    "subject": "alice", "predicate": "works_at", "object": "Initech",
    "valid_from": JOB_CHANGE, "recorded_at": JOB_CHANGE,
})
store.put(ALICE, "nb-diet", {
    "subject": "alice", "predicate": "dietary_restriction", "object": "vegetarian",
})
print("Initech supersedes Acme — closed, not deleted")"""),

    (MD, """## 2 · Resume — not a cold start

Two reads an agent does when a returning user shows up.

**The working-memory block** is compiled per owner and fetched by a point-read, so — unlike a vector
search — a global top-K cannot starve it.

**The delta** is the resume brief. Its window is half-open on the server's clock and it hands back the
next checkpoint, so consecutive deltas partition time exactly: nothing seen twice, nothing lost
between calls."""),

    (CODE, """print(store.working_memory("nb-alice") or "(no block compiled)")"""),

    (CODE, """brief = store.delta("nb-alice", since=EPOCH + timedelta(hours=1))

print(f"since {brief['since']:%Y-%m-%d}  →  next checkpoint {brief['taken_at']:%Y-%m-%d %H:%M:%S}")
for f in brief["new_facts"]:
    print(f"  + {f['subject']} {f['predicate']} {f['object']}")
for p in brief["superseded"]:
    print(f"  ~ was \\"{p['old']['object']}\\", now \\"{p['new']['object']}\\"")
for f in brief["invalidated"]:
    print(f"  - {f['subject']} {f['predicate']} {f['object']}")
print("truncated:", brief["truncated_sections"] or "nothing")"""),

    (MD, """## 3 · `as_of` — the beat

The same question at two instants. **The two calls differ by one dictionary key**, and `filter` is
LangGraph's own parameter — nothing about the `BaseStore` signature changed.

Any existing LangGraph agent gets this by adding one key."""),

    (CODE, """QUESTION = "where does alice work?"

def employers(items):
    return {i.value["object"] for i in items if i.value["predicate"] == "works_at"}

live      = store.search(ALICE, query=QUESTION,                              limit=10)
march     = store.search(ALICE, query=QUESTION, filter={"as_of": MARCH},     limit=10)
september = store.search(ALICE, query=QUESTION, filter={"as_of": SEPTEMBER}, limit=10)

for label, result in (("live", live),
                      (f"as_of {MARCH:%Y-%m-%d}", march),
                      (f"as_of {SEPTEMBER:%Y-%m-%d}", september)):
    print(f"{label:<18} →  {employers(result)}")

# The witness. Two identical answers byte-match perfectly and prove nothing -- which is exactly what a
# broken as_of looks like, and is indistinguishable from success unless something checks.
assert employers(march) and employers(september), "an arm returned nothing — the clock was unobservable"
assert employers(march) != employers(september), "same answer at both instants — as_of did nothing"
print("\\n✓ different answers at different instants")"""),

    (MD, """## 4 · Provenance — *why* do you believe that?

The surprising answer above is auditable. The closed fact is still here: still readable, still
carrying its window, still pointing at what replaced it.

This is the cell to linger on. Everything else has a plausible-looking substitute somewhere; this one
is the reason the substitute is not equivalent.

Watch the read-audit counts. The **live** search above surfaced two facts to a caller and they show 1;
the two `as_of` searches returned answers too — the cell above printed them — but they were **not
recorded** and did not inflate anything. A historical read is
a replay, not a retrieval, and letting it move the counters would let auditing the past change how the
present ranks."""),

    (CODE, """rows = {r["id"]: r for r in store.history("nb-alice")}

for row in rows.values():
    if row["kind"] != "Fact":
        continue
    mark = "✗ closed " if row["status"] == "Invalidated" else "✓ live   "
    print(f"{mark} {row['summary']}")
    print(f"           valid {row['validFromUtc'] or '…'} → {row['validUntilUtc'] or 'now'}")
    if row["invalidatedAtUtc"]:
        print(f"           closed on the transaction clock at {row['invalidatedAtUtc']}")
    for replacement in row["supersededByIds"]:
        print(f"           replaced by → {rows.get(replacement, {}).get('summary', replacement)}")
    for replaced in row["supersedesIds"]:
        print(f"           replaces    ← {rows.get(replaced, {}).get('summary', replaced)}")
    print(f"           surfaced to a caller {row['readAuditCount']} time(s)")
    print()"""),

    (MD, """### What you just saw

| | |
|---|---|
| **Typed** | writes are triples with two clocks, not documents |
| **Non-destructive** | an update closes the old fact; nothing is overwritten |
| **Point-in-time** | one `filter` key, and any LangGraph agent can ask what was believed on a date |
| **Auditable** | the replaced fact is still there, still linked, still explains the answer |

Isolation is enforced under all of it — every row carries its owner, so a client can *check* rather
than trust.

**One engine. Every language gets this through a thin client, not a reimplementation.**"""),
]


def main() -> None:
    notebook = {
        "cells": [
            {
                "cell_type": kind,
                # Stable, derived from position rather than random: regenerating the notebook must not
                # churn every cell id, or a diff of a one-line change looks like a rewrite.
                "id": f"cell-{index:02d}",
                "metadata": {},
                "source": source.split("\n"),
                **({"outputs": [], "execution_count": None} if kind == CODE else {}),
            }
            for index, (kind, source) in enumerate(CELLS)
        ],
        "metadata": {
            "kernelspec": {"display_name": "Python 3", "language": "python", "name": "python3"},
            "language_info": {"name": "python"},
        },
        "nbformat": 4,
        "nbformat_minor": 5,
    }

    # Re-split so each source line keeps its trailing newline, which is the .ipynb convention; a
    # notebook written as bare lines renders as one long line in some viewers.
    for cell in notebook["cells"]:
        lines = cell["source"]
        cell["source"] = [line + "\n" for line in lines[:-1]] + [lines[-1]]

    out = pathlib.Path(__file__).with_name("agentmemory_langgraph.ipynb")
    out.write_text(json.dumps(notebook, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {out}  ({len(CELLS)} cells, outputs cleared)")


if __name__ == "__main__":
    main()
