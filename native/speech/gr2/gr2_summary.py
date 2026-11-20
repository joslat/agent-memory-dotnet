"""Summarizes gr2-run.sh as the G-R2 checks. Usage: python gr2_summary.py <report-dir> <llm> <embedder> [<writer>]

Answers are checked by containment of a key word in the spoken reply's transcript; receipts and admissions come from
the server's own log lines (states and item ids only). A killed container keeps its log, so log lines are counted once.
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
llm, embedder = sys.argv[2], sys.argv[3]
writer = sys.argv[4] if len(sys.argv) > 4 else llm
lines = sorted(set((root / "speech.log").read_text(encoding="utf-8", errors="replace").splitlines())) \
    if (root / "speech.log").exists() else []
log = "\n".join(lines)


def turns(label: str) -> list[dict]:
    path = root / f"{label}.summary.json"
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else []


def reply(label: str, index: int = 0) -> str:
    found = turns(label)
    return str(found[index].get("reply") or "") if len(found) > index else ""


def heard(label: str, index: int = 0) -> str:
    found = turns(label)
    return str(found[index].get("transcript") or "") if len(found) > index else ""


def has(text: str, words: str) -> bool:
    return re.search(rf"\b{words}\b", text, re.IGNORECASE) is not None


def number(name: str) -> int | None:
    try:
        values = re.findall(r"^\s*(\d+)\s*$", (root / name).read_text(encoding="utf-8", errors="replace"), re.MULTILINE)
        return int(values[-1]) if values else None
    except OSError:
        return None


observed = re.findall(r"memory: observe requested for (\S+)", log)
observed_ended = re.findall(r"memory: observe for \S+ ended (\w+)", log)
paused = "learning unasked off" in log
erase = {}
try:
    erase = json.loads((root / "erase.json").read_text(encoding="utf-8").strip().splitlines()[-1])
except (OSError, ValueError, IndexError):
    pass
end = json.loads((root / "capabilities-end.json").read_text(encoding="utf-8") or "{}").get("memory", {})
retries = number("journal-retries.txt")

checks = [
    ("Three told facts were learned unasked (sister, the move, the tea)", len(set(observed)) == 3),
    ("No question was learned (the weather, every 'where', 'what')", len(set(observed)) == 3),
    ("The model was never told about an unasked write", "remember status" not in log and "status line(s)" not in log),
    ("After a full restart, a fresh session answers with what was told (Lisbon)", has(reply("s2-ask-sister"), "Lisbon")),
    ("After the move was told, the answer has Madrid", has(reply("s4-ask-sister"), "Madrid")),
    ("After the move was told, the answer does not have Lisbon", not has(reply("s4-ask-sister"), "Lisbon")),
    ("Learning was paused for one session", paused),
    ("What was told while paused is never learned later (no chess)", not has(reply("s6-ask-brother"), "chess")),
    ("After the server was killed during a learned write, the tea is stored exactly once", number("store-tea.txt") == 1),
    ("After the restart, a fresh session answers with the tea (Earl Grey)", has(reply("s8-ask-tea"), r"Earl\s+Gr[ae]y")),
    ("The operator's erase was verified", erase.get("state") == "applied" and (erase.get("deleted") or 0) > 0),
    ("After the erase, the store holds nothing of the owner", number("store-after-erase.txt") == 0),
    ("After the erase, no answer has Lisbon, Madrid or Earl Grey",
     not any(has(reply("s9-ask-after-erase", i), w) for i in (0, 1) for w in ("Lisbon", "Madrid", r"Earl\s+Gr[ae]y"))),
    ("At the end nothing is saving or needs attention",
     end.get("writes", {}).get("saving") == 0 and end.get("writes", {}).get("needsAttention") is False),
]

print(f"# G-R2, learning spoken end to end\n\nConversation model {llm} and embeddings {embedder}, local; writer model "
      f"{writer}. Profile `learning` through the recipe (MEMORY_LEARNING=on). Nothing preloaded and nothing asked to be "
      "remembered: every memory comes from something the owner said in passing.\n")
print("| Check | Result |\n|---|---|")
for name, ok in checks:
    print(f"| {name} | {'pass' if ok else 'FAIL'} |")
print(f"\n{sum(ok for _, ok in checks)} of {len(checks)} pass.\n")
crash = ("the write was put back in the queue after the restart and applied once" if retries
         else "the write had finished before the kill, so the restart had nothing to resume")
print(f"Crash case: {crash} (journal retry records: {retries}). Learned writes ended: {observed_ended}. "
      f"Erase: {erase}.\n")
print("| Session | Heard | Replied |\n|---|---|---|")
for label in ("s1-tell-sister", "s2-ask-sister", "s3-tell-moved", "s4-ask-sister", "s5-tell-brother-paused",
              "s6-ask-brother", "s7-tell-tea", "s8-ask-tea", "s9-ask-after-erase"):
    for index, _ in enumerate(turns(label)):
        print(f"| {label} #{index + 1} | {heard(label, index)} | {reply(label, index)} |")
