# SPDX-License-Identifier: Apache-2.0
"""The packaged verifier reaches the recorded verdict on the conformance vectors.

conformance/pack-v1.1/results.json holds, for every vector, the verdict both verifiers must reach. The vectors of kind
"pack" are a pack, a trust policy and revocation documents: exactly what ampack.verify() takes. The other kinds
(receipts, certificates, erasures, aggregates, bare documents) go through the command line's options and are run by the
kit itself (run_vectors.py); they are counted here and not run.
"""
import json
from pathlib import Path

import pytest

import ampack

KIT = Path(__file__).resolve().parents[3] / "conformance" / "pack-v1.1"
RESULTS = json.loads((KIT / "results.json").read_text(encoding="utf-8"))
CANDIDATES = [v for v in RESULTS["vectors"] if v["kind"] == "pack" and "location" not in v and "document" not in v]
# One vector's policy holds a private key and is deliberately not in the repository. It cannot be run here, and a missing
# file must not count as the refusal it expects.
PLAIN = [v for v in CANDIDATES if (KIT / v["policy"]).is_file()]


def test_the_kit_is_whole():
    assert RESULTS["agreed"] == RESULTS["total"] == len(RESULTS["vectors"])
    assert len(PLAIN) > 300, "most vectors are plain packs"
    assert [v["vector"] for v in CANDIDATES if v not in PLAIN] == ["policy-with-a-private-key"]


@pytest.mark.parametrize("vector", PLAIN, ids=lambda v: v["vector"])
def test_vector(vector):
    pack = KIT / "vectors" / (vector["vector"] + ".ampack")
    trust = KIT / vector["policy"]
    revocations = [KIT / name for name in vector.get("revocations", [])]
    try:
        ampack.verify(pack, trust, revocations)
        verdict = "verified"
    except ampack.Refused:
        verdict = "refused"
    assert verdict == vector["expected"]
