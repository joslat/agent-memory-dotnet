"""Runs the recorded mutants (mutants.json): applies each one, runs the guards it names, and records whether they failed (the
mutant is killed) or passed (it survived: the guard does not hold its rule there). Every file is restored, whatever happens.

    python mutants.py --check     every mutant's edits apply exactly once (seconds; no build)
    python mutants.py [ids...]    run them (a build and a filtered test run each), writing mutants-results.json

The guards are first run on the unmutated tree: a guard that fails there kills nothing.
"""
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
MUTANTS = json.loads((HERE / "mutants.json").read_text(encoding="utf-8"))["mutants"]


def read(rel):
    raw = (ROOT / rel).read_bytes()
    return raw, raw.decode("utf-8-sig").replace("\r\n", "\n")


def mutated(mutant):
    raw, text = read(mutant["file"])
    for edit in mutant["edits"]:
        count = text.count(edit["old"])
        if count != 1:
            raise SystemExit(f"{mutant['id']}: an edit's old text occurs {count} times in {mutant['file']}")
        text = text.replace(edit["old"], edit["new"])
    nl = "\r\n" if b"\r\n" in raw else "\n"
    bom = "﻿".encode() if raw[:3] == b"\xef\xbb\xbf" else b""
    return raw, bom + text.replace("\n", nl).encode("utf-8")


def guards(mutant):
    if mutant["project"] == "kit":
        out = tempfile.mkdtemp(prefix="mutant-kit-")
        tool = ROOT / "tools/Ampack/bin/Debug/net10.0" / ("Ampack.exe" if sys.platform == "win32" else "Ampack")
        cmd = [sys.executable, "-I", str(ROOT / "conformance/pack-v1.1/run_vectors.py"), str(tool), out]
    else:
        cmd = ["dotnet", "test", str(ROOT / mutant["project"]), "-m:1", "--nologo", "--filter", mutant["filter"]]
    run = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=ROOT,
                         env={**os.environ, "MSBUILDDISABLENODEREUSE": "1"})
    log = run.stdout + run.stderr
    # A mutant that does not compile proves nothing about the guards, and a filter that runs no test guards nothing.
    if re.search(r"error CS\d+|Build FAILED", log):
        return "compile", log[-1500:]
    if mutant["project"] != "kit" and not re.search(r"Total:\s+[1-9]", log):
        return "none", log[-1500:]
    return run.returncode, log[-1500:]


def main():
    if "--check" in sys.argv:
        for mutant in MUTANTS:
            mutated(mutant)
        print(f"{len(MUTANTS)} mutants: every edit applies exactly once")
        return 0
    chosen = [m for m in MUTANTS if len(sys.argv) == 1 or m["id"] in sys.argv[1:]]
    results, baselines = [], {}
    for mutant in chosen:
        key = (mutant["project"], mutant["filter"])
        if key not in baselines:
            baselines[key] = guards(mutant)
        baseline, log = baselines[key]
        if baseline != 0:
            results.append({"id": mutant["id"], "rule": mutant["rule"], "door": mutant["door"], "killed": None, "note": f"its guards do not pass unmutated ({baseline}): " + log})
            print(f"BASE {mutant['id']}: its guards do not pass on the unmutated tree ({baseline})", flush=True)
            continue
        raw, changed = mutated(mutant)
        path = ROOT / mutant["file"]
        started = time.time()
        try:
            path.write_bytes(changed)
            code, log = guards(mutant)
        finally:
            path.write_bytes(raw)
        if code in ("compile", "none"):
            results.append({"id": mutant["id"], "rule": mutant["rule"], "door": mutant["door"], "killed": None, "note": f"the mutant proves nothing ({code}): " + log})
            print(f"VOID {mutant['id']}: {code}", flush=True)
            continue
        killed = code != 0
        results.append({"id": mutant["id"], "rule": mutant["rule"], "door": mutant["door"], "killed": killed, "seconds": round(time.time() - started)})
        print(f"{'kill' if killed else 'LIVE'} {mutant['id']} ({mutant['rule']} at {mutant['door']})", flush=True)
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], capture_output=True, text=True, cwd=ROOT).stdout.strip()
    previous = json.loads((HERE / "mutants-results.json").read_text(encoding="utf-8"))["results"] if (HERE / "mutants-results.json").exists() else []
    merged = {r["id"]: r for r in previous} | {r["id"]: r for r in results}
    (HERE / "mutants-results.json").write_text(json.dumps({"format": "agentmemory.guards.mutants-results/1", "commit": commit,
                                                           "results": [merged[m["id"]] for m in MUTANTS if m["id"] in merged]}, indent=1) + "\n", encoding="utf-8")
    live = [r["id"] for r in results if r["killed"] is not True]
    print(f"{len(results) - len(live)}/{len(results)} mutants killed" + (f"; not killed: {', '.join(live)}" if live else ""))
    return 1 if live else 0


if __name__ == "__main__":
    sys.exit(main())
