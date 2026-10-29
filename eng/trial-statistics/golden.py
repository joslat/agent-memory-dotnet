"""Writes golden.json: the reference's bounds on seeded trials and on the edges (every item right, nothing offered), which
TrialStatisticsTests holds the library's port to. usage: python golden.py"""
import json
import random
from pathlib import Path
import reference as r


def tasks(n, cap, p):
    out = []
    for _ in range(n):
        o = random.randint(0, cap)
        out.append((o, sum(1 for _ in range(o) if random.random() < p)))
    return out


def case(conf, cap1, cap2, first, second):
    return {"confidence": conf, "cap1": cap1, "cap2": cap2, "first": first, "second": second,
            "rateLower": r.rate_lower(sum(1 for _, c in first if c > 0), len(first), conf),
            "precisionLower": r.precision_lower(first, cap1, conf), "precisionUpper": r.precision_upper(first, cap1, conf),
            "precisionDifferenceLower": r.precision_diff_lower(first, cap1, second, cap2, conf),
            "hitDifferenceLower": r.hit_diff_lower(first, second, conf)}


random.seed(29)
cases = []
for conf in (0.9, 0.95, 0.975, 0.99):
    for n, cap, p in ((10, 3, .5), (40, 3, .35), (40, 16, .2), (120, 4, .6), (200, 3, .9), (12, 1, .7)):
        cases.append(case(conf, cap, 3, tasks(n, cap, p), tasks(n, 3, p * 0.7)))
cases.append(case(0.95, 3, 3, [(3, 3)] * 40, [(3, 3)] * 40))
cases.append(case(0.95, 3, 3, [(0, 0)] * 20, [(2, 1)] * 20))
Path(__file__).with_name("golden.json").write_text(json.dumps(cases, indent=0), encoding="utf-8")
print(len(cases), "cases")
