"""Conformance vectors for memory pack format v1.2, checked by two independent verifiers.

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


def ampack(*args, env=None):
    r = subprocess.run([AMPACK, *args], capture_output=True, text=True, encoding="utf-8", errors="replace",
                       env={**os.environ, **env} if env else None)
    return r.returncode, (r.stdout + r.stderr).strip()


def write_deflated_zip(files, junk=None):
    """The strict profile with every entry deflated (method 8); `junk` maps a name to bytes appended after its deflate stream."""
    out, central = bytearray(), []
    for name in sorted(files):
        data, nb = files[name], name.encode("ascii")
        packer = zlib.compressobj(9, zlib.DEFLATED, -15)
        packed = packer.compress(data) + packer.flush() + (junk or {}).get(name, b"")
        crc, off = zlib.crc32(data) & 0xFFFFFFFF, len(out)
        out += b"PK\x03\x04" + struct.pack("<HHHHHIIIHH", 20, 0, 8, 0, 0x21, crc, len(packed), len(data), len(nb), 0) + nb + packed
        central.append((nb, crc, len(packed), len(data), off))
    start = len(out)
    for nb, crc, csize, size, off in central:
        out += b"PK\x01\x02" + struct.pack("<HHHHHHIIIHHHHHII", 20, 20, 0, 8, 0, 0x21, crc, csize, size, len(nb), 0, 0, 0, 0, 0, off) + nb
    out += b"PK\x05\x06" + struct.pack("<HHHHIIH", 0, 0, len(central), len(central), len(out) - start, start, 0)
    return bytes(out)


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
    # PACK-conformance-v12: a key's window and the revocations' freshness, as host policies.
    policies = {
        "trust-key-expired.json": {"keys": [dict(entry, notAfter="2020-01-01T00:00:00Z")]},
        "trust-key-not-yet-valid.json": {"keys": [dict(entry, notBefore="2099-01-01T00:00:00Z")]},
        "trust-key-window-open.json": {"keys": [dict(entry, notBefore="2020-01-01T00:00:00Z", notAfter="2099-01-01T00:00:00Z")]},
        "trust-revocations-stale.json": {"keys": [entry], "revoked": {"digests": [], "expires": "2020-01-01T00:00:00Z"}},
    }
    for name, body in policies.items():
        (OUT / name).write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", **body}, separators=(",", ":")), encoding="utf-8")
    # PACK-harden: a revoker key beside the publisher's, for signed revocation documents.
    code, text = ampack("keygen", "--out", str(work / "keys"), "--name", "revoker", "--role", "revoker", "--prefix", "io.example")
    assert code == 0, text
    revoker = json.loads(text[text.index("{"):])
    (OUT / "trust-with-revoker.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [entry, revoker]},
                                                            separators=(",", ":")), encoding="utf-8")

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

    # PACK-conformance-v12: pack.json's fields and kinds, as both verifiers check them.
    def edited(**fields):
        m = json.loads(content["pack.json"])
        m.update(fields)
        return {**content, "pack.json": (json.dumps(m, indent=2, ensure_ascii=False) + chr(10)).encode("utf-8")}
    vectors["unknown-kind"] = (signed(edited(kind="bundle")), "trust.json", "refused")
    vectors["unknown-tier"] = (signed(edited(tier="gold")), "trust.json", "refused")
    vectors["unknown-code-tier"] = (signed(edited(code="E9")), "trust.json", "refused")
    vectors["valid-until-passed"] = (signed(edited(validUntil="2020-01-01T00:00:00Z")), "trust.json", "refused")
    vectors["valid-until-ahead"] = (signed(edited(validUntil="2099-01-01T00:00:00Z")), "trust.json", "verified")
    vectors["valid-until-not-rfc3339"] = (signed(edited(validUntil="2099-01-01")), "trust.json", "refused")
    vectors["source-commit-not-hex"] = (signed(edited(source={"commit": "main"})), "trust.json", "refused")
    vectors["source-valid"] = (signed(edited(source={"repository": "https://example.com/notes", "commit": "0a1b2c3",
                                                     "builtAt": "2026-10-27T00:00:00Z"})), "trust.json", "verified")
    vectors["containable-by-not-an-id"] = (signed(edited(containableBy=["Not An Id"])), "trust.json", "refused")
    vectors["knowledge-without-records"] = (signed({p: b for p, b in content.items() if not p.startswith("records/")}), "trust.json", "refused")
    vectors["definition-with-records"] = (signed(edited(kind="definition")), "trust.json", "refused")
    vectors["protected-without-seal"] = (signed(edited(protection="audience")), "trust.json", "refused")

    # A key's window and the revocations, judged against now as well as the claimed signing time.
    vectors["key-expired"] = (dict(base), "trust-key-expired.json", "refused")
    vectors["key-not-yet-valid"] = (dict(base), "trust-key-not-yet-valid.json", "refused")
    vectors["key-window-open"] = (dict(base), "trust-key-window-open.json", "verified")
    vectors["revocations-stale"] = (dict(base), "trust-revocations-stale.json", "refused")

    # Signed revocation documents (PACK-harden): applied to the policy by both verifiers.
    digest = "sha256:" + hashlib.sha256(base[py.FILES]).hexdigest()
    def revoke(name, *args, env=None):
        code, text = ampack("revoke", str(OUT / name), *args, env=env)
        assert code == 0, text
        return name
    keys_dir = work / "keys"
    revocations = {
        "revocations-none": (revoke("revocations-none.json", "--key", str(keys_dir / "revoker.key.pem"), "--expires", "2099-01-01T00:00:00Z"), "verified"),
        "revocations-digest": (revoke("revocations-digest.json", "--key", str(keys_dir / "revoker.key.pem"), "--expires", "2099-01-01T00:00:00Z",
                                      "--digests", digest), "refused"),
        "revocations-key": (revoke("revocations-key.json", "--key", str(keys_dir / "revoker.key.pem"), "--expires", "2099-01-01T00:00:00Z",
                                   "--keys", entry["keyId"]), "refused"),
        "revocations-expired": (revoke("revocations-expired.json", "--key", str(keys_dir / "revoker.key.pem"), "--expires", "2020-06-01T00:00:00Z",
                                       env={"SOURCE_DATE_EPOCH": "1577836800"}), "refused"),
        "revocations-issued-in-the-future": (revoke("revocations-future.json", "--key", str(keys_dir / "revoker.key.pem"), "--expires", "2100-01-01T00:00:00Z",
                                                    env={"SOURCE_DATE_EPOCH": "4070908800"}), "refused"),
        "revocations-by-a-publisher-key": (revoke("revocations-publisher.json", "--key", str(keys_dir / "publisher.key.pem"), "--expires", "2099-01-01T00:00:00Z"),
                                           "refused"),
    }
    none_env = json.loads((OUT / "revocations-none.json").read_bytes())
    none_doc = json.loads(base64.b64decode(none_env["payload"]))
    tampered = {**none_env, "payload": base64.b64encode(py.canonical({**none_doc, "expires": "2199-01-01T00:00:00Z"}).encode()).decode()}
    (OUT / "revocations-tampered.json").write_bytes(py.canonical(tampered).encode())
    revocations["revocations-tampered"] = ("revocations-tampered.json", "refused")
    # Signed by this writer, applied by .NET: the document is language-neutral too.
    revoker_key = serialization.load_pem_private_key((keys_dir / "revoker.key.pem").read_bytes(), password=None)
    payload = py.canonical({"format": py.REVOCATIONS_FORMAT, "issuedAt": "2026-10-27T00:00:00Z", "expires": "2099-01-01T00:00:00Z",
                            "digests": [digest], "keys": []}).encode()
    signed_doc = f"DSSEv1 {len(py.REVOCATIONS_TYPE)} {py.REVOCATIONS_TYPE} {len(payload)} ".encode() + payload
    sig = revoker_key.sign(signed_doc, ec.ECDSA(hashes.SHA256()))
    (OUT / "revocations-python.json").write_bytes(py.canonical({"payloadType": py.REVOCATIONS_TYPE, "payload": base64.b64encode(payload).decode(),
        "signatures": [{"keyid": revoker["keyId"], "sig": base64.b64encode(sig).decode()}]}).encode())
    revocations["revocations-python-signed"] = ("revocations-python.json", "refused")
    for name, (document, expected) in revocations.items():
        vectors[name] = (dict(base), "trust-with-revoker.json", expected, document)

    # The JSON profile and the archive, more ways.
    vectors["junk-signature-file"] = ({**signed(content), py.SIGNATURES + "junk.dsse.json": b'{"hello":1}\n'}, "trust.json", "refused")
    vectors["integer-beyond-2-53"] = (signed({**content, record: b'{"key":"a","n":9007199254740993}\n'}), "trust.json", "refused")
    vectors["nfd-string"] = (signed({**content, record: '{"key":"cafe\u0301"}\n'.encode("utf-8")}), "trust.json", "refused")
    vectors["empty-line-record"] = (signed({**content, record: content[record] + b"\n"}), "trust.json", "refused")
    vectors["no-final-line-feed"] = (signed({**content, record: content[record].rstrip(b"\n")}), "trust.json", "refused")
    vectors["case-collision"] = (signed({**content, "records/Note.jsonl": content[record]}), "trust.json", "refused")

    # Composites: built with ampack (lock pins the member's digest), then changed by this writer and signed again.
    family = work / "family"
    family.mkdir()
    (family / "pack.json").write_bytes((json.dumps({"format": py.PACK_FORMAT, "id": "io.example.family", "version": "1.0.0", "kind": "composite",
                                                    "contains": [pid], "tier": "open", "protection": "open", "code": "E0"}, indent=2) + chr(10)).encode())
    (family / "members.json").write_bytes((json.dumps({"members": [{"id": pid, "version": "[1,2)"}]}, indent=2) + chr(10)).encode())
    code, text = ampack("lock", str(family), str(VECTORS / "valid-dotnet.ampack"))
    assert code == 0, text
    code, text = ampack("build", str(family), "--key", str(work / "keys" / "publisher.key.pem"), "--out", str(work / "family.ampack"))
    assert code == 0, text
    composite = py.read_zip((work / "family.ampack").read_bytes())
    composite_content = {p: b for p, b in composite.items() if p != py.FILES and not p.startswith(py.SIGNATURES)}
    lock = json.loads(composite_content[py.LOCK])
    def family_with(**files):
        return signed({**composite_content, **files}, pid_="io.example.family")
    vectors["composite-valid"] = (dict(composite), "trust.json", "verified")
    vectors["composite-lock-pins-other"] = (family_with(**{py.LOCK: py.canonical({**lock, "members": [{**lock["members"][0], "id": "io.example.other"}]}).encode()}),
                                            "trust.json", "refused")
    vectors["composite-lock-not-canonical"] = (family_with(**{py.LOCK: (json.dumps(lock, indent=1) + chr(10)).encode()}), "trust.json", "refused")
    vectors["composite-lock-extra-field"] = (family_with(**{py.LOCK: py.canonical({**lock, "note": "x"}).encode()}), "trust.json", "refused")
    vectors["composite-members-differ"] = (family_with(**{"members.json": (json.dumps({"members": [{"id": "io.example.other", "version": "[1,2)"}]}) + chr(10)).encode()}),
                                           "trust.json", "refused")

    # Pack sets: built with ampack (each member keeps its own list and signatures), then changed and signed again as a whole.
    code, text = ampack("set", str(work / "bundle.ampack"), str(VECTORS / "valid-dotnet.ampack"), "--id", "io.example.bundle", "--version", "1.0.0",
                        "--key", str(work / "keys" / "publisher.key.pem"))
    assert code == 0, text
    bundle = py.read_zip((work / "bundle.ampack").read_bytes())
    bundle_content = {p: b for p, b in bundle.items() if p != py.FILES and not p.startswith(py.SIGNATURES)}
    member = f"packs/{pid}/"
    def bundle_with(files, contains=None):
        m = json.loads(files["pack.json"])
        if contains is not None:
            m["contains"] = contains
        files = {**files, "pack.json": (json.dumps(m, indent=2) + chr(10)).encode()}
        return signed(files, pid_="io.example.bundle")
    vectors["set-valid"] = (dict(bundle), "trust.json", "verified")
    vectors["set-member-tampered"] = (bundle_with({**bundle_content, member + record: bundle_content[member + record].replace(b"Replace me", b"Changed")}),
                                      "trust.json", "refused")
    renamed = {(p.replace(member, "packs/io.example.renamed/", 1) if p.startswith(member) else p): b for p, b in bundle_content.items()}
    vectors["set-member-misnamed"] = (bundle_with(renamed, contains=["io.example.renamed"]), "trust.json", "refused")
    vectors["set-file-outside-member"] = (bundle_with({**bundle_content, "packs/loose.json": b'{"a":1}\n'}), "trust.json", "refused")

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
    # PACK-harden 4.13: a deflate stream must end where its entry's bytes end.
    raw["deflate-valid"] = (write_deflated_zip(s), "trust.json", "verified")
    raw["deflate-trailing-bytes"] = (write_deflated_zip(s, junk={record: b"\x00junk"}), "trust.json", "refused")

    results, failures = [], 0
    for name, (files, policy_file, expected, *document) in {**{k: (write_zip(v[0]), *v[1:]) for k, v in vectors.items()}, **raw}.items():
        path = VECTORS / f"{name}.ampack"
        path.write_bytes(files)
        extra = ["--revocations", str(OUT / document[0])] if document else []
        dn_code, dn_text = ampack("verify", str(path), "--policy", str(OUT / policy_file), *extra)
        pr = subprocess.run([sys.executable, "-I", str(HERE / "python" / "ampack_verify.py"), str(path), str(OUT / policy_file),
                             *([str(OUT / document[0])] if document else [])],
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
        dotnet = "verified" if dn_code == 0 else "refused"
        python = "verified" if pr.returncode == 0 else "refused"
        agree = dotnet == python == expected
        failures += not agree
        results.append({"vector": name, "expected": expected, "dotnet": dotnet, "python": python, "agree": agree,
                        "dotnetSays": dn_text.splitlines()[0] if dn_text else "", "pythonSays": pr.stdout.strip().splitlines()[0] if pr.stdout.strip() else pr.stderr.strip()[-200:],
                        "sha256": hashlib.sha256(files).hexdigest()})
        print(f"{'ok  ' if agree else 'FAIL'} {name:34} expected {expected:8} dotnet {dotnet:8} python {python}")
    (OUT / "results.json").write_text(json.dumps({"format": "agentmemory.pack.conformance/1.2", "vectors": results,
                                                  "agreed": len(results) - failures, "total": len(results)}, indent=1) + "\n", encoding="utf-8")
    shutil.rmtree(work, ignore_errors=True)
    print(f"{len(results) - failures}/{len(results)} vectors: both verifiers agree with the expected outcome")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
