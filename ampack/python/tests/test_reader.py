# SPDX-License-Identifier: Apache-2.0
"""Reading a verified pack: the manifest, the records, a set's members, and what a line must look like."""
import json
import subprocess
import sys
from datetime import datetime
from pathlib import Path

import pytest

import ampack
from ampack import _reader

REPOSITORY = Path(__file__).resolve().parents[3]
KIT = REPOSITORY / "conformance" / "pack-v1.1"
VECTORS = KIT / "vectors"
TRUST = KIT / "trust.json"
RESULTS = json.loads((KIT / "results.json").read_text(encoding="utf-8"))


def first(kind_of_pack):
    """The first plain vector that verifies under trust.json and is of this kind."""
    for v in RESULTS["vectors"]:
        if v["kind"] == "pack" and v["expected"] == "verified" and v["policy"] == "trust.json" and not v.get("revocations") \
                and "location" not in v:
            pack = ampack.load(VECTORS / (v["vector"] + ".ampack"), TRUST)
            if pack.kind == kind_of_pack:
                return pack
    pytest.skip(f"the kit has no plain verified {kind_of_pack} vector")


def test_a_knowledge_pack_reads():
    pack = ampack.load(VECTORS / "valid-python-built.ampack", TRUST)
    assert (pack.id, pack.version, pack.kind) == ("io.example.notes", "1.0.0", "knowledge")
    assert pack.digest.startswith("sha256:") and len(pack.digest) == 71
    assert ampack.verify(VECTORS / "valid-python-built.ampack", TRUST) == ampack.Verdict(pack.id, pack.version, pack.digest)
    records = list(pack.records())
    assert records, "a knowledge pack holds records"
    assert all(isinstance(r.type, str) and isinstance(r.key, str) for r in records)
    assert records[0].file in pack.record_files and records[0].line == 1
    with pytest.raises(TypeError):
        pack.files["pack.json"] = b"{}"      # what was verified cannot be changed


def test_the_files_are_the_ones_the_digest_covers():
    """The file list, the signatures and the attestations travel beside the content. This vector verifies and carries a
    certificate nobody checked: it must not come out as a file of the verified pack."""
    pack = ampack.load(VECTORS / "attestation-certificate-content-unchecked.ampack", TRUST)
    assert "pack.json" in pack.files
    assert [p for p in pack.files if p.startswith("META/")] == []
    for v in RESULTS["vectors"]:
        plain = v["kind"] == "pack" and v["policy"] == "trust.json" and not v.get("revocations") and "location" not in v
        if plain and v["expected"] == "verified":
            loaded = ampack.load(VECTORS / (v["vector"] + ".ampack"), TRUST)
            for each in (loaded, *loaded.members):
                beside = [p for p in each.files
                          if p == "META/files.json" or p.startswith(("META/signatures/", "META/attestations/"))]
                assert beside == [], f"{v['vector']}: {beside}"


def test_bytes_are_taken_as_well_as_paths():
    pack = ampack.load((VECTORS / "valid-python-built.ampack").read_bytes(), TRUST.read_bytes())
    assert pack.id == "io.example.notes"


def test_a_set_gives_its_members():
    pack = first("set")
    assert pack.members and list(pack.records()) == []
    assert sorted(m.id for m in pack.members) == sorted(pack.manifest["contains"])
    assert all(m.kind != "set" for m in pack.members)


def test_nothing_comes_out_of_a_refused_pack():
    with pytest.raises(ampack.Refused):
        ampack.load(VECTORS / "tampered-record.ampack", TRUST)


def test_the_moment_needs_a_time_zone():
    with pytest.raises(ValueError):
        ampack.verify(VECTORS / "valid-python-built.ampack", TRUST, at=datetime(2026, 11, 19))


@pytest.mark.parametrize("line, problem", [
    ('[1]', "a record is a JSON object"),
    ('{"type":"T"}', "needs a string type and key"),
    ('{"type":"T","key":1}', "needs a string type and key"),
    ('{"type":"T","key":"k","properties":[]}', "properties is an object"),
    ('{"type":"T","key":"k","links":[{"relation":"r"}]}', "links is a list"),
    ('{"type":"T","key":"k","links":[{"relation":"r","target":"t","more":1}]}', "links is a list"),
    ('{"type":"T","key":"k","colour":"red"}', "unknown field colour"),
])
def test_a_malformed_line_is_refused(line, problem):
    with pytest.raises(ampack.Refused, match=problem):
        _reader._record(json.loads(line), "records/x.jsonl", 3, "records/x.jsonl:3")


def test_a_line_keeps_the_format_s_other_fields():
    record = _reader._record(json.loads('{"type":"T","key":"k","validFrom":"2026-01-01T00:00:00Z","confidenceMicro":900000}'),
                             "records/x.jsonl", 1, "records/x.jsonl:1")
    assert dict(record.extra) == {"validFrom": "2026-01-01T00:00:00Z", "confidenceMicro": 900000}
    assert dict(record.properties) == {} and record.links == ()


def test_the_command_line_gives_the_kit_s_exit_codes():
    def run(*args):
        return subprocess.run([sys.executable, "-m", "ampack", *args], capture_output=True, text=True, encoding="utf-8")
    ok = run("verify", str(VECTORS / "valid-python-built.ampack"), str(TRUST))
    assert ok.returncode == 0 and ok.stdout.startswith("verified io.example.notes 1.0.0 sha256:")
    refused = run("verify", str(VECTORS / "tampered-record.ampack"), str(TRUST))
    assert refused.returncode == 1 and refused.stdout.startswith("refused: ")
    assert run("build", "anything").returncode == 2
    assert run("--version").stdout.startswith("ampack 0.1.0")


def test_the_copies_are_the_sources():
    """The verifier and the registry in the package are byte for byte the repository's (sync_sources.py --check)."""
    check = subprocess.run([sys.executable, str(REPOSITORY / "ampack" / "python" / "sync_sources.py"), "--check"],
                           capture_output=True, text=True)
    assert check.returncode == 0, check.stdout
