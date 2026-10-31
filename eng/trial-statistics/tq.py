"""Student's t quantiles as the library computes them: its table for 1 to 60 degrees of freedom (PackTrialGate.StudentQuantiles, generated from it) and the Cornish-Fisher tail beyond."""
import json
from pathlib import Path

# The registry's decimals (review F02, M7): one table for the float and the exact statistic, in Python and C#.
_STAT = json.loads((Path(__file__).resolve().parents[2] / "spec" / "registry.json").read_text(encoding="utf-8"))["trial"]["statistic"]["quantiles"]
Z = {int(k) / 1000: float(v["z"]) for k, v in _STAT.items()}
TABLE = {float(v["z"]): [float(t) for t in v["t"]] for v in _STAT.values()}
DECIMALS = {int(k) / 1000: v for k, v in _STAT.items()}


def student_q(z, df):
    v = max(1, df)
    if v <= 60:
        for k, row in TABLE.items():
            if abs(k - z) < 1e-6:
                return row[v - 1]
    z3, z5, z7 = z ** 3, z ** 5, z ** 7
    return z + (z3 + z) / (4 * v) + (5 * z5 + 16 * z3 + 3 * z) / (96 * v * v) + (3 * z7 + 19 * z5 + 17 * z3 - 15 * z) / (384 * v ** 3)
