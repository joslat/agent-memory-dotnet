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


def case(conf, cap1, cap2, first, second, spacing=8):
    """Spacing 8 or more: tasks independent; closer, the measurement-level bounds cluster them (review 6, H4)."""
    return {"confidence": conf, "cap1": cap1, "cap2": cap2, "spacing": spacing, "first": first, "second": second,
            "rateLower": r.rate_lower(sum(1 for _, c in first if c > 0), len(first), conf),
            "precisionLower": r.precision_lower(first, cap1, conf), "precisionUpper": r.precision_upper(first, cap1, conf),
            "hitRateLowerOf": r.hit_rate_lower_arm(first, spacing, conf), "precisionLowerOf": r.precision_lower_arm(first, cap1, spacing, conf),
            "precisionDifferenceLower": r.precision_diff_lower_arm(first, cap1, second, cap2, spacing, conf),
            "hitDifferenceLower": r.hit_diff_lower_arm(first, second, spacing, conf)}


random.seed(29)
cases = []
for conf in (0.9, 0.95, 0.975, 0.99):
    for n, cap, p in ((10, 3, .5), (40, 3, .35), (40, 16, .2), (120, 4, .6), (200, 3, .9), (12, 1, .7)):
        cases.append(case(conf, cap, 3, tasks(n, cap, p), tasks(n, 3, p * 0.7)))
cases.append(case(0.95, 3, 3, [(3, 3)] * 40, [(3, 3)] * 40))
cases.append(case(0.95, 3, 3, [(0, 0)] * 20, [(2, 1)] * 20))
# Review 6: a baseline that offers nothing by design (cap 0) and a host that could offer but happened not to (cap 3) differ.
silent = tasks(40, 3, .4)
cases.append(case(0.95, 3, 0, silent, [(0, 0)] * 40))
cases.append(case(0.95, 3, 3, silent, [(0, 0)] * 40))
cases.append(case(0.99, 1, 1, tasks(60, 1, .8), tasks(60, 1, .5)))
# Review 6, H4: tasks drawn one, two and three eligible commits apart, clustered in runs of 8, 4 and 3.
for spacing in (1, 2, 3):
    for conf in (0.95, 0.99):
        cases.append(case(conf, 3, 3, tasks(40, 3, .45), tasks(40, 3, .3), spacing))
        cases.append(case(conf, 3, 0, tasks(41, 3, .6), [(0, 0)] * 41, spacing))
Path(__file__).with_name("golden.json").write_text(json.dumps(cases, indent=0), encoding="utf-8")
print(len(cases), "cases")
