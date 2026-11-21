"""Load, first-call, open and close times of the native library on this platform, as one Markdown table (NATIVE-PLAT-M1).

Usage: python timings.py <library> --rid <rid> [--runs 5]

Each run is a fresh Python process, so the load is a real first load of the library. The open is a read-only context
against a closed local port without connecting, so it measures the library and not a database. The median of the runs
is reported. Standard library only; needs native/python on PYTHONPATH.
"""
from __future__ import annotations

import argparse
import json
import os
import statistics
import subprocess
import sys
from pathlib import Path

CHILD = r"""
import json, sys, time
from agentmemory_native import _library as abi
from agentmemory_native import MemoryClient
t0 = time.perf_counter()
lib = abi.load(sys.argv[1])
t1 = time.perf_counter()
lib.am_v1_abi_version()
t2 = time.perf_counter()
memory = MemoryClient({"schemaVersion": 1, "profile": "read_only", "owner": {"mode": "fixed", "id": "timing"},
                       "store": {"uri": "bolt://127.0.0.1:9", "username": "neo4j", "password": "unused", "verifyConnectivity": False},
                       "embedding": {"dimensions": 8, "model": "timing"}})
t3 = time.perf_counter()
memory.close()
t4 = time.perf_counter()
print(json.dumps({"load": (t1 - t0) * 1000, "first_call": (t2 - t1) * 1000, "open": (t3 - t2) * 1000, "close": (t4 - t3) * 1000}))
"""


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("library", type=Path)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--runs", type=int, default=5)
    args = parser.parse_args()
    runs = []
    library = str(args.library.resolve())
    # The client looks the library up again by this variable, so the child loads the one being timed.
    env = os.environ | {"AGENTMEMORY_NATIVE_LIBRARY": library}
    for _ in range(args.runs):
        out = subprocess.run([sys.executable, "-c", CHILD, library], capture_output=True, text=True, env=env)
        if out.returncode != 0:
            sys.exit(f"the timing run failed ({out.returncode}): {out.stderr}")
        runs.append(json.loads(out.stdout.strip().splitlines()[-1]))
    median = {key: statistics.median(run[key] for run in runs) for key in runs[0]}
    size_mib = args.library.stat().st_size / (1024 * 1024)
    print("| RID | Library | Load ms | First call ms | Open ms | Close ms | Runs |")
    print("|---|---|---|---|---|---|---|")
    print(f"| {args.rid} | {size_mib:.1f} MiB | {median['load']:.1f} | {median['first_call']:.2f} | {median['open']:.1f} "
          f"| {median['close']:.1f} | {args.runs} (median) |")


if __name__ == "__main__":
    main()
