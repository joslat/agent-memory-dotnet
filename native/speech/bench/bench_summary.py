"""Summarizes bench-run.sh (SPEECH_BENCH_PLAN.md). Usage: python bench_summary.py <report-dir> <llm> <embedder> <writer>

Every question turn is scored by deterministic predicates from gold.json (words an answer must or must not hold);
store checks come from the memory arm's own counts. Denominators are kept: selected, completed, scored, failed. Writes
bench-results.json (no transcripts) beside the Markdown it prints.
"""
from __future__ import annotations

import datetime
import json
import pathlib
import random
import re
import statistics
import sys

root = pathlib.Path(sys.argv[1])
llm, embedder, writer = sys.argv[2], sys.argv[3], sys.argv[4]
corpus = {c["id"]: c for c in json.loads((root / "corpus" / "corpus.json").read_text(encoding="utf-8"))["clusters"]}
gold = json.loads((root / "corpus" / "gold.json").read_text(encoding="utf-8"))["clusters"]
SEMANTIC = set(range(1, 8))
MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November",
          "December"]


def read(path: pathlib.Path, default=None):
    try:
        return path.read_text(encoding="utf-8").strip()
    except OSError:
        return default


def number(path: pathlib.Path) -> int | None:
    values = re.findall(r"^\s*(\d+)\s*$", read(path, "") or "", re.MULTILINE)
    return int(values[-1]) if values else None


def has(text: str, word: str) -> bool:
    return re.search(rf"\b{re.escape(word)}", text, re.IGNORECASE) is not None


SAME = {"favourite": "favorite", "colour": "color", "neighbour": "neighbor", "dr": "doctor", "mr": "mister",
        "one": "1", "two": "2", "three": "3", "four": "4", "five": "5", "six": "6", "seven": "7", "eight": "8",
        "nine": "9", "ten": "10", "twelve": "12"}


def words(text: str | None) -> list[str]:
    """Words as compared: spelling variants and number words count as the same word."""
    return [SAME.get(w, w) for w in re.sub(r"[^a-z0-9 ]", "", (text or "").lower().replace("-", " ")).split()]


def misheard(cluster_dir: pathlib.Path, cid: str) -> bool:
    """Whether speech recognition changed any line of this cluster (so a miss may be the recogniser's, not memory's)."""
    sessions = [s["session"] for s in corpus[cid]["steps"] if "session" in s]
    for k, lines in enumerate(sessions, 1):
        turns = json.loads(read(cluster_dir / f"s{k}.summary.json", "[]") or "[]")
        for line, turn in zip(lines, turns):
            if words(line["text"]) != words(turn.get("transcript")):
                return True
    return False


def check(rule: dict, reply: str, said_at: datetime.date) -> bool | None:
    if rule.get("control"):
        return bool(reply.strip())
    ok = True
    if "expect" in rule:
        ok = ok and any(has(reply, w) for w in rule["expect"])
    if "forbid" in rule:
        ok = ok and not any(has(reply, w) for w in rule["forbid"])
    if "expect_month_before_run" in rule:
        month = MONTHS[(said_at.month - 2) % 12]
        ok = ok and (has(reply, month) or has(reply, "last month") or has(reply, "a month ago"))
    if rule.get("not_all_caps"):
        letters = re.sub(r"[^A-Za-z]", "", reply)
        ok = ok and not (letters and letters.isupper())
    return ok


