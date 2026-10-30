"""Exact coverage as the statistics test (review 7, I6: the guard was Monte Carlo with ceilings too wide to catch a miss of
round 6's size). Each script enumerates every outcome of n tasks over the sufficient statistics, for a grid of regimes with the
edges and random ones, and prints the regimes whose target at the truth passes most often, worst first. A bound passes the
check when its worst regime passes at most the nominal rate, 1 - confidence. Exits 1 when one does not.

    exact_precision.py      the one-arm precision bound (cap > 1)
    exact_pdiff.py          the paired precision-difference bound, the upgrade chain's no-regression check included
    exact_pdiff_rare.py     the same, where two arms agree on most tasks and differ on a rare one
The paired hit difference is held exactly by TrialStatisticsTests, and cap 1 precision equals Clopper-Pearson by test.

usage: python coverage.py <repository root>
"""
import re
import subprocess
import sys
from pathlib import Path

ROOT = sys.argv[1]
HERE = Path(__file__).resolve().parent
RUNS = [
    ("one-arm precision, 10 tasks, cap 3", "exact_precision.py", ["10", "0.95", "3", "300"], 0.95),
    ("one-arm precision, 10 tasks, cap 3", "exact_precision.py", ["10", "0.99", "3", "300"], 0.99),
    ("one-arm precision, 30 tasks, cap 2", "exact_precision.py", ["30", "0.95", "2", "150"], 0.95),
    ("paired precision difference, 20 tasks, caps 3/3", "exact_pdiff.py", ["20", "0.95", "3", "3", "40", "7"], 0.95),
    ("paired precision difference, 20 tasks, caps 3/3", "exact_pdiff.py", ["20", "0.99", "3", "3", "40", "7"], 0.99),
    ("paired difference, a rare loss, 10 and 20 tasks", "exact_pdiff_rare.py", ["0.95", "10,20"], 0.95),
    ("paired difference, a rare loss, 10 and 20 tasks", "exact_pdiff_rare.py", ["0.99", "10,20"], 0.99),
]

failed = 0
for name, script, args, conf in RUNS:
    run = subprocess.run([sys.executable, "-I", str(HERE / script), ROOT, *args], capture_output=True, text=True, encoding="utf-8")
    rates = [float(m) for m in re.findall(r"^\s+pass (\d\.\d+)", run.stdout, re.M)]
    if run.returncode != 0 or not rates:
        print(f"ERROR {name} at {conf}: {script} did not run\n{run.stdout[-2000:]}{run.stderr[-2000:]}")
        failed += 1
        continue
    worst, nominal = max(rates), 1 - conf
    ok = worst <= nominal + 1e-9
    failed += not ok
    print(f"{'ok  ' if ok else 'OVER'} {name} at {conf}: worst {worst:.4f}, nominal {nominal:.3f}")
print(f"{len(RUNS) - failed}/{len(RUNS)} bounds pass a target at the truth at most their nominal rate, exactly over every outcome")
sys.exit(1 if failed else 0)
