"""Times recalls on a just-started store, from inside the speech server's container, with a read-only context of the
server's own configuration. Usage: python3 recall_probe.py <warm-up min score> <query> [<query> ...]

Prints one line per recall: what was asked (the warm-up or the query's position), how many items came back, the
library's run time and the wall time. Never prints recalled text or the configuration.
"""
from __future__ import annotations

import json
import os
import sys
import time

from agentmemory_native import ContextResult, MemoryClient, get_context_request

warm_min = float(sys.argv[1])
queries = sys.argv[2:]
with open(os.environ["S2S_MEMORY_CONFIG"], encoding="utf-8") as source:
    config = json.load(source)
# Read-only: the server keeps its writable context (and its journal); this process only reads.
config = {key: value for key, value in config.items() if key in ("schemaVersion", "store", "owner", "embedding")}
config["profile"] = "read_only"
started = time.monotonic()
client = MemoryClient(config)
print(f"open {(time.monotonic() - started) * 1000:.0f} ms")


def recall(label: str, query: str, min_score: float, facts: int, preferences: int) -> None:
    began = time.monotonic()
    result = ContextResult.from_json(client.run_job(get_context_request(
        session_id="probe", query=query, max_facts=facts, max_preferences=preferences, min_score=min_score), 10000))
    wall = (time.monotonic() - began) * 1000
    items = len(result.facts) + len(result.preferences) if result.ok else f"{result.status}"
    print(f"{label}: items {items}, library ran {result.run_ms} ms, wall {wall:.0f} ms")


recall(f"warm-up (min {warm_min})", "warm-up", warm_min, 1, 1)
for index, query in enumerate(queries, 1):
    recall(f"query {index}", query, 0.75, 5, 3)
    recall(f"query {index} again", query, 0.75, 5, 3)
client.close()
