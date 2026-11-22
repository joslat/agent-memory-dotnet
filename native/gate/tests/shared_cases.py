"""The shared gate's cases (native/gate/cases/*.json) as unittest tests: tiers A and B, run by the Python runner.

test_gate.py imports Test9SharedCases, so the cases run wherever the gate runs, after its own classes, once.

GATE_TIER=A runs the contract cases with no store; tier B (or unset: the target image, with its seeded store) runs them
again against the store, and the recall cases with them. Each case is one test, named by its id and numbered in file
order: the order matters once, because a failed login (store.wrong-password-is-reported-as-authentication) comes last.
The stub servers (native/gate/stubs.py) run as a child process, as every language's runner runs them.
"""
from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "runner"))
import gate_runner as gate  # noqa: E402

TIER = (os.environ.get("GATE_TIER") or "B").upper()


class Test9SharedCases(unittest.TestCase):
    """One test per case; see native/gate/cases/FORMAT.md."""

    @classmethod
    def setUpClass(cls):
        cls.stubs = gate.Stubs()
        cls.env = gate.Environment(TIER, cls.stubs.url)
        cls.fixtures = gate.fixtures()

    @classmethod
    def tearDownClass(cls):
        cls.stubs.close()


def _test_for(case: dict):
    def test(self):
        try:
            gate.run_case(case, self.env, self.fixtures, self.stubs)
        except gate.Skip as reason:
            self.skipTest(str(reason))
    test.__doc__ = f"{case['id']} ({case['source']})"
    return test


def add_cases(target: type, tier: str) -> None:
    for number, case in enumerate(gate.load_cases(tier), 1):
        name = "test_%03d_%s" % (number, case["id"].replace(".", "__").replace("-", "_"))
        setattr(target, name, _test_for(case))


add_cases(Test9SharedCases, TIER)

if __name__ == "__main__":
    unittest.main(verbosity=2)
