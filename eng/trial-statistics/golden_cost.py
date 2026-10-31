"""Golden cost decisions (feature 05 review, M3): the reference's cost bound (exact.cost_bound) on sampled trials, each at targets
around its bound, so the C# port (PackTrialGate.CostCheck) is held to the same verdicts. Writes golden-cost.json; the test
TrialGateTests.The_cost_check_reaches_the_reference_s_verdicts reads it.

Each case is a trial: per task, the baseline's bytes and the pack's; the spacing; the confidence (null for the estimate); the
target's kind (bytes or a ratio in millionths); and, per target, the bound the reference shows and whether it is met.

usage: python golden_cost.py [--check]
"""
import json
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import exact  # noqa: E402

HERE = Path(__file__).resolve().parent
ONE = 1_000_000


def floor_div(a, b):
    return a // b


def savings(pack, baseline, kind):
    out = []
    for p, b in zip(pack, baseline):
        s = b - p
        out.append(s if kind == "bytes" else floor_div(s * ONE, max(1, b)))
    return out


def main():
    rng = random.Random(20261031)
    cases = []
    for _ in range(240):
        n = rng.choice([10, 11, 17, 24, 40, 41, 63, 100])
        spacing = rng.choice([1, 2, 3, 5, 7, 8, 9, 20])
        conf = rng.choice([None, 0.9, 0.95, 0.975, 0.99])
        kind = rng.choice(["bytes", "ratio"])
        baseline = [rng.choice([0, 0, 1, 50, 500, 1000, 1500, 4000]) if rng.random() < 0.3 else rng.randint(0, 3000) for _ in range(n)]
        pack = [max(0, b + rng.randint(-1500, 900)) if rng.random() < 0.85 else rng.randint(0, 5000) for b in baseline]
        if rng.random() < 0.15:
            pack = [b for b in baseline]  # every saving zero: ties at a target of 0
        values = savings(pack, baseline, kind)
        bound = exact.cost_bound(values, spacing, conf)
        targets = sorted({0, 1, max(0, (bound or 0) - 1), max(0, bound or 0), max(0, (bound or 0) + 1), rng.randint(0, ONE if kind == "ratio" else 2000)})
        decisions = []
        for t in targets:
            shown = t - 1 if bound is None else bound
            decisions.append([t, shown, shown >= t])
        cases.append({"spacing": spacing, "confidence": conf, "kind": kind, "baseline": baseline, "pack": pack, "decisions": decisions})
    text = json.dumps({"format": "agentmemory.trial.golden-cost/1", "cases": cases}, separators=(",", ":")) + "\n"
    if "--check" in sys.argv:
        same = (HERE / "golden-cost.json").read_text(encoding="utf-8") == text
        print("golden-cost.json is exact.py's output" if same else "golden-cost.json differs from exact.py's output: run golden_cost.py")
        return 0 if same else 1
    (HERE / "golden-cost.json").write_text(text, encoding="utf-8")
    print(f"{len(cases)} cases, {sum(len(c['decisions']) for c in cases)} decisions")
    return 0


if __name__ == "__main__":
    sys.exit(main())
