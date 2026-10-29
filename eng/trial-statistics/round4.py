"""A faithful port of the round-4 bounds (PackTrialGate at 0396d791), kept to show what the harness found (compare.py)."""
import math
import numpy as np
from tq import Z, student_q


def q_of(conf, df):
    return student_q(Z[conf], df)


def wilson_lower(p, n, z):
    if n <= 0:
        return 0.0
    z2 = z * z
    centre = p + z2 / (2 * n)
    margin = z * math.sqrt(max(0.0, p * (1 - p) / n + z2 / (4 * n * n)))
    return max(0.0, (centre - margin) / (1 + z2 / n))


def wilson_upper(p, n, z):
    return 1.0 if n <= 0 else 1 - wilson_lower(1 - p, n, z)


def left_out(o, c):
    off, cor = o.sum(), c.sum()
    whole = 0.0 if off == 0 else cor / off
    rest = off - o
    return np.where(rest > 0, (cor - c) / np.where(rest > 0, rest, 1), whole)


def clustered_bounds(o, c, q):
    offered = o.sum()
    if offered == 0:
        return None, 0.0
    p = c.sum() / offered
    ans = o > 0
    over = wilson_lower(float(np.mean(c[ans] / o[ans])), int(ans.sum()), q)
    if ans.sum() < 2 or p in (0.0, 1.0):
        return None, over
    left = left_out(o, c)
    n = len(o)
    jack = (n - 1) / n * ((left - left.mean()) ** 2).sum()
    de = max(1.0, jack / (p * (1 - p) / offered))
    return wilson_lower(p, offered / de, q), over


def precision_lower(o, c, conf):
    q = q_of(conf, int((o > 0).sum()) - 1)
    items, over = clustered_bounds(o, c, q)
    return over if items is None else min(items, over)


def effective_lower(o, c, q):
    items, over = clustered_bounds(o, c, q)
    return over if items is None else items


def precdiff_lower(o1, c1, o2, c2, conf):
    z = Z[conf]
    if o2.sum() == 0:
        return 0.0 if o1.sum() == 0 else precision_lower(o1, c1, conf)
    if o1.sum() == 0:
        return -(1 - precision_lower(o2, o2 - c2, conf))
    r1, r2 = c1.sum() / o1.sum(), c2.sum() / o2.sum()
    n = len(o1)
    q = q_of(conf, int(((o1 > 0) | (o2 > 0)).sum()) - 1)
    l1 = effective_lower(o1, c1, q)
    u2 = 1 - effective_lower(o2, o2 - c2, q)
    s1, s2 = o1.sum(), o2.sum()
    res1, res2 = (c1 - r1 * o1) / s1, (c2 - r2 * o2) / s2
    v1, v2 = (res1 ** 2).sum(), (res2 ** 2).sum()
    rho = np.clip((res1 * res2).sum() / math.sqrt(v1 * v2), -1, 1) if v1 > 0 and v2 > 0 else 0.0
    newcombe = r1 - r2 - math.sqrt(max(0.0, (r1 - l1) ** 2 - 2 * rho * (r1 - l1) * (u2 - r2) + (u2 - r2) ** 2))
    d = left_out(o1, c1) - left_out(o2, c2)
    paired = r1 - r2 - q * math.sqrt((n - 1) / n * ((d - d.mean()) ** 2).sum())
    return min(newcombe, paired)


def hitdiff_lower(h1, h2, conf):
    z = Z[conf]
    n = len(h1)
    p1, p2 = h1.mean(), h2.mean()
    l1, u2 = wilson_lower(p1, n, z), wilson_upper(p2, n, z)
    both = int(((h1 > 0) & (h2 > 0)).sum()); f = int(((h1 > 0) & (h2 == 0)).sum())
    s = int(((h1 == 0) & (h2 > 0)).sum()); nn = n - both - f - s
    den = math.sqrt((both + f) * (s + nn) * (both + s) * (f + nn))
    phi = 0.0 if den == 0 else (both * nn - f * s) / den
    return p1 - p2 - math.sqrt(max(0.0, (p1 - l1) ** 2 - 2 * phi * (p1 - l1) * (u2 - p2) + (u2 - p2) ** 2))


def rate_lower(h, conf):
    return wilson_lower(h.mean(), len(h), Z[conf])
