"""Conformance vectors for memory pack format v1.1, checked by two independent verifiers.

Builds a valid pack with `ampack`, derives tampered and ambiguous variants with a separate Python writer, builds one pack
entirely in Python (canonical list, DSSE signature, strict ZIP), and requires `ampack verify` (.NET) and
python/ampack_verify.py to reach the expected outcome on every vector. Writes vectors/, trust.json and results.json.

usage: python run_vectors.py <path to Ampack executable> [<out folder>]
"""
import base64
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import zlib
from datetime import datetime, timezone
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / "python"))
import ampack_verify as py  # noqa: E402

AMPACK = sys.argv[1]
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else HERE
VECTORS = OUT / "vectors"


def ampack(*args):
    r = subprocess.run([AMPACK, *args], capture_output=True, text=True, encoding="utf-8")
    return r.returncode, (r.stdout + r.stderr).strip()


def write_zip(files, order=None, mutate=None):
    """A strict-profile writer, independent of the .NET one: stored entries, sorted, fixed time, no extras."""
    out, central = bytearray(), []
    for name in (order or sorted(files)):
        data, nb = files[name], name.encode("ascii")
        crc, off = zlib.crc32(data) & 0xFFFFFFFF, len(out)
        out += b"PK\x03\x04" + struct.pack("<HHHHHIIIHH", 10, 0, 0, 0, 0x21, crc, len(data), len(data), len(nb), 0) + nb + data
        central.append((nb, crc, len(data), off))
    start = len(out)
    for nb, crc, size, off in central:
        out += b"PK\x01\x02" + struct.pack("<HHHHHHIIIHHHHHII", 20, 10, 0, 0, 0, 0x21, crc, size, size, len(nb), 0, 0, 0, 0, 0, off) + nb
    out += b"PK\x05\x06" + struct.pack("<HHHHIIH", 0, 0, len(central), len(central), len(out) - start, start, 0)
    return bytes(mutate(out) if mutate else out)


def files_list(pid, version, content):
    entries = [{"path": p, "length": len(b), "sha256": hashlib.sha256(b).hexdigest()} for p, b in sorted(content.items())]
    return py.canonical({"format": py.FILES_FORMAT, "id": pid, "version": version, "files": entries}).encode()


def sign(list_bytes, pid, version, key, role="publisher"):
    statement = py.canonical({
        "_type": py.STATEMENT,
        "subject": [{"name": py.FILES, "digest": {"sha256": hashlib.sha256(list_bytes).hexdigest()}}],
        "predicateType": f"urn:agentmemory:pack:{role}:v1",
        "predicate": {"id": pid, "version": version, "role": role, "signedAt": "2026-10-26T12:00:00Z"},
    }).encode()
    spki = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    sig = key.sign(py.pae(statement), ec.ECDSA(hashes.SHA256()))
    envelope = py.canonical({"payloadType": py.PAYLOAD_TYPE, "payload": base64.b64encode(statement).decode(),
                             "signatures": [{"keyid": hashlib.sha256(spki).hexdigest(), "sig": base64.b64encode(sig).decode()}]}).encode()
    return f"{py.SIGNATURES}{role}-{hashlib.sha256(spki).hexdigest()[:16]}.dsse.json", envelope


