"""Pins a reason for every refused vector that names none (review 7, L8: the kit checked the reason for 32 of 291, so a vector
refused for another rule than its own passed). From results.json, for each such vector, the longest run of words without digits,
quotes or paths in what each verifier said, at least four words: stable across runs (keys and times change every run) and
specific to the rule. Writes reasons.json, which run_vectors.py checks beside REASONS. Read the file before committing it: a
vector refused for the wrong rule today would be pinned to that rule.

usage: python pin_reasons.py
"""
import json
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
results = json.loads((HERE / "results.json").read_text(encoding="utf-8"))["vectors"]
text = (HERE / "run_vectors.py").read_text(encoding="utf-8")
block = text[text.index("REASONS = {"):text.index("\n}", text.index("REASONS = {"))]
named = set(re.findall(r'^\s+"([a-z0-9-]+)": \(', block, re.M))


def stable(message):
    # The first line that states a reason: a "Note:" line before it is context, not the refusal.
    lines = [re.sub(r"^refused: ", "", line.strip()) for line in message.strip().splitlines()]
    message = next((line for line in lines if line and not line.startswith("Note:")), "")
    message = re.sub(r"^(?:[A-Za-z]:\\|/)\S+\s*", "", message)
    message = re.sub(r"^(?:packs/\S+/: )?(?:META/\S+: )", "", message)
    words, best, run = message.split(" "), [], []
    for word in words + ["0"]:
        if re.search(r"[0-9'\"/\\:]", word) or not word:
            if len(run) > len(best):
                best = run
            run = []
        else:
            run.append(word)
    phrase = " ".join(best).rstrip(".,;")
    return phrase if len(best) >= 4 else None


# Where .NET says it in the runtime's own words, which a runtime update may change: left to REASONS by hand.
RUNTIME = {"policy-not-utf8", "nesting-65-deep", "nesting-100000-deep", "policy-second-key-off-curve", "policy-second-key-x-not-reduced"}
pinned, skipped = {}, []
for v in results:
    if v["expected"] != "refused" or v["vector"] in named or v["vector"] in RUNTIME:
        continue
    d, p = stable(v.get("dotnetSays", "")), stable(v.get("pythonSays", ""))
    if d and p:
        pinned[v["vector"]] = [d, p]
    else:
        skipped.append(v["vector"])
(HERE / "reasons.json").write_text(json.dumps({"format": "agentmemory.pack.conformance.reasons/1", "reasons": pinned}, indent=1, ensure_ascii=False) + "\n",
                                   encoding="utf-8")
print(f"pinned {len(pinned)}; {len(skipped)} left to REASONS by hand: {', '.join(skipped)}")