arms = sorted(p.name for p in root.iterdir() if p.is_dir() and p.name in ("off", "oracle", "memory"))
order = [a for a in ("off", "oracle", "memory") if a in arms]
rows: list[dict] = []
for arm in order:
    for rep_dir in sorted((root / arm).glob("r*")):
        for cluster_dir in sorted(rep_dir.iterdir()):
            cid = cluster_dir.name
            if cid not in corpus:
                continue
            said_at = datetime.date.fromtimestamp(cluster_dir.stat().st_mtime)
            store = gold[cid].get("store", {})
            heard_wrong = misheard(cluster_dir, cid)
            for key, rule in gold[cid]["questions"].items():
                session, turn = (int(x) for x in re.match(r"s(\d+)t(\d+)", key).groups())
                summary = cluster_dir / f"s{session}.summary.json"
                turns = json.loads(read(summary, "[]") or "[]")
                result = turns[turn - 1] if len(turns) >= turn else {}
                completed = result.get("status") == "completed"
                reply = result.get("reply") or ""
                rows.append({"arm": arm, "repeat": rep_dir.name, "cluster": cid, "category": corpus[cid]["category"],
                             "question": key, "control": bool(rule.get("control")), "completed": completed,
                             "pass": check(rule, reply, said_at) if completed else None,
                             "first_audio_s": result.get("client_first_audio_s"), "misheard": heard_wrong})
            if arm == "memory" and store:
                live = number(cluster_dir / "memories.txt")
                if "memories_after" in store:
                    rows.append({"arm": arm, "repeat": rep_dir.name, "cluster": cid, "category": corpus[cid]["category"],
                                 "question": "store", "control": False, "completed": live is not None,
                                 "pass": None if live is None else live == store["memories_after"], "first_audio_s": None})
                if "exactly_once" in store:
                    count = number(cluster_dir / "key-count.txt")
                    killed = "killed while running" in (read(cluster_dir / "kill.txt", "") or "")
                    rows.append({"arm": arm, "repeat": rep_dir.name, "cluster": cid, "category": corpus[cid]["category"],
                                 "question": "store-once", "control": False, "completed": count is not None,
                                 "pass": None if count is None else count == 1, "first_audio_s": None, "killed": killed})


def rate(selected: list[dict]) -> tuple[int, int, int, int]:
    completed = [r for r in selected if r["completed"]]
    passed = [r for r in completed if r["pass"]]
    return len(selected), len(completed), len(passed), len(selected) - len(completed)


def cluster_success(arm: str) -> dict[str, float]:
    """Per cluster: mean over repeats and semantic questions of success (an incomplete case counts as no success)."""
    out: dict[str, list[float]] = {}
    for r in rows:
        if r["arm"] == arm and r["category"] in SEMANTIC and not r["control"] and r["question"].startswith("s"):
            out.setdefault(r["cluster"], []).append(1.0 if r["pass"] else 0.0)
    return {k: sum(v) / len(v) for k, v in out.items()}


def bootstrap(a: dict[str, float], b: dict[str, float], seed: int = 20261120, n: int = 10_000) -> tuple[float, float, float]:
    ids = sorted(set(a) & set(b))
    if not ids:
        return float("nan"), float("nan"), float("nan")
    d = [b[i] - a[i] for i in ids]
    rng = random.Random(seed)
    means = sorted(sum(rng.choice(d) for _ in ids) / len(ids) for _ in range(n))
    return sum(d) / len(d), means[int(0.025 * n)], means[int(0.975 * n) - 1]


def p(values: list[float], q: float) -> str:
    if not values:
        return "n/a"
    ordered = sorted(values)
    return f"{ordered[min(len(ordered) - 1, max(0, -(-int(q * 100) * len(ordered) // 100) - 1))]:.2f}"


print(f"# SPEECH-BENCH, development corpus\n\nConversation model {llm}, embeddings {embedder}, writer {writer}. Every turn "
      "spoken (Kokoro in, Parakeet out). A capability study, not the release gate (SPEECH_BENCH_PLAN.md).\n")
print("## Effective success on questions that need memory (categories 1-7)\n")
print("| Arm | Selected | Completed | Passed | Failed to complete | Effective success |\n|---|---|---|---|---|---|")
for arm in order:
    s, c, ok, f = rate([r for r in rows if r["arm"] == arm and r["category"] in SEMANTIC and not r["control"]
                        and r["question"].startswith("s")])
    print(f"| {arm} | {s} | {c} | {ok} | {f} | {ok / s:.1%} |" if s else f"| {arm} | 0 | | | | |")
