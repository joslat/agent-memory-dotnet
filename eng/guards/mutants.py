"""Runs the recorded sabotage checks (mutants.json): applies each deliberate breakage, runs the guards it names, and records
whether they failed (caught) or passed (the guard does not hold its rule there). Every file is restored, whatever happens.

    python mutants.py --check     every breakage's edits apply exactly once (seconds; no build)
    python mutants.py [ids...]    run them (a build and a filtered test run each), writing mutants-results.json

Each result records what it is evidence for (review 9, D5, D6, D10): the hash of the file as it was broken (line ends and BOM
normalized), the breakage's own hash (from mutants.json), and the tests that failed. The registry test accepts a catch only for
today's file and breakage, and only when a failing test is one of the rule's guards.

The guards are first run on the unbroken tree: a guard that fails there proves nothing, nor does a breakage that does not
compile, a filter that runs no test, or a run past its time limit. A copy of each file is kept in .restore/ while it is broken,
and put back at the next start if a run was killed.
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
RESTORE = HERE / ".restore"
TIMEOUT = 1800
MUTANTS = json.loads((HERE / "mutants.json").read_text(encoding="utf-8"))["mutants"]


def normalized_hash(raw):
    return hashlib.sha256(raw.decode("utf-8-sig").replace("\r\n", "\n").encode("utf-8")).hexdigest()


def recover():
    # A run that was killed while a file was broken: put the copy back before anything else.
    if not RESTORE.exists():
        return
    for meta in RESTORE.glob("*.json"):
        info = json.loads(meta.read_text(encoding="utf-8"))
        (ROOT / info["file"]).write_bytes((RESTORE / info["copy"]).read_bytes())
        print(f"restored {info['file']} from a killed run", flush=True)
    shutil.rmtree(RESTORE)


def mutated(mutant):
    raw = (ROOT / mutant["file"]).read_bytes()
    text = raw.decode("utf-8-sig").replace("\r\n", "\n")
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
    try:
        run = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=ROOT,
                             env={**os.environ, "MSBUILDDISABLENODEREUSE": "1"}, timeout=TIMEOUT)
    except subprocess.TimeoutExpired:
        return "timeout", "", []
    log = run.stdout + run.stderr
    # A breakage that does not compile proves nothing about the guards, and a filter that runs no test guards nothing.
    if re.search(r"error CS\d+|Build FAILED", log):
        return "compile", log[-1500:], []
    if mutant["project"] != "kit" and not re.search(r"Total:\s+[1-9]", log):
        return "none", log[-1500:], []
    failed = ["kit"] if mutant["project"] == "kit" and run.returncode != 0 else sorted({
        ".".join(name.split("(")[0].split(".")[-2:]) for name in re.findall(r"^\s+Failed (\S+)", log, re.M)})
    return run.returncode, log[-1500:], failed


def main():
    if "--check" in sys.argv:
        for mutant in MUTANTS:
            mutated(mutant)
        print(f"{len(MUTANTS)} mutants: every edit applies exactly once")
        return 0
    recover()
    chosen = [m for m in MUTANTS if len(sys.argv) == 1 or m["id"] in sys.argv[1:]]
    results, baselines = [], {}
    for mutant in chosen:
        key = (mutant["project"], mutant["filter"])
        if key not in baselines:
            baselines[key] = guards(mutant)
        baseline, log, _ = baselines[key]
        common = {"id": mutant["id"], "rule": mutant["rule"], "door": mutant["door"], "mutant_sha256": mutant["sha256"]}
        if baseline != 0:
            results.append(common | {"killed": None, "note": f"its guards do not pass unmutated ({baseline}): " + log})
            print(f"BASE {mutant['id']}: its guards do not pass on the unmutated tree ({baseline})", flush=True)
            continue
        raw, changed = mutated(mutant)
        path = ROOT / mutant["file"]
        RESTORE.mkdir(exist_ok=True)
        (RESTORE / (mutant["id"] + ".copy")).write_bytes(raw)
        (RESTORE / (mutant["id"] + ".json")).write_text(json.dumps({"file": mutant["file"], "copy": mutant["id"] + ".copy"}), encoding="utf-8")
        started = time.time()
        try:
            path.write_bytes(changed)
            code, log, failed = guards(mutant)
        finally:
            path.write_bytes(raw)
            shutil.rmtree(RESTORE, ignore_errors=True)
        if code in ("compile", "none", "timeout"):
            results.append(common | {"killed": None, "note": f"the mutant proves nothing ({code}): " + log})
            print(f"VOID {mutant['id']}: {code}", flush=True)
            continue
        killed = code != 0
        results.append(common | {"killed": killed, "file_sha256": normalized_hash(raw), "failed": failed, "seconds": round(time.time() - started)})
        print(f"{'kill' if killed else 'LIVE'} {mutant['id']} ({mutant['rule']} at {mutant['door']}){'' if not failed else ' by ' + ', '.join(failed[:3])}", flush=True)
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], capture_output=True, text=True, cwd=ROOT).stdout.strip()
    previous = json.loads((HERE / "mutants-results.json").read_text(encoding="utf-8"))["results"] if (HERE / "mutants-results.json").exists() else []
    merged = {r["id"]: r for r in previous} | {r["id"]: r for r in results}
    (HERE / "mutants-results.json").write_text(json.dumps({"format": "agentmemory.guards.mutants-results/2", "commit": commit,
                                                           "results": [merged[m["id"]] for m in MUTANTS if m["id"] in merged]}, indent=1) + "\n", encoding="utf-8")
    live = [r["id"] for r in results if r["killed"] is not True]
    print(f"{len(results) - len(live)}/{len(results)} mutants killed" + (f"; not killed: {', '.join(live)}" if live else ""))
    return 1 if live else 0


if __name__ == "__main__":
    sys.exit(main())