def main():
    if VECTORS.exists():
        shutil.rmtree(VECTORS)
    VECTORS.mkdir(parents=True)
    work = OUT / ".work"
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir()
    code, text = ampack("keygen", "--out", str(work / "keys"), "--prefix", "io.example")
    assert code == 0, text
    entry = json.loads(text[text.index("{"):])
    pem = (work / "keys" / "publisher.key.pem").read_bytes()
    key = serialization.load_pem_private_key(pem, password=None)
    policy = {"format": "agentmemory.pack.trust/1.1", "keys": [entry]}
    (OUT / "trust.json").write_text(json.dumps(policy, separators=(",", ":")), encoding="utf-8")
    other_scope = {"format": "agentmemory.pack.trust/1.1", "keys": [dict(entry, idPrefixes=["io.other"])]}
    (OUT / "trust-other-scope.json").write_text(json.dumps(other_scope, separators=(",", ":")), encoding="utf-8")

    assert ampack("init", str(work / "notes"), "--id", "io.example.notes", "--version", "1.0.0")[0] == 0
    code, text = ampack("build", str(work / "notes"), "--key", str(work / "keys" / "publisher.key.pem"), "--out", str(VECTORS / "valid-dotnet.ampack"))
    assert code == 0, text
    base = py.read_zip((VECTORS / "valid-dotnet.ampack").read_bytes())
    content = {p: b for p, b in base.items() if p != py.FILES and not p.startswith(py.SIGNATURES)}
    pid, version = "io.example.notes", "1.0.0"

    def signed(content_, pid_=pid, version_=version):
        lb = files_list(pid_, version_, content_)
        path, env = sign(lb, pid_, version_, key)
        return {**content_, py.FILES: lb, path: env}

    record = "records/note.jsonl"
    vectors = {}  # name -> (files or raw bytes, policy file, expected)
    vectors["valid-python-built"] = (signed(content), "trust.json", "verified")
    vectors["valid-python-stored-from-dotnet"] = (dict(base), "trust.json", "verified")
    vectors["tampered-record"] = ({**base, record: base[record].replace(b"Replace me", b"Replace me!")}, "trust.json", "refused")
    vectors["extra-file"] = ({**base, "records/extra.jsonl": b'{"key":"x"}\n'}, "trust.json", "refused")
    vectors["missing-file"] = ({p: b for p, b in base.items() if p != "ontology.json"}, "trust.json", "refused")
    forged = {**content, record: base[record].replace(b"Replace me", b"Forged")}
    vectors["relisted-with-old-signature"] = ({**forged, py.FILES: files_list(pid, version, forged),
                                               **{p: b for p, b in base.items() if p.startswith(py.SIGNATURES)}}, "trust.json", "refused")
    pretty = json.dumps(json.loads(base[py.FILES]), indent=1).encode()
    vectors["non-canonical-list"] = ({**base, py.FILES: pretty}, "trust.json", "refused")
    vectors["key-scoped-elsewhere"] = (dict(base), "trust-other-scope.json", "refused")
    vectors["crlf-record"] = (signed({**content, record: content[record].replace(b"\n", b"\r\n")}), "trust.json", "refused")
    vectors["bom-manifest"] = (signed({**content, "pack.json": b"\xef\xbb\xbf" + content["pack.json"]}), "trust.json", "refused")
    vectors["duplicate-key-record"] = (signed({**content, record: b'{"key":"a","key":"b"}\n'}), "trust.json", "refused")
    vectors["device-name-path"] = (signed({**content, "records/CON.jsonl": b'{"key":"c"}\n'}), "trust.json", "refused")
    vectors["pack-json-names-other-id"] = (signed(content, pid_="io.example.other"), "trust.json", "refused")

    def manifest_with(**fields):
        m = json.loads(content["pack.json"])
        m.update(fields)
        return {**content, "pack.json": (json.dumps(m, indent=2, ensure_ascii=False) + chr(10)).encode("utf-8")}
    # PACK-strict: a field the format does not define refuses the pack; inside "extensions" it is ignored unless critical.
    vectors["unknown-field"] = (signed(manifest_with(homepage="https://example.com")), "trust.json", "refused")
    vectors["extension-ignored"] = (signed(manifest_with(extensions={"io.example.cache": {"ttl": 3600}})), "trust.json", "verified")
    vectors["critical-extension"] = (signed(manifest_with(extensions={"io.example.cache": {"ttl": 3600}, "critical": ["io.example.cache"]})),
                                     "trust.json", "refused")
    vectors["critical-names-nothing"] = (signed(manifest_with(extensions={"critical": ["io.example.absent"]})), "trust.json", "refused")

    raw = {}
    good = write_zip(signed(content))
    raw["prefix-bytes"] = (b"MZ-prefix" + good, "trust.json", "refused")
    raw["trailing-bytes"] = (good + b"\x00", "trust.json", "refused")
    raw["archive-comment"] = (good[:-2] + struct.pack("<H", 2) + b"hi", "trust.json", "refused")
    s = signed(content)
    raw["unsorted-entries"] = (write_zip(s, order=sorted(s, reverse=True)), "trust.json", "refused")

    def flag_descriptor(b):
        b = bytearray(b)
        b[6:8] = struct.pack("<H", 8)
        cd = struct.unpack("<I", b[-6:-2])[0]
        b[cd + 8:cd + 10] = struct.pack("<H", 8)
        return b
    raw["data-descriptor-flag"] = (write_zip(s, mutate=flag_descriptor), "trust.json", "refused")

    def local_name(b):
        b = bytearray(b)
        b[30] = ord("X")  # the first entry's local name only
        return b
    raw["local-name-differs"] = (write_zip(s, mutate=local_name), "trust.json", "refused")

    results, failures = [], 0
    for name, (files, policy_file, expected) in {**{k: (write_zip(v[0]), v[1], v[2]) for k, v in vectors.items()}, **raw}.items():
        path = VECTORS / f"{name}.ampack"
        path.write_bytes(files)
        dn_code, dn_text = ampack("verify", str(path), "--policy", str(OUT / policy_file))
        pr = subprocess.run([sys.executable, "-I", str(HERE / "python" / "ampack_verify.py"), str(path), str(OUT / policy_file)],
                            capture_output=True, text=True, encoding="utf-8")
        dotnet = "verified" if dn_code == 0 else "refused"
        python = "verified" if pr.returncode == 0 else "refused"
        agree = dotnet == python == expected
        failures += not agree
        results.append({"vector": name, "expected": expected, "dotnet": dotnet, "python": python, "agree": agree,
                        "dotnetSays": dn_text.splitlines()[0] if dn_text else "", "pythonSays": pr.stdout.strip().splitlines()[0] if pr.stdout.strip() else pr.stderr.strip()[-200:],
                        "sha256": hashlib.sha256(files).hexdigest()})
        print(f"{'ok  ' if agree else 'FAIL'} {name:34} expected {expected:8} dotnet {dotnet:8} python {python}")
    (OUT / "results.json").write_text(json.dumps({"format": "agentmemory.pack.conformance/1.1", "vectors": results,
                                                  "agreed": len(results) - failures, "total": len(results)}, indent=1) + "\n", encoding="utf-8")
    shutil.rmtree(work, ignore_errors=True)
    print(f"{len(results) - failures}/{len(results)} vectors: both verifiers agree with the expected outcome")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
