"""Another process recalling from the same store at a steady rate, through the native library, until stopped.

Usage: python3 recall_load.py <agentmemory-config.json> <recalls-per-second>
Prints one summary line every 10 s (count, failures, median and p95 milliseconds). Standard library and the
agentmemory-native wheel only.
"""
import json
import statistics
import sys
import time

from agentmemory_native import ContextResult, MemoryClient, get_context_request

config = json.load(open(sys.argv[1], encoding="utf-8"))
interval = 1.0 / float(sys.argv[2])
questions = ["Where do I work?", "How should you answer me?", "What is the weather like?"]
durations, failures, count = [], 0, 0
next_report = time.monotonic() + 10
with MemoryClient(config) as client:
    while True:
        began = time.monotonic()
        request = get_context_request(session_id="load", query=questions[count % len(questions)], max_facts=5,
                                      max_preferences=3, min_score=0.75)
        result = ContextResult.from_json(client.run_job(request, 2000))
        durations.append((time.monotonic() - began) * 1000)
        failures += 0 if result.ok else 1
        count += 1
        if time.monotonic() >= next_report:
            ordered = sorted(durations)
            print(json.dumps({"recalls": count, "failures": failures, "median_ms": round(statistics.median(ordered), 1),
                              "p95_ms": round(ordered[int(0.95 * (len(ordered) - 1))], 1)}), flush=True)
            durations.clear()
            next_report += 10
        time.sleep(max(0.0, interval - (time.monotonic() - began)))