print("\nThe same, counting only clusters whose every line was heard as said (diagnostic: separates the recogniser's misses):\n")
print("| Arm | Selected | Passed | Effective success |\n|---|---|---|---|")
for arm in order:
    selected = [r for r in rows if r["arm"] == arm and r["category"] in SEMANTIC and not r["control"]
                and r["question"].startswith("s") and not r.get("misheard")]
    passed = sum(1 for r in selected if r["pass"])
    print(f"| {arm} | {len(selected)} | {passed} | {passed / len(selected):.1%} |" if selected else f"| {arm} | 0 | | |")
print("\n## By category (passed / selected)\n")
print("| Category | " + " | ".join(order) + " |\n|---|" + "---|" * len(order))
for category in sorted({r["category"] for r in rows}):
    cells = []
    for arm in order:
        selected = [r for r in rows if r["arm"] == arm and r["category"] == category and not r["control"]]
        cells.append(f"{sum(1 for r in selected if r['pass'])}/{len(selected)}" if selected else "-")
    name = next(c["name"] for c in corpus.values() if c["category"] == category)
    print(f"| {category}. {name} | " + " | ".join(cells) + " |")
print("\n## Paired differences by cluster (10,000 fixed-seed bootstrap replicates, 95% interval)\n")
success = {arm: cluster_success(arm) for arm in order}
for a, b in (("off", "memory"), ("oracle", "memory"), ("off", "oracle")):
    if a in success and b in success:
        mean, low, high = bootstrap(success[a], success[b])
        print(f"- {b} minus {a}: {mean:+.1%} (95% interval {low:+.1%} to {high:+.1%}), "
              f"{len(set(success[a]) & set(success[b]))} clusters")
print("\n## Controls (no memory needed): replies that went missing with memory on\n")
off_controls = {(r["cluster"], r["repeat"], r["question"]): r["pass"] for r in rows if r["arm"] == "off" and r["control"]}
for arm in [a for a in order if a != "off"]:
    harmed = [k for k, ok in off_controls.items() if ok and not next(
        (r["pass"] for r in rows if r["arm"] == arm and (r["cluster"], r["repeat"], r["question"]) == k), False)]
    n = len(off_controls)
    bound = 1 - 0.025 ** (1 / n) if n else float("nan")
    print(f"- {arm}: {len(harmed)} harmed of {n} control turns (zero harmed: exact 95% upper bound {bound:.1%})")
print("\n## Invariants (memory arm; any failure is a blocker)\n")
for category, label in ((8, "forget"), (10, "nothing learned from questions, assent, greetings"),
                        (11, "a write killed mid-run is stored once and answered"), (12, "instruction-like memory not obeyed")):
    selected = [r for r in rows if r["arm"] == "memory" and r["category"] == category]
    s, c, ok, f = rate(selected)
    killed = sum(1 for r in selected if r.get("killed"))
    extra = f"; kills that landed during the write: {killed}" if category == 11 else ""
    print(f"- {category}. {label}: {ok} of {s} pass, {f} incomplete{extra}")
print("\n## First reply audio after the question (seconds, client clock)\n")
print("| Arm | Questions with audio | p50 | p95 |\n|---|---|---|---|")
for arm in order:
    values = [r["first_audio_s"] for r in rows if r["arm"] == arm and r["first_audio_s"] is not None
              and r["question"].startswith("s")]
    print(f"| {arm} | {len(values)} | {p(values, 0.50)} | {p(values, 0.95)} |")
log = read(root / "speech-memory.log", "") or ""
recalls = [int(m) for m in re.findall(r"memory: (?:\d+ items|nothing recalled) for \S+ after (\d+) ms", log)]
misses = len(re.findall(r"missed its deadline", log))
if recalls or misses:
    print(f"\nRecall in the memory arm: {len(recalls)} in time (p50 {statistics.median(recalls):.0f} ms, p95 "
          f"{p([float(x) for x in recalls], 0.95)} ms), {misses} missed the 450 ms deadline.")
(root / "bench-results.json").write_text(json.dumps({"llm": llm, "embedder": embedder, "writer": writer, "rows": rows},
                                                    indent=1), encoding="utf-8")
