"""Summarizes ab-memory.sh: first reply audio after speech end (client-measured) and the server's end-to-end time,
for spoken turns, memory off against memory on. Usage: python3 ab_summary.py <report-dir>"""
from __future__ import annotations

import json
import pathlib
import statistics
import sys


def values(root: pathlib.Path, arm: str) -> tuple[list[float], list[float], list[str]]:
    first, e2e, replies = [], [], []
    for summary in sorted(root.glob(f"{arm}-*/summary.json")):
        for turn in json.loads(summary.read_text(encoding="utf-8")):
            if not str(turn["turn"]).endswith(".wav"):
                continue
            replies.append(turn["reply"])
            if turn.get("client_first_audio_s") is not None:
                first.append(float(turn["client_first_audio_s"]))
            latency = (turn.get("server_latency") or {}).get("e2e_s")
            if latency is not None:
                e2e.append(float(latency))
    return first, e2e, replies


def describe(name: str, sample: list[float]) -> str:
    if not sample:
        return f"{name}: no samples"
    ordered = sorted(sample)
    p90 = ordered[min(len(ordered) - 1, int(round(0.9 * (len(ordered) - 1))))]
    return (f"{name}: n={len(sample)} median {statistics.median(sample) * 1000:.0f} ms, "
            f"p90 {p90 * 1000:.0f} ms, min {ordered[0] * 1000:.0f}, max {ordered[-1] * 1000:.0f}")


root = pathlib.Path(sys.argv[1])
results = {arm: values(root, arm) for arm in ("off", "on")}
for arm, (first, e2e, replies) in results.items():
    print(f"memory {arm}")
    print("  " + describe("first reply audio after speech end", first))
    print("  " + describe("server end to end", e2e))
    print(f"  answered from memory: {sum(r.startswith('From memory:') for r in replies)} of {len(replies)} spoken turns")
off, on = results["off"][0], results["on"][0]
if off and on:
    print(f"difference in median first reply audio (on - off): {(statistics.median(on) - statistics.median(off)) * 1000:+.0f} ms")
