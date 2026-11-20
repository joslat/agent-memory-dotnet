"""Summarizes gr1-run.sh as the G-R1 checks. Usage: python gr1_summary.py <report-dir> <llm-model> <embedding-model>

Answers are checked by containment of a key word in the spoken reply's transcript (it says whether the memory reached
the answer, not whether the answer was good). Receipts and statuses are read from the server's own log lines, which
carry states and item ids only, never what was said.
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
llm, embedder = sys.argv[2], sys.argv[3]
writer = sys.argv[4] if len(sys.argv) > 4 else llm
log = "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in sorted(root.glob("speech-*.log")))


def turns(label: str) -> list[dict]:
    path = root / f"{label}.summary.json"
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else []


def reply(label: str, index: int = 0) -> str:
    found = turns(label)
    return str(found[index].get("reply") or "") if len(found) > index else ""


def transcript(label: str, index: int = 0) -> str:
    found = turns(label)
    return str(found[index].get("transcript") or "") if len(found) > index else ""


def has(text: str, word: str) -> bool:
    return re.search(rf"\b{word}\b", text, re.IGNORECASE) is not None


def ended(kind: str) -> list[str]:
    return re.findall(rf"memory: {kind} for \S+ ended (\w+)", log)


def told(kind: str) -> list[str]:
    return re.findall(rf"memory: {kind} status for \S+: (\S+)", log)


erase = {}
try:
    erase = json.loads((root / "erase.json").read_text(encoding="utf-8").strip().splitlines()[-1])
except (OSError, ValueError, IndexError):
    pass
end = json.loads((root / "capabilities-end.json").read_text(encoding="utf-8") or "{}").get("memory", {})
store = (root / "store-after-erase.txt").read_text(encoding="utf-8", errors="replace")
owned = [int(n) for n in re.findall(r"^\s*(\d+)\s*$", store, re.MULTILINE)]
journal_words = (root / "journal-words-after-erase.txt").read_text(encoding="utf-8").strip()

remembers, forgets = ended("remember"), ended("forget")
checks = [
    ("Spoken remembers were recognised and admitted (3)", len(re.findall(r"memory: remember requested for", log)) == 3),
    ("Every remember ended applied (verified)", remembers == ["applied"] * 3),
    ("The spoken forget ended applied", forgets == ["applied"]),
    ("The model was never told \"saved\" before the receipt said applied",
     all(state in ("pending", "applied") for state in told("remember"))),
    ("After a full restart, a fresh session's answer has the remembered colour (green)", has(reply("s2-ask-green"), "green")),
    ("After the change, the answer has blue", has(reply("s4-ask-blue"), "blue")),
    ("After the change, the answer does not have green", not has(reply("s4-ask-blue"), "green")),
    ("After the forget, the answer does not have the cat's name (Tom)", not has(reply("s6-ask-cat"), "tom")),
    ("The operator's erase was verified", erase.get("state") == "applied" and (erase.get("deleted") or 0) > 0),
    ("After the erase, the store holds nothing of the owner", owned == [0, 0]),
    ("After the erase, the journal holds none of the remembered words", journal_words == "0"),
    ("After the erase, no answer has green, blue or Tom",
     not any(has(reply("s8-ask-after-erase", i), w) for i in (0, 1) for w in ("green", "blue", "tom"))),
    ("At the end nothing is saving or needs attention",
     end.get("writes", {}).get("saving") == 0 and end.get("writes", {}).get("needsAttention") is False),
]

print(f"# G-R1, spoken end to end\n\nConversation model {llm} and embeddings {embedder}, local (Ollama); writer model {writer}. "
      "Nothing preloaded: every memory comes from a spoken request in this run. A full restart (store and journal "
      "on their volumes) separates the first remember from the first recall.\n")
print("| Check | Result |\n|---|---|")
for name, ok in checks:
    print(f"| {name} | {'pass' if ok else 'FAIL'} |")
print(f"\n{sum(ok for _, ok in checks)} of {len(checks)} pass.\n")
print("| Session | Heard | Replied |\n|---|---|---|")
for label in ("s1-remember-green", "s2-ask-green", "s3-remember-blue", "s4-ask-blue", "s5-remember-forget-cat",
              "s6-ask-cat", "s8-ask-after-erase"):
    for index, turn in enumerate(turns(label)):
        print(f"| {label} #{index + 1} | {transcript(label, index)} | {reply(label, index)} |")
print(f"\nStatuses told to the model for remembers: {told('remember')}; for forgets: {told('forget')}. "
      f"Receipts: remember {remembers}, forget {forgets}. Erase: {erase}.")
