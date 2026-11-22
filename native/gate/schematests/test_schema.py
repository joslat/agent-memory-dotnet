"""NATIVE-SCHEMA: the admin profile prepares an empty store from Python, with no .NET, and a second bootstrap changes nothing.

Standard library only. The store is fresh and unseeded (native/gate/store/store_gate.py, tier S). GATE_SCHEMA_PASS is
"first" (the store is empty, the bootstrap makes it complete) or "second" (it is complete, a bootstrap leaves it so);
the runner fingerprints the graph between the two passes, so "changes nothing" is checked on the graph itself.
Environment: AGENTMEMORY_NATIVE_LIBRARY, AMP_URI, AMP_USER, AMP_PASSWORD, GATE_SCHEMA_PASS.
"""
from __future__ import annotations

import os
import unittest

from agentmemory_native import MemoryClient, NativeCallError

DIMENSIONS = 8
PASS = os.environ.get("GATE_SCHEMA_PASS", "first")


def config(profile: str = "admin", **extra) -> dict:
    document = {
        "schemaVersion": 1,
        "profile": profile,
        "store": {"uri": os.environ["AMP_URI"], "username": os.environ["AMP_USER"], "password": os.environ["AMP_PASSWORD"]},
        "embedding": {"dimensions": DIMENSIONS, "model": "gate-unit-axes"},
    }
    document.update(extra)
    return document


class Test1Profile(unittest.TestCase):
    def test_admin_offers_the_schema_operations_only(self):
        with MemoryClient(config()) as admin:
            capabilities = admin.capabilities()
            recall = admin.call({"schemaVersion": 1, "operation": "memory_get_context", "sessionId": "s",
                                 "queryVector": [1.0] + [0.0] * (DIMENSIONS - 1)})
        self.assertEqual(capabilities["profile"], "admin")
        self.assertEqual(capabilities["operations"], ["capabilities_get", "schema_status", "schema_bootstrap"])
        self.assertEqual((recall["status"], recall["error"]["code"]), ("invalid", "unknown_operation"))

    def test_read_only_does_not_offer_the_schema(self):
        reader = config("read_only", owner={"mode": "fixed", "id": "gate-schema-owner"})
        reader["store"]["verifyConnectivity"] = False
        with MemoryClient(reader) as memory:
            refused = memory.call({"schemaVersion": 1, "operation": "schema_bootstrap"})
        self.assertEqual((refused["status"], refused["error"]["code"]), ("invalid", "unknown_operation"))


class Test2Bootstrap(unittest.TestCase):
    def test_1_the_bootstrap_makes_the_store_complete_at_the_configured_dimensions(self):
        with MemoryClient(config()) as admin:
            before = admin.schema_status()["schema"]
            after = admin.schema_bootstrap()
        if PASS == "first":
            self.assertTrue(before["empty"], before)
            self.assertFalse(before["complete"])
            self.assertEqual(len(before["missing"]), before["expected"])
        else:
            self.assertTrue(before["complete"], before)
        schema = after["schema"]
        self.assertEqual(after["status"], "ok")
        self.assertTrue(after["bootstrapped"])
        self.assertTrue(schema["complete"], schema["missing"])
        self.assertEqual(schema["missing"], [])
        self.assertTrue(schema["dimensionsMatch"])
        self.assertTrue(schema["vectorIndexes"])
        self.assertEqual(set(schema["vectorIndexes"].values()), {DIMENSIONS})
        print(f"\nMETRIC schema_objects={schema['expected']} vector_indexes={len(schema['vectorIndexes'])} pass={PASS}", flush=True)

    def test_2_a_status_read_reports_other_dimensions_as_a_mismatch(self):
        with MemoryClient(config(embedding={"dimensions": 1024, "model": "other"})) as admin:
            other = admin.schema_status()["schema"]
        self.assertFalse(other["dimensionsMatch"])
        self.assertEqual(other["dimensions"], 1024)


if __name__ == "__main__":
    try:
        unittest.main(verbosity=2)
    except NativeCallError as error:
        raise SystemExit(f"native call failed: {error}") from error
