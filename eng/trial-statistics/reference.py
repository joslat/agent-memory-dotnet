"""The reference for the hedged trial bounds (statistic agentmemory.trial.hedged/2026-10-29); the C# port in PackTrialGate must agree (golden.json)."""
import math
from tq import Z, student_q


def rate_lower(x, n, conf):
    """Clopper-Pearson: the rate at which P(X >= x) = 1 - conf; 0 for no successes."""
    if n <= 0 or x <= 0:
        return 0.0
    d = 1 - conf
    logc = [0.0] * (n + 1)
    for j in range(1, n + 1):
        logc[j] = logc[j - 1] + math.log(n - j + 1) - math.log(j)
    lo, hi = 0.0, 1.0
    for _ in range(64):
        p = (lo + hi) / 2
        lp, lq = math.log(p), math.log1p(-p)
        terms = [logc[j] + j * lp + (n - j) * lq for j in range(x, n + 1)]
        m = max(terms)
        tail = math.exp(m) * sum(math.exp(t - m) for t in terms)
        if tail > d:
            hi = p
        else:
            lo = p
    return lo


def _mean(v):
    return sum(v) / len(v)


def _cov(a, b):
    ma, mb = _mean(a), _mean(b)
    return sum((x - ma) * (y - mb) for x, y in zip(a, b)) / (len(a) - 1)


def precision_lower(tasks, cap, conf):
    """tasks: [(offered, correct)]; one unseen task offering `cap` items, all wrong, beside them; Fieller at t(n - 1)."""
    n = len(tasks)
    if n == 0 or sum(o for o, _ in tasks) == 0:
        return 0.0
    o = [float(t[0]) for t in tasks] + [float(cap)]
    c = [float(t[1]) for t in tasks] + [0.0]
    N = n + 1
    ob, cb = _mean(o), _mean(c)
    r = cb / ob
    soo, scc, sco = _cov(o, o), _cov(c, c), _cov(o, c)
    q = student_q(Z[conf], n - 1)
    a = N * ob * ob - q * q * soo
    b = -2 * N * cb * ob + 2 * q * q * sco
    cc = N * cb * cb - q * q * scc
    if a <= 0:
        return 0.0
    disc = b * b - 4 * a * cc
    if disc < 0:
        return min(max(r, 0.0), 1.0)
    root = math.sqrt(disc)
    lo = 2 * cc / (-b + root) if -b + root > 0 else (-b - root) / (2 * a)
    return min(max(lo, 0.0), r, 1.0)


def precision_upper(tasks, cap, conf):
    return 1 - precision_lower([(o, o - c) for o, c in tasks], cap, conf)


def precision_diff_lower(t1, cap1, t2, cap2, conf):
    """R1 - R2, paired: one unseen task where the first offers cap1 all wrong and the second cap2 all right."""
    if sum(o for o, _ in t2) == 0:
        return 0.0 if sum(o for o, _ in t1) == 0 else precision_lower(t1, cap1, conf)
    if sum(o for o, _ in t1) == 0:
        return -precision_upper(t2, cap2, conf)
    n = len(t1)
    o1 = [float(o) for o, _ in t1] + [float(cap1)]; c1 = [float(c) for _, c in t1] + [0.0]
    o2 = [float(o) for o, _ in t2] + [float(cap2)]; c2 = [float(c) for _, c in t2] + [float(cap2)]
    N = n + 1
    r1, r2 = sum(c1) / sum(o1), sum(c2) / sum(o2)
    m1, m2 = _mean(o1), _mean(o2)
    f = [(c1[i] - r1 * o1[i]) / m1 - (c2[i] - r2 * o2[i]) / m2 for i in range(N)]
    se = math.sqrt(_cov(f, f) / N)
    q = student_q(Z[conf], n - 1)
    return min(max(r1 - r2 - q * se, -1.0), 1.0)


def hit_diff_lower(t1, t2, conf):
    """Hit rate 1 - hit rate 2, paired: one unseen task the first misses and the second hits."""
    n = len(t1)
    d = [(1.0 if a[1] > 0 else 0.0) - (1.0 if b[1] > 0 else 0.0) for a, b in zip(t1, t2)] + [-1.0]
    N = n + 1
    se = math.sqrt(_cov(d, d) / N)
    q = student_q(Z[conf], n - 1)
    return min(max(_mean(d) - q * se, -1.0), 1.0)
