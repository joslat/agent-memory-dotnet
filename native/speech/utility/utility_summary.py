"""Summarizes utility-run.sh: per arm, how many answers carried the stored fact, any leak of the other owner's facts,
and the time to first reply audio. The check is containment of the fact's key word in the spoken reply's transcript; it
says whether the fact reached the answer, not whether the answer was good.
Usage: python utility_summary.py <report-dir> <llm-model> <embedding-model>"""
from __future__ import annotations

import json
import pathlib
import re
import statistics
import sys

root = pathlib.Path(sys.argv[1])
llm, embedder = sys.argv[2], sys.argv[3]
EXPECTED = {"work.wav": "zurich", "dog.wav": "mochi", "food.wav": "ramen", "instrument.wav": "cello"}
OTHER_OWNER = ("barcelona", "rex")  # owner B's facts; its preference word ("detailed") is too common to count
ARMS = [("off", "Memory off"), ("instructions", "Facts in the instructions (the app's local memory)"),
        ("recall", "AgentMemory recall")]


def median_ms(values: list[float]) -> str:
    return f"{statistics.median(values) * 1000:,.0f}" if values else "n/a"


print(f"# Utility with a real model\n\nModel {llm}, embeddings {embedder}, both local (Ollama). Fictional facts; the "
      "same spoken questions in every arm. \"Has the fact\": the fact's key word appears in the reply. First reply "
      "audio: median ms from end of speech, client clock.\n")
print("| Arm | Has the fact | Work | Dog | Food | Instrument | Other owner's facts | First reply audio, questions | First reply audio, greeting |")
print("|---|---|---|---|---|---|---|---|---|")
for prefix, title in ARMS:
    turns = []
    for path in sorted(root.glob(f"{prefix}-*.summary.json")):
        turns += json.loads(path.read_text(encoding="utf-8"))
    by_question = {name: [0, 0] for name in EXPECTED}
    leaks, question_audio, greeting_audio = 0, [], []
    for turn in turns:
        reply = str(turn.get("reply") or "").lower()
        name = str(turn.get("turn", "")).rsplit("/", 1)[-1]
        leaks += any(re.search(rf"\b{word}\b", reply) for word in OTHER_OWNER)
        audio = turn.get("client_first_audio_s")
        if name in EXPECTED:
            by_question[name][1] += 1
            by_question[name][0] += EXPECTED[name] in reply
            if audio is not None:
                question_audio.append(audio)
        elif audio is not None:
            greeting_audio.append(audio)
    hits = sum(v[0] for v in by_question.values())
    total = sum(v[1] for v in by_question.values())
    cells = " | ".join(f"{v[0]}/{v[1]}" for v in by_question.values())
    print(f"| {title} | {hits}/{total} | {cells} | {leaks} | {median_ms(question_audio)} | {median_ms(greeting_audio)} |")

log = root / "recall-speech.log"
if log.exists():
    text = log.read_text(encoding="utf-8", errors="replace")
    found = [float(m) for m in re.findall(r"memory: \d+ items for \S+ after (\d+) ms", text)]
    nothing = [float(m) for m in re.findall(r"memory: nothing recalled for \S+ after (\d+) ms", text)]
    late = len(re.findall(r"missed its deadline", text))
    ran = [int(m) for m in re.findall(r"library queued \d+ ms, ran (\d+) ms", text)]
    print(f"\nRecall inside the server: {len(found)} found facts (median wait {statistics.median(found) if found else 0:.0f} ms), "
          f"{len(nothing)} found nothing, {late} past the 450 ms deadline; library run time median "
          f"{statistics.median(ran) if ran else 0:.0f} ms (embedding call plus store query).")

print("\nReplies, first session of each arm:\n")
for prefix, title in ARMS:
    path = root / f"{prefix}-1.summary.json"
    if path.exists():
        print(f"- {title}: " + " | ".join(repr(t.get("reply")) for t in json.loads(path.read_text(encoding="utf-8"))))
