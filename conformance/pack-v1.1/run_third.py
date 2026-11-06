# SPDX-License-Identifier: Apache-2.0
"""The third verifier (feature 18): runs `ampack-rs` (reader-rs/, Rust) on every vector results.json lists, as the Python
verifier is run, and compares its verdict with the expected one.

It reads results.json and the vectors and trust files beside it; it never builds, regenerates or rewrites a vector (that is
run_vectors.py's, which it never calls). Exit 0 verified, 1 refused; anything else is an error that agrees with nothing.

usage: python conformance/pack-v1.1/run_third.py [<path to ampack-rs>] [--kit <folder>] [--show-all] [--show-rules] [--no-wait]
       --kit: the folder run_vectors.py wrote (results.json, vectors/, the trust files); default: this folder
       (default: reader-rs/target/release/ampack-rs[.exe], else reader-rs/target/debug/)
Prints each disagreement, then per vector family (the name up to its first '-') and in total how many agree.
Waits while a .NET run (dotnet.exe or testhost.exe) is going: one heavy task at a time on this machine. --no-wait is for a
caller that is that task already (run_vectors.py, and a test host running it): waiting there would wait on itself.
exit 0 when every vector agrees, 1 when a reader disagrees, 2 on a usage error, 3 when a test input is missing: a missing
input blocks its vector, it is never counted as an expected refusal (2026-11-06).
"""
import json
import os
import re
import subprocess
import sys
import time
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
VECTORS = HERE / "vectors"
ROOT = HERE.parent.parent
EXE = ".exe" if os.name == "nt" else ""


def dotnet_running():
    if os.name != "nt":
        return False
    try:
        out = subprocess.run(["tasklist"], capture_output=True, text=True, errors="replace").stdout.lower()
    except OSError:
        return False
    return "dotnet.exe" in out or "testhost.exe" in out


def names_rule(pattern, text):
    """The pattern as run_vectors.py reads it (a regular expression), or as plain text."""
    try:
        if re.search(pattern, text, re.I) is not None:
            return True
    except re.error:
        pass
    return pattern.lower() in text.lower()


def find_binary(given):
    if given:
        return Path(given)
    for build in ("release", "debug"):
        path = ROOT / "reader-rs" / "target" / build / f"ampack-rs{EXE}"
        if path.is_file():
            return path
    return None


def command(binary, v, base=HERE):
    vectors = base / "vectors"
    pack = str(vectors / f"{v['vector']}.ampack")
    policy = str(base / v["policy"])
    kind = v["kind"]
    if kind == "pack":
        return [str(binary), "verify", pack, policy, *[str(base / r) for r in v.get("revocations", [])]]
    if kind == "receipt":
        return [str(binary), "verify", pack, policy, "--receipt", str(vectors / f"{v['vector']}.receipt.dsse.json"), "--location", v["location"]]
    if kind in ("certificate", "erasure", "aggregate"):
        doc = v["document"]
        target = doc if doc.startswith("META/attestations/") else str(vectors / doc)
        return [str(binary), "verify", pack, policy, f"--{kind}", target]
    raise ValueError(f"unknown kind {kind!r} for {v['vector']}")


def main(argv):
    args, kit = [], HERE
    rest = iter(argv[1:])
    for a in rest:
        if a == "--kit":
            kit = Path(next(rest, ".")).resolve()
        elif not a.startswith("--"):
            args.append(a)
    show_all = "--show-all" in argv
    if len(args) > 1:
        print(__doc__)
        return 2
    binary = find_binary(args[0] if args else None)
    if binary is None or not binary.is_file():
        print("error: ampack-rs not found; build it with: cargo build --release -j 2 (in reader-rs/)")
        return 2
    while "--no-wait" not in argv and dotnet_running():
        print("a .NET run is going; waiting 60 s")
        time.sleep(60)
    results = json.loads((kit / "results.json").read_text(encoding="utf-8"))
    # Informative, never the verdict: for the vectors reasons.json pins, whether the refusal names the same rule as the
    # Python verifier's pattern for it (ampack-rs words its reasons as the Python verifier does).
    reasons = json.loads((HERE / "reasons.json").read_text(encoding="utf-8"))["reasons"]
    pinned, same_rule, other_rule = 0, 0, []
    families = defaultdict(lambda: [0, 0])
    disagreements = []
    started = time.perf_counter()
    blocked = []
    for v in results["vectors"]:
        cmd = command(binary, v, kit)
        # Every file the reader is handed must exist: a reader refusing a file it cannot read would otherwise pass as the
        # expected refusal (2026-11-06, policy-with-a-private-key after the work folder was removed).
        missing = [a for a in cmd[2:] if (a.endswith(".json") or a.endswith(".ampack")) and not a.startswith("META/")
                   and "://" not in a
                   and not Path(a).is_file()]
        if missing:
            blocked.append((v["vector"], [str(Path(m).relative_to(kit)) if Path(m).is_relative_to(kit) else m for m in missing]))
            continue
        run = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
        verdict = {0: "verified", 1: "refused"}.get(run.returncode, f"error ({run.returncode})")
        agree = verdict == v["expected"]
        family = v["vector"].split("-")[0]
        families[family][0] += agree
        families[family][1] += 1
        said = (run.stdout.strip().splitlines() or [run.stderr.strip()[-200:]])[0] if (run.stdout.strip() or run.stderr.strip()) else ""
        if agree and v["vector"] in reasons and verdict == "refused":
            pinned += 1
            if names_rule(reasons[v["vector"]][1], run.stdout + run.stderr):
                same_rule += 1
            else:
                other_rule.append((v["vector"], said))
        if not agree:
            disagreements.append((v["vector"], v["kind"], v["expected"], verdict, said, v.get("dotnetSays", "")))
        if show_all or not agree:
            print(f"{'ok  ' if agree else 'FAIL'} {v['vector']:50} expected {v['expected']:8} rust {verdict:8} {said[:120]}")
    elapsed = time.perf_counter() - started
    print()
    print("family                     agree / total")
    for family in sorted(families, key=lambda f: (-families[f][1], f)):
        a, t = families[family]
        print(f"{family:26} {a:5} / {t:<5}{'' if a == t else '  <-'}")
    agreed = sum(a for a, _ in families.values())
    total = sum(t for _, t in families.values())
    print(f"{'total':26} {agreed:5} / {total:<5}   ({elapsed:.1f} s, {binary})")
    print(f"refused for the rule reasons.json pins (Python's wording): {same_rule} / {pinned} (informative)")
    if blocked:
        print(f"\nblocked, a test input is missing ({len(blocked)}): a harness failure, not a verdict")
        for name, missing in blocked:
            print(f"  {name}: {', '.join(missing)}")
    if "--show-rules" in argv:
        for name, said in other_rule:
            print(f"  other words: {name}: {said[:160]}")
    if disagreements:
        print("\ndisagreements (vector, kind, expected, rust, rust says | .NET says):")
        for name, kind, expected, verdict, said, dotnet in disagreements:
            print(f"  {name} [{kind}] expected {expected}, rust {verdict}: {said[:160]} | {dotnet[:160]}")
    if blocked:
        return 3
    return 0 if agreed == total else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
