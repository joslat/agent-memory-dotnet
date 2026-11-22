"""NATIVE-GATE-SHARED: proves no gate test was lost when the tests became data. Standard library only.

Every test in cases/ORIGINAL-TESTS.txt (the Python tests before the move) must be the "source" of a case in
cases/*.json, or be listed in REQUIRED.md (behaviour each binding tests in its own language, or library behaviour the
Python harness tests once). And every case must name a source, have a unique id and a known tier. Exit 1 on any gap.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

GATE = Path(__file__).resolve().parent


def main() -> int:
    original = [line.strip() for line in (GATE / "cases" / "ORIGINAL-TESTS.txt").read_text(encoding="utf-8").splitlines()
                if line.strip() and not line.startswith("#")]
    cases = []
    for path in sorted((GATE / "cases").glob("*.json")):
        if path.name != "fixtures.json":
            cases += [dict(c, file=path.name) for c in json.loads(path.read_text(encoding="utf-8"))["cases"]]
    required = (GATE / "REQUIRED.md").read_text(encoding="utf-8")
    listed = set(re.findall(r"`(native/gate/[^`]+::[^`]+)`", required))

    problems = []
    ids = [c["id"] for c in cases]
    problems += [f"case id used twice: {i}" for i in sorted({i for i in ids if ids.count(i) > 1})]
    problems += [f"case {c['id']}: tier {c.get('tier')!r} is not A, B or C" for c in cases if c.get("tier") not in ("A", "B", "C")]
    problems += [f"case {c['id']}: no source" for c in cases if not c.get("source")]
    sources = {c.get("source") for c in cases}
    # A case with no Python original names itself: native/gate/cases/<file>::<id>.
    problems += [f"case {c['id']}: its source {c['source']} is not an original test" for c in cases
                 if c.get("source") and c["source"] not in original
                 and c["source"] != f"native/gate/cases/{c.get('file', '')}::{c['id']}"]
    problems += [f"lost: {test} is neither a case's source nor in REQUIRED.md" for test in original
                 if test not in sources and test not in listed]
    problems += [f"REQUIRED.md lists {test}, which is also a case's source" for test in sorted(listed & sources)]
    problems += [f"REQUIRED.md lists {test}, which is not an original test" for test in sorted(listed - set(original))]

    print(f"{len(original)} original tests: {len(set(original) & sources)} are cases ({len(cases)} cases), "
          f"{len(set(original) & listed)} in REQUIRED.md")
    for problem in problems:
        print("  " + problem)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
