"""The shared gate's tier C cases (native/gate/cases/writes.json) as unittest tests, on the write gate's own store.

test_writes.py imports Test9SharedWriteCases, so they run wherever the write gate runs, once.

The tests left in test_writes.py are the library's journal and crash behaviour and the LangGraph adapter, run by this
harness only (native/gate/REQUIRED.md).
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tests"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "runner"))
import gate_runner as gate  # noqa: E402
from shared_cases import _test_for  # noqa: E402


class Test9SharedWriteCases(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.stubs = gate.Stubs()
        cls.env = gate.Environment("C", cls.stubs.url)
        cls.fixtures = gate.fixtures()

    @classmethod
    def tearDownClass(cls):
        cls.stubs.close()


for number, case in enumerate(gate.load_cases("C"), 1):
    setattr(Test9SharedWriteCases, "test_%03d_%s" % (number, case["id"].replace(".", "__").replace("-", "_")), _test_for(case))

if __name__ == "__main__":
    unittest.main(verbosity=2)
