"""Summarizes latency-matrix.sh: one Markdown table of first reply audio and server end-to-end time per scenario,
the server's recall times by kind, the second process's recall times, and container memory and CPU.
Usage: python latency_summary.py <report-dir> <cores>"""
from __future__ import annotations

import json
import pathlib
import re
import statistics
import sys

root = pathlib.Path(sys.argv[1])
cores = sys.argv[2] if len(sys.argv) > 2 else "?"
SCENARIOS = [
    ("off-hit", "Memory off, work question"), ("off-miss", "Memory off, greeting"),
    ("off-cpu-hit", "Memory off, CPU load, work question"), ("off-cpu-miss", "Memory off, CPU load, greeting"),
    ("cold", "Memory on, first turn after a server start"),
    ("warm-hit", "Memory on, work question (recall finds facts)"), ("warm-miss", "Memory on, greeting (recall finds nothing)"),
    ("concurrent-hit", "Memory on, another process recalling 20/s"),
    ("cpu-hit", "Memory on, CPU load, work question"), ("cpu-miss", "Memory on, CPU load, greeting"),
    ("outage", "Memory on, Neo4j stopped"),
]


def turns(prefix: str) -> list[dict]:
    found = []
    for path in sorted(root.glob(f"{prefix}*.summary.json")):
        if prefix == "cold" or path.name.startswith(prefix + ".") or path.name == f"{prefix}.summary.json":
            found += json.loads(path.read_text(encoding="utf-8"))
    return found


def describe(values: list[float]) -> str:
    if not values:
        return "n/a"
    ordered = sorted(values)
    p90 = ordered[min(len(ordered) - 1, round(0.9 * (len(ordered) - 1)))]
    return f"{statistics.median(ordered) * 1000:,.0f} / {p90 * 1000:,.0f}"


print(f"# G-R0 latency matrix\n\nCPU speech on this machine (Docker VM with {cores} cores), deterministic model stand-in, "
      "Neo4j beside the server. First reply audio: end of speech to the first reply audio received by the client, "
      "on the client's monotonic clock (network receipt, not device playback). Median / p90 in ms.\n")
print("| Scenario | Turns | Answered from memory | First reply audio | Server end to end |")
print("|---|---|---|---|---|")
for prefix, title in SCENARIOS:
    found = turns(prefix)
    completed = [t for t in found if t.get("status") == "completed"]
    first = [t["client_first_audio_s"] for t in completed if t.get("client_first_audio_s") is not None]
    e2e = [(t.get("server_latency") or {}).get("e2e_s") for t in completed]
    from_memory = sum(1 for t in completed if str(t.get("reply", "")).startswith("From memory:"))
    print(f"| {title} | {len(completed)} of {len(found)} | {from_memory} | {describe(first)} | "
          f"{describe([v for v in e2e if v is not None])} |")

log = (root / "speech.log").read_text(encoding="utf-8", errors="replace") if (root / "speech.log").exists() else ""
hits = [(float(a), int(b), int(c)) for a, b, c in
        re.findall(r"memory: \d+ items for \S+ after (\d+) ms \(library queued (\d+) ms, ran (\d+) ms\)", log)]
misses = [(float(a), int(b), int(c)) for a, b, c in
          re.findall(r"memory: nothing recalled for \S+ after (\d+) ms \(library queued (\d+) ms, ran (\d+) ms\)", log)]
late = len(re.findall(r"missed its deadline", log))
warm = re.findall(r"memory: warm-up recall (\S+) after (\d+) ms", log)


def recall_line(name: str, rows: list[tuple[float, int, int]]) -> str:
    if not rows:
        return f"| {name} | 0 | n/a | n/a | n/a |"
    return (f"| {name} | {len(rows)} | {statistics.median(r[0] for r in rows):.0f} | "
            f"{statistics.median(r[1] for r in rows):.0f} | {statistics.median(r[2] for r in rows):.0f} |")


print("\n## Recall inside the server (every memory-on turn above)\n")
print("Waited: from the start of the recall (final transcript) until the model request had it. The library's own "
      "time splits into queue and run (embedding call plus store query). Median ms.\n")
print("| Recall | Count | Waited | Library queued | Library ran |")
print("|---|---|---|---|---|")
print(recall_line("Found facts", hits))
print(recall_line("Found nothing", misses))
print(f"\nRecalls past the 450 ms deadline (turns answered without memory): {late}. "
      f"Warm-up at start: {', '.join(f'{s} {ms} ms' for s, ms in warm) or 'n/a'}.")

load = (root / "recall-load.log")
if load.exists():
    lines = [json.loads(line) for line in load.read_text(encoding="utf-8").splitlines() if line.startswith("{")]
    if lines:
        print(f"\nSecond process: {lines[-1]['recalls']} recalls, {sum(l['failures'] for l in lines)} failures; "
              f"median of 10 s windows {statistics.median(l['median_ms'] for l in lines):.1f} ms, "
              f"worst p95 {max(l['p95_ms'] for l in lines):.1f} ms.")

print("\n## Containers after each scenario (memory, CPU)\n")
for label in ("off", "warm", "concurrent", "cpu", "outage"):
    path = root / f"{label}.stats.txt"
    if path.exists():
        rows = [line.split(" ", 1) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
        print(f"- {label}: " + "; ".join(f"{name.replace('am-latency-', '')} {rest}" for name, rest in rows))
