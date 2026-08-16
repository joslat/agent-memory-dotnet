"""D4 — the timed rehearsal against a clean Neo4j.

Runs every kit artifact in run-sheet order and times each one, so `DEMO-SCRIPT.md` carries measured
timings rather than estimates. A rehearsal that isn't timed only tells you the demo *works*; the thing
that actually goes wrong in a room is that it works and takes fourteen minutes.

    python crosslang/demo/kit/dry_run.py

Assumes a live host on :5173 pointed at a **freshly created** database — the point is to catch what
only fails on a cold start, which is where the first two spike failures lived. It does not tear down or
recreate anything itself: a script that can delete a database is a script that will, eventually,
delete the wrong one.
"""

from __future__ import annotations

import pathlib
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).parent
ROOT = HERE.parent.parent.parent

STEPS: list[tuple[str, list[str], float]] = [
    # (name, argv, the run sheet's budget in seconds)
    ("store contract tests", [sys.executable, str(HERE.parent / "test_store_contract.py")], 30),
    ("preflight", [sys.executable, str(HERE / "preflight.py")], 30),
    ("demo_langgraph (beats 1-6)", [sys.executable, str(HERE.parent / "demo_langgraph.py")], 120),
    ("notebook, executed", [sys.executable, str(HERE / "run_notebook.py")], 180),
    ("screencast replay", [sys.executable, str(HERE / "screencast.py"), "--fast"], 15),
]

NOISE = ("Debugger warning", "frozen_modules", "frozen modules", "debugger miss breakpoints",
         "PYDEVD", "MissingIDField", "validate(nb)", "zmq", "_get_loop", "RuntimeWarning",
         "Note: Debugging", "site-packages")


def main() -> int:
    print("D4 dry run — clean database, run-sheet order\n" + "=" * 62)
    results: list[tuple[str, float, int, str]] = []

    for name, argv, budget in STEPS:
        started = time.monotonic()
        completed = subprocess.run(
            argv, capture_output=True, cwd=ROOT,
            env={**__import__("os").environ, "PYTHONIOENCODING": "utf-8"})
        elapsed = time.monotonic() - started

        streams = (completed.stdout or b"").decode("utf-8", "replace") \
            + (completed.stderr or b"").decode("utf-8", "replace")
        tail = "\n".join(
            line for line in streams.splitlines()
            if line.strip() and not any(noise in line for noise in NOISE))[-600:]

        verdict = "OK " if completed.returncode == 0 else "FAIL"
        budget_note = "" if elapsed <= budget else f"  ⚠ over budget ({budget:.0f}s)"
        print(f"{verdict}  {name:<28} {elapsed:6.1f}s{budget_note}")
        if completed.returncode != 0:
            print(f"      ↳ {tail}")
        results.append((name, elapsed, completed.returncode, tail))

    total = sum(elapsed for _, elapsed, _, _ in results)
    failures = [name for name, _, code, _ in results if code != 0]

    print("=" * 62)
    print(f"machine time: {total:6.1f}s  ({total/60:.1f} min)")
    print("The 10-minute budget is SPEAKING time; machine time is what must fit inside it with room "
          "to talk over.")
    if failures:
        print(f"\nDRY RUN FAILED — {', '.join(failures)}")
        return 1
    print("\nDRY RUN CLEAN — every artifact ran on a database created minutes ago.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
