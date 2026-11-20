"""Prints what one recall returns, for TEST stores with fictional data only (it prints memory content). Runs inside the
speech server's container with a read-only context of the server's configuration.
Usage: python3 recall_show.py <min score> <query> [<query> ...]
"""
from __future__ import annotations

import json
import os
import sys

from agentmemory_native import ContextResult, MemoryClient, get_context_request

min_score = float(sys.argv[1])
with open(os.environ["S2S_MEMORY_CONFIG"], encoding="utf-8") as source:
    config = json.load(source)
config = {key: value for key, value in config.items() if key in ("schemaVersion", "store", "owner", "embedding")}
config["profile"] = "read_only"
client = MemoryClient(config)
for query in sys.argv[2:]:
    result = ContextResult.from_json(client.run_job(get_context_request(
        session_id="show", query=query, max_facts=5, max_preferences=3, min_score=min_score), 10000))
    print(f"{query!r} (min {min_score}): {result.status}")
    for fact in result.facts:
        print(f"  fact  {fact.subject} | {fact.predicate} | {fact.object}  (from {fact.valid_from}, until {fact.valid_until})")
    for preference in result.preferences:
        print(f"  pref  {preference.text}")
client.close()
