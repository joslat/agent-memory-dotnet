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

AMPACK = str(Path(sys.argv[1]).resolve())  # the vectors are built in a work folder: a relative path would not resolve there
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else HERE
VECTORS = OUT / "vectors"


def ampack(*args, env=None):
    r = subprocess.run([AMPACK, *args], capture_output=True, text=True, encoding="utf-8", errors="replace",
                       env={**os.environ, **env} if env else None)
    return r.returncode, (r.stdout + r.stderr).strip()


def write_deflated_zip(files, junk=None, unfinished=()):
    """The strict profile with every entry deflated (method 8); `junk` maps a name to bytes appended after its deflate stream,
    and an entry named in `unfinished` ends with a sync flush: all its data, no final block."""
    out, central = bytearray(), []
    for name in sorted(files):
        data, nb = files[name], name.encode("ascii")
        packer = zlib.compressobj(9, zlib.DEFLATED, -15)
        packed = packer.compress(data) + packer.flush(zlib.Z_SYNC_FLUSH if name in unfinished else zlib.Z_FINISH) + (junk or {}).get(name, b"")
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


def sign(list_bytes, pid, version, key, role="publisher", signed_at="2026-10-26T12:00:00Z", payload_text=None, sig_text=None, extra_signatures=()):
    statement = py.canonical({
        "_type": py.STATEMENT,
        "subject": [{"name": py.FILES, "digest": {"sha256": hashlib.sha256(list_bytes).hexdigest()}}],
        "predicateType": f"urn:agentmemory:pack:{role}:v1",
        "predicate": {"id": pid, "version": version, "role": role, "signedAt": signed_at},
    }).encode()
    spki = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    sig = key.sign(py.pae(statement), ec.ECDSA(hashes.SHA256()))
    signatures = [{"keyid": hashlib.sha256(spki).hexdigest(), "sig": sig_text(base64.b64encode(sig).decode()) if sig_text else base64.b64encode(sig).decode()},
                  *extra_signatures]
    envelope = py.canonical({"payloadType": py.PAYLOAD_TYPE,
                             "payload": payload_text(base64.b64encode(statement).decode()) if payload_text else base64.b64encode(statement).decode(),
                             "signatures": signatures}).encode()
    return f"{py.SIGNATURES}{role}-{hashlib.sha256(spki).hexdigest()[:16]}.dsse.json", envelope


def unpadded_pack(der):
    """A DER signature with the sign pad of r (or else s) dropped, or None when neither has one."""
    r = der[4:4 + der[3]]
    s = der[6 + der[3]:]
    if len(r) == 33: r = r[1:]
    elif len(s) == 33: s = s[1:]
    else: return None
    return bytes([0x30, 4 + len(r) + len(s), 0x02, len(r)]) + r + bytes([0x02, len(s)]) + s



# Edits of a finished DSSE envelope that the one closed shape refuses (review 4): an extra field, an entry with an extra key, an
# entry without a signature, a key id in upper case.
SHAPES = ("envelope-extra-field", "entry-extra-key", "entry-without-sig", "keyid-uppercase")


def reshaped(envelope_bytes, how):
    envelope = json.loads(envelope_bytes)
    entry = envelope["signatures"][0]
    if how == "envelope-extra-field":
        envelope["note"] = "x"
    elif how == "entry-extra-key":
        entry["x"] = 1
    elif how == "entry-without-sig":
        envelope["signatures"].append({"keyid": "a" * 64})
    else:
        entry["keyid"] = entry["keyid"].upper()
    return py.canonical(envelope).encode()


def main():
    # Review 1, M14: the Python verifier keeps instants to 100 ns, as .NET does; two lists 100 ns apart are not the same instant.
    assert py.instant("2026-10-29T00:00:00.0000001Z") > py.instant("2026-10-29T00:00:00Z")
    assert py.instant("2026-10-29T00:00:00.0000002Z") > py.instant("2026-10-29T00:00:00.0000001Z")
    assert py.instant("2026-10-29T00:00:00.1Z") == py.instant("2026-10-29T00:00:00.1000000Z")
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
    # The sixth review: an ECDSA INTEGER whose sign pad was dropped reads as negative. OpenSSL refuses it; .NET took it, so
    # anyone holding a signed pack or list could make the verifiers disagree. Both refuse it now.
    def unpadded(der):
        r = der[4:4 + der[3]]
        s_ = der[6 + der[3]:]
        if len(r) == 33: r = r[1:]
        elif len(s_) == 33: s_ = s_[1:]
        else: return None
        return bytes([0x30, 4 + len(r) + len(s_), 0x02, len(r)]) + r + bytes([0x02, len(s_)]) + s_
    # On a list that revokes nothing, so only the signature's encoding decides: well formed it verifies, unpadded it is refused.
    nothing = py.canonical({"format": py.REVOCATIONS_FORMAT, "issuedAt": "2026-10-27T00:00:00Z", "expires": "2099-01-01T00:00:00Z",
                            "digests": [], "keys": []}).encode()
    nothing_signed = f"DSSEv1 {len(py.REVOCATIONS_TYPE)} {py.REVOCATIONS_TYPE} {len(nothing)} ".encode() + nothing
    while (negative := unpadded(padded := revoker_key.sign(nothing_signed, ec.ECDSA(hashes.SHA256())))) is None:
        pass
    # The seventh review: a list from a revoker the policy revokes still denies what it names, and nothing else.
    revoked_revoker = {"format": "agentmemory.pack.trust/1.1", "keys": [entry, revoker], "revoked": {"digests": [], "keys": [{"keyId": revoker["keyId"]}]}}
    (OUT / "trust-revoker-revoked.json").write_text(json.dumps(revoked_revoker, separators=(",", ":")), encoding="utf-8")
    for name, signature, expected in (("revocations-padded-integer", padded, "verified"), ("revocations-negative-integer", negative, "refused")):
        (OUT / f"{name}.json").write_bytes(py.canonical({"payloadType": py.REVOCATIONS_TYPE, "payload": base64.b64encode(nothing).decode(),
            "signatures": [{"keyid": revoker["keyId"], "sig": base64.b64encode(signature).decode()}]}).encode())
        revocations[name] = (f"{name}.json", expected)
    for name, (document, expected) in revocations.items():
        vectors[name] = (dict(base), "trust-with-revoker.json", expected, document)
    vectors["revocations-by-a-revoked-revoker-revoking-nothing"] = (dict(base), "trust-revoker-revoked.json", "verified", "revocations-none.json")
    vectors["revocations-by-a-revoked-revoker-revoking-the-pack"] = (dict(base), "trust-revoker-revoked.json", "refused", "revocations-digest.json")
    # The eighth review: a revoker that a non-live list revoked must not come back to life; a revoker holds that role alone.
    code, text = ampack("keygen", "--out", str(work / "keys2"), "--name", "second", "--role", "revoker", "--prefix", "io.example")
    assert code == 0, text
    second = json.loads(text[text.index("{"):])
    (OUT / "trust-two-revokers-first-revoked.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [entry, revoker, second],
        "revoked": {"digests": [], "keys": [{"keyId": revoker["keyId"]}]}}, separators=(",", ":")), encoding="utf-8")
    revoke("revocations-revoking-the-second-revoker.json", "--key", str(keys_dir / "revoker.key.pem"), "--expires", "2099-01-01T00:00:00Z", "--keys", second["keyId"])
    vectors["revocations-non-live-list-revoking-a-live-revoker"] = (dict(base), "trust-two-revokers-first-revoked.json", "refused", "revocations-revoking-the-second-revoker.json")
    (OUT / "trust-revoker-also-publisher.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [dict(entry, roles=["publisher", "revoker"])]},
        separators=(",", ":")), encoding="utf-8")
    vectors["policy-with-a-revoker-that-also-publishes"] = (dict(base), "trust-revoker-also-publisher.json", "refused")
    # The ninth review: the rule for one run of several lists, now in both verifiers. The first revoker is rotated out (the
    # policy revokes it); its list revoked the third, a compromised revoker; the second is in good standing.
    code, text = ampack("keygen", "--out", str(work / "keys3"), "--name", "third", "--role", "revoker", "--prefix", "io.example")
    assert code == 0, text
    third = json.loads(text[text.index("{"):])
    first_key, second_key, third_key = str(keys_dir / "revoker.key.pem"), str(work / "keys2" / "second.key.pem"), str(work / "keys3" / "third.key.pem")
    (OUT / "trust-first-rotated-out.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [entry, revoker, second, third],
        "revoked": {"digests": [], "keys": [{"keyId": revoker["keyId"]}]}}, separators=(",", ":")), encoding="utf-8")
    (OUT / "trust-two-live-revokers.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [entry, second, third]},
        separators=(",", ":")), encoding="utf-8")
    (OUT / "trust-successor-not-yet-valid.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [entry, revoker,
        dict(second, notBefore="2099-01-01T00:00:00Z")], "revoked": {"digests": [], "keys": [{"keyId": revoker["keyId"]}]}}, separators=(",", ":")), encoding="utf-8")
    revoke("run-first-revokes-third.json", "--key", first_key, "--expires", "2099-01-01T00:00:00Z", "--keys", third["keyId"])
    revoke("run-third-revokes-second.json", "--key", third_key, "--expires", "2099-01-01T00:00:00Z", "--keys", second["keyId"])
    revoke("run-third-revokes-second-and-itself.json", "--key", third_key, "--expires", "2099-01-01T00:00:00Z", "--keys", f"{second['keyId']},{third['keyId']}")
    revoke("run-second-revokes-third.json", "--key", second_key, "--expires", "2099-01-01T00:00:00Z", "--keys", third["keyId"])
    revoke("run-second-revokes-the-pack.json", "--key", second_key, "--expires", "2099-01-01T00:00:00Z", "--digests", digest)
    vectors["run-rotated-revoker-not-carried-forward"] = (dict(base), "trust-first-rotated-out.json", "refused",
        ["run-first-revokes-third.json", "run-third-revokes-second.json"])
    vectors["run-rotated-revoker-carried-forward"] = (dict(base), "trust-first-rotated-out.json", "verified",
        ["run-first-revokes-third.json", "run-second-revokes-third.json"])
    vectors["run-revoked-revoker-revoking-itself-to-dodge"] = (dict(base), "trust-first-rotated-out.json", "refused",
        ["run-first-revokes-third.json", "run-third-revokes-second-and-itself.json"])
    vectors["run-two-revokers-in-a-circle"] = (dict(base), "trust-two-live-revokers.json", "refused",
        ["run-second-revokes-third.json", "run-third-revokes-second.json"])
    vectors["run-the-same-list-twice"] = (dict(base), "trust-two-live-revokers.json", "verified",
        ["run-second-revokes-third.json", "run-second-revokes-third.json"])
    both = {"format": py.REVOCATIONS_FORMAT, "issuedAt": "2026-10-28T00:00:00Z", "expires": "2099-01-01T00:00:00Z", "digests": [],
            "keys": sorted([{"keyId": second["keyId"]}, {"keyId": third["keyId"]}], key=lambda k: k["keyId"])}
    def list_signed_by(document, key_path, key_id):
        signer_ = serialization.load_pem_private_key(Path(key_path).read_bytes(), None)
        payload = py.canonical(document).encode()
        pae_ = f"DSSEv1 {len(py.REVOCATIONS_TYPE)} {py.REVOCATIONS_TYPE} {len(payload)} ".encode() + payload
        return py.canonical({"payloadType": py.REVOCATIONS_TYPE, "payload": base64.b64encode(payload).decode(),
                             "signatures": [{"keyid": key_id, "sig": base64.b64encode(signer_.sign(pae_, ec.ECDSA(hashes.SHA256()))).decode()}]})
    (OUT / "run-both-revoked-by-second.json").write_text(list_signed_by(both, second_key, second["keyId"]), encoding="utf-8")
    (OUT / "run-both-revoked-by-third.json").write_text(list_signed_by(both, third_key, third["keyId"]), encoding="utf-8")
    vectors["run-one-list-signed-by-both-revokers-in-two-envelopes"] = (dict(base), "trust-two-live-revokers.json", "verified",
        ["run-both-revoked-by-second.json", "run-both-revoked-by-third.json"])
    vectors["run-live-list-revoking-the-pack-beside-others"] = (dict(base), "trust-first-rotated-out.json", "refused",
        ["run-first-revokes-third.json", "run-second-revokes-third.json", "run-second-revokes-the-pack.json"])
    revoke("run-first-revokes-the-successor.json", "--key", first_key, "--expires", "2099-01-01T00:00:00Z", "--keys", second["keyId"])
    vectors["run-non-live-list-revoking-a-successor-not-yet-valid"] = (dict(base), "trust-successor-not-yet-valid.json", "refused",
        ["run-first-revokes-the-successor.json"])

    # The second red team's divergences and crashes (27 Oct): one verdict for each, in both verifiers.
    def signed_with(content_, **options):
        lb = files_list(pid, version, content_)
        path, env = sign(lb, pid, version, key, **options)
        return {**content_, py.FILES: lb, path: env}
    listing = json.loads(files_list(pid, version, content))
    def relisted(change):
        changed = json.loads(json.dumps(listing))
        change(changed["files"][0])
        lb = py.canonical(changed).encode()
        path, env = sign(lb, pid, version, key)
        return {**content, py.FILES: lb, path: env}
    vectors["files-entry-extra-field"] = (relisted(lambda e: e.__setitem__("mode", "rw")), "trust.json", "refused")
    vectors["files-entry-length-true"] = (relisted(lambda e: e.__setitem__("length", True)), "trust.json", "refused")
    vectors["signature-outside-the-alphabet"] = (signed_with(content, sig_text=lambda s: s[:10] + "*" + s[10:]), "trust.json", "refused")
    vectors["payload-base64-with-a-line-break"] = (signed_with(content, payload_text=lambda s: s[:40] + "\n" + s[40:]), "trust.json", "refused")
    # Review 2, L7: an entry is a key id and a signature, shaped as the format writes them, or the envelope refuses the pack.
    vectors["second-signature-entry-not-base64"] = (signed_with(content, extra_signatures=[{"keyid": entry["keyId"], "sig": "abc"}]), "trust.json", "refused")
    vectors["second-signature-entry-without-sig"] = (signed_with(content, extra_signatures=[{"keyid": entry["keyId"]}]), "trust.json", "refused")
    vectors["signed-at-without-leading-zero"] = (signed_with(content, signed_at="2026-1-05T12:00:00Z"), "trust.json", "refused")
    vectors["signed-at-lowercase"] = (signed_with(content, signed_at="2026-10-26t12:00:00z"), "trust.json", "refused")
    vectors["valid-until-offset-beyond-14-hours"] = (signed(edited(validUntil="2099-01-01T00:00:00+15:00")), "trust.json", "refused")
    vectors["valid-until-as-a-number"] = (signed(edited(validUntil=1767225600)), "trust.json", "refused")
    vectors["protection-null"] = (signed(edited(protection=None)), "trust.json", "refused")
    vectors["code-as-a-list"] = (signed(edited(code=["E2"])), "trust.json", "refused")
    vectors["sdk-as-a-number"] = (signed(edited(sdk=1)), "trust.json", "refused")
    vectors["display-as-a-string"] = (signed(edited(display="notes")), "trust.json", "refused")
    vectors["number-beyond-double"] = (signed({**content, record: b'{"key":"a","n":1e400}\n'}), "trust.json", "refused")
    vectors["integer-int64-min"] = (signed({**content, record: b'{"key":"a","n":-9223372036854775808}\n'}), "trust.json", "refused")
    nested = lambda depth: ("[" * depth + "]" * depth).encode()
    vectors["nesting-64-deep"] = (signed({**content, record: b'{"key":"a","n":' + nested(63) + b'}\n'}), "trust.json", "verified")
    vectors["nesting-65-deep"] = (signed({**content, record: b'{"key":"a","n":' + nested(64) + b'}\n'}), "trust.json", "refused")
    vectors["combining-marks-30-in-a-row"] = (signed({**content, record: ('{"key":"q' + "\u0301" * 30 + '"}\n').encode("utf-8")}), "trust.json", "verified")
    vectors["combining-marks-31-in-a-row"] = (signed({**content, record: ('{"key":"q' + "\u0301" * 31 + '"}\n').encode("utf-8")}), "trust.json", "refused")
    vectors["source-100-astral-characters"] = (signed(edited(source={"repository": "\U0001F600" * 100})), "trust.json", "verified")
    vectors["source-101-astral-characters"] = (signed(edited(source={"repository": "\U0001F600" * 101})), "trust.json", "refused")
    arabic = "1\u0663.0.0"
    arabic_content = {**content, "pack.json": (json.dumps({**json.loads(content["pack.json"]), "version": arabic}, ensure_ascii=False) + chr(10)).encode("utf-8")}
    vectors["version-with-an-arabic-digit"] = (signed(arabic_content, version_=arabic), "trust.json", "refused")
    extra_statement = base64.b64encode(py.canonical({"_type": 1, "subject": [], "predicateType": 2, "predicate": {"role": 3}}).encode()).decode()
    odd = py.canonical({"payloadType": py.PAYLOAD_TYPE, "payload": extra_statement, "signatures": [{"keyid": entry["keyId"], "sig": "AAAA"}]}).encode()
    # Review 1, L7: an envelope that does not sign this pack is unlisted content riding beside it, and refuses it (it was ignored).
    vectors["extra-envelope-of-wrong-types"] = ({**signed(content), py.SIGNATURES + "zz.dsse.json": odd}, "trust.json", "refused")
    base_list = files_list(pid, version, content)
    other_list = files_list(pid, version, {**content, "records/extra.jsonl": content[record]})
    _, for_other = sign(other_list, pid, version, key, role="curator")
    vectors["envelope-signing-another-list"] = ({**signed(content), py.SIGNATURES + "curator-other.dsse.json": for_other}, "trust.json", "refused")
    _, unentried = sign(base_list, pid, version, key, role="curator")
    unentried = py.canonical({**json.loads(unentried), "signatures": []}).encode()
    vectors["envelope-with-no-signature"] = ({**signed(content), py.SIGNATURES + "curator-none.dsse.json": unentried}, "trust.json", "refused")
    stranger_key = ec.generate_private_key(ec.SECP256R1())
    path_, by_stranger = sign(base_list, pid, version, stranger_key, role="curator")
    vectors["countersignature-by-a-key-not-trusted-ignored"] = ({**signed(content), path_: by_stranger}, "trust.json", "verified")
    # Policies the red team found read differently: both verifiers refuse them before any pack is looked at.
    for name, body in {
        "trust-require-no-roles.json": {"keys": [entry], "requireRoles": []},
        "trust-key-listed-twice.json": {"keys": [entry, entry]},
        "trust-prefix-null.json": {"keys": [dict(entry, idPrefixes=[None])]},
        "trust-time-not-rfc3339.json": {"keys": [dict(entry, notAfter="next year")]},
    }.items():
        (OUT / name).write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", **body}, separators=(",", ":")), encoding="utf-8")
    unsigned = {**content, py.FILES: files_list(pid, version, content)}
    vectors["unsigned-pack-with-no-required-roles"] = (unsigned, "trust-require-no-roles.json", "refused")
    vectors["policy-with-a-key-listed-twice"] = (dict(base), "trust-key-listed-twice.json", "refused")
    vectors["policy-with-a-null-prefix"] = (dict(base), "trust-prefix-null.json", "refused")
    vectors["policy-with-a-time-not-rfc3339"] = (dict(base), "trust-time-not-rfc3339.json", "refused")

    # The third review's (27 Oct): the Stream-Safe count on the NFKD decomposition and on keys, times, versions, policies.
    # U+00A8 is a starter whose NFKD trails one mark: with 29 more it is 30 in a row, with 30 it is 31.
    vectors["stream-safe-nfkd-30"] = (signed({**content, record: ('{"key":"q\u00a8' + "\u0316" * 29 + '"}\n').encode("utf-8")}), "trust.json", "verified")
    vectors["stream-safe-nfkd-31"] = (signed({**content, record: ('{"key":"q\u00a8' + "\u0316" * 30 + '"}\n').encode("utf-8")}), "trust.json", "refused")
    vectors["stream-safe-expanding-starters"] = (signed({**content, record: ('{"key":"q' + "\u0f73" * 16 + '"}\n').encode("utf-8")}), "trust.json", "refused")
    vectors["object-key-30-marks"] = (signed({**content, record: ('{"key":"a","q' + "\u0301" * 30 + '":1}\n').encode("utf-8")}), "trust.json", "verified")
    vectors["object-key-31-marks"] = (signed({**content, record: ('{"key":"a","q' + "\u0301" * 31 + '":1}\n').encode("utf-8")}), "trust.json", "refused")
    vectors["nesting-100000-deep"] = (signed({**content, record: b'{"key":"a","n":' + nested(100000) + b'}\n'}), "trust.json", "refused")
    vectors["valid-until-offset-minutes-60"] = (signed(edited(validUntil="2099-01-01T00:00:00+05:60")), "trust.json", "refused")
    prerelease = "1.0.0-01"
    prerelease_content = {**content, "pack.json": (json.dumps({**json.loads(content["pack.json"]), "version": prerelease}, indent=2) + chr(10)).encode("utf-8")}
    vectors["version-prerelease-leading-zero"] = (signed(prerelease_content, version_=prerelease), "trust.json", "refused")
    vectors["files-list-a-number"] = ({**content, py.FILES: b"5"}, "trust.json", "refused")
    vectors["files-list-an-array"] = ({**content, py.FILES: b"[{}]"}, "trust.json", "refused")
    # The signatures array is not signed: anyone may append an entry, and a malformed one only fails to count.
    vectors["signature-entry-keyid-a-list"] = (signed_with(content, extra_signatures=({"keyid": ["x"], "sig": "AAAA"},)), "trust.json", "refused")
    from cryptography.hazmat.primitives.asymmetric import rsa
    rsa_public = rsa.generate_private_key(public_exponent=65537, key_size=2048).public_key()
    rsa_pem = rsa_public.public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
    rsa_id = hashlib.sha256(rsa_public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).hexdigest()
    for name, body in {
        "trust-time-null.json": {"keys": [dict(entry, notAfter=None)]},
        "trust-revoked-expires-null.json": {"keys": [entry], "revoked": {"digests": [], "expires": None}},
        "trust-revoked-key-null.json": {"keys": [entry], "revoked": {"keys": [{"keyId": None}]}},
        "trust-prefix-double-dot.json": {"keys": [dict(entry, idPrefixes=["io.example.."])]},
        "trust-rsa-key.json": {"keys": [entry, {"keyId": rsa_id, "publicKey": rsa_pem, "idPrefixes": ["io.other"], "roles": ["publisher"]}]},
    }.items():
        (OUT / name).write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", **body}, separators=(",", ":")), encoding="utf-8")
    plain = json.dumps(policy, separators=(",", ":")).encode()
    (OUT / "trust-two-boms.json").write_bytes(b"\xef\xbb\xbf\xef\xbb\xbf" + plain)
    (OUT / "trust-one-bom.json").write_bytes(b"\xef\xbb\xbf" + plain)
    (OUT / "trust-not-utf8.json").write_bytes(plain.replace(b'"publisher"', b'"publisher\xff"'))
    vectors["policy-with-a-null-time"] = (dict(base), "trust-time-null.json", "refused")
    vectors["policy-with-revocations-expiring-null"] = (dict(base), "trust-revoked-expires-null.json", "refused")
    vectors["policy-with-a-null-revoked-key"] = (dict(base), "trust-revoked-key-null.json", "refused")
    vectors["policy-prefix-with-two-dots"] = (dict(base), "trust-prefix-double-dot.json", "refused")
    vectors["policy-with-an-rsa-key"] = (dict(base), "trust-rsa-key.json", "refused")
    vectors["policy-with-two-boms"] = (dict(base), "trust-two-boms.json", "refused")
    vectors["policy-with-one-bom"] = (dict(base), "trust-one-bom.json", "verified")
    # Review 1, H3: a misspelt or malformed policy field is refused by both verifiers, never ignored.
    for name, body in {
        "trust-misspelt-require-roles.json": {"keys": [entry], "requiredRoles": ["publisher", "curator"]},
        "trust-key-misspelt-not-after.json": {"keys": [dict(entry, notafter="2020-01-01T00:00:00Z")]},
        "trust-revoked-misspelt-digests.json": {"keys": [entry], "revoked": {"digest": ["sha256:" + "0" * 64]}},
        "trust-revoked-digest-uppercase.json": {"keys": [entry], "revoked": {"digests": ["SHA256:" + "A" * 64]}},
        "trust-revoked-key-id-short.json": {"keys": [entry], "revoked": {"keys": [{"keyId": "abc"}]}},
        "trust-revoked-key-extra-field.json": {"keys": [entry], "revoked": {"keys": [{"keyId": "0" * 64, "reason": "lost"}]}},
        "trust-prefix-uppercase.json": {"keys": [dict(entry, idPrefixes=["IO.Example"])]},
        "trust-prefix-trailing-dot.json": {"keys": [dict(entry, idPrefixes=["io.example."])]},
        "trust-root-extra-field.json": {"keys": [entry], "comment": "a note"},
    }.items():
        (OUT / name).write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", **body}, separators=(",", ":")), encoding="utf-8")
    for name, expected in (("trust-misspelt-require-roles.json", "refused"), ("trust-key-misspelt-not-after.json", "refused"),
                           ("trust-revoked-misspelt-digests.json", "refused"), ("trust-revoked-digest-uppercase.json", "refused"),
                           ("trust-revoked-key-id-short.json", "refused"), ("trust-revoked-key-extra-field.json", "refused"),
                           ("trust-prefix-uppercase.json", "refused"), ("trust-prefix-trailing-dot.json", "verified"),
                           ("trust-root-extra-field.json", "refused")):
        vectors["policy-" + name[len("trust-"):-len(".json")]] = (dict(base), name, expected)
    vectors["policy-not-utf8"] = (dict(base), "trust-not-utf8.json", "refused")
    # The fourth review's: a key on another curve, a version numeral too long to read, members named by non-strings.
    p384 = ec.generate_private_key(ec.SECP384R1()).public_key()
    p384_pem = p384.public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
    p384_id = hashlib.sha256(p384.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)).hexdigest()
    (OUT / "trust-p384-key.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [entry, {"keyId": p384_id, "publicKey": p384_pem,
        "idPrefixes": ["io.other"], "roles": ["publisher"]}]}, separators=(",", ":")), encoding="utf-8")
    vectors["policy-with-a-p384-key"] = (dict(base), "trust-p384-key.json", "refused")
    long_version = "1" * 5000 + ".0.0"
    long_content = {**content, "pack.json": (json.dumps({**json.loads(content["pack.json"]), "version": long_version}, indent=2) + chr(10)).encode("utf-8")}
    vectors["version-5000-digits"] = (signed(long_content, version_=long_version), "trust.json", "refused")
    # The fifth review's bounds before any signature holds: a version is at most 256 characters; an envelope at most 64 KiB
    # and 16 signature entries; a pack at most 16 envelopes. Each bound, and one past it.
    def versioned(text):
        changed = {**content, "pack.json": (json.dumps({**json.loads(content["pack.json"]), "version": text}, indent=2) + chr(10)).encode("utf-8")}
        return signed(changed, version_=text)
    vectors["version-256-characters"] = (versioned("1.0.0-" + "a" * 250), "trust.json", "verified")
    vectors["version-257-characters"] = (versioned("1.0.0-" + "a" * 251), "trust.json", "refused")
    junk_entry = {"keyid": "0" * 64, "sig": "AAAA"}
    vectors["envelope-16-signatures"] = (signed_with(content, extra_signatures=(junk_entry,) * 15), "trust.json", "verified")
    vectors["envelope-17-signatures"] = (signed_with(content, extra_signatures=(junk_entry,) * 16), "trust.json", "refused")
    one = signed(content)
    envelope_path, envelope = next((p, b) for p, b in one.items() if p.startswith(py.SIGNATURES))
    def copies(n):
        return {**one, **{f"{py.SIGNATURES}copy-{i:02}.dsse.json": envelope for i in range(n)}}
    vectors["sixteen-envelopes"] = (copies(15), "trust.json", "verified")
    vectors["seventeen-envelopes"] = (copies(16), "trust.json", "refused")
    padded = json.loads(envelope)
    padded["padding"] = "x" * (64 * 1024)
    while True:
        listed = files_list(pid, version, content)
        sig_path, sig_env = sign(listed, pid, version, key)
        envelope_json = json.loads(sig_env)
        if (negative_pack := unpadded_pack(base64.b64decode(envelope_json["signatures"][0]["sig"]))) is not None:
            break
    envelope_json["signatures"][0]["sig"] = base64.b64encode(negative_pack).decode()
    vectors["signature-integer-without-sign-pad"] = ({**content, py.FILES: listed, sig_path: py.canonical(envelope_json).encode()}, "trust.json", "refused")
    vectors["envelope-over-64-kib"] = ({**one, py.SIGNATURES + "padded.dsse.json": py.canonical(padded).encode()}, "trust.json", "refused")

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
    vectors["composite-members-not-strings"] = (family_with(**{"members.json": b'{"members":[{"id":1,"version":"[1,2)"}]}\n'}), "trust.json", "refused")
    vectors["composite-lock-not-canonical"] = (family_with(**{py.LOCK: (json.dumps(lock, indent=1) + chr(10)).encode()}), "trust.json", "refused")
    vectors["composite-lock-extra-field"] = (family_with(**{py.LOCK: py.canonical({**lock, "note": "x"}).encode()}), "trust.json", "refused")
    vectors["composite-members-differ"] = (family_with(**{"members.json": (json.dumps({"members": [{"id": "io.example.other", "version": "[1,2)"}]}) + chr(10)).encode()}),
                                           "trust.json", "refused")

    vectors["composite-lock-id-a-number"] = (family_with(**{py.LOCK: py.canonical({**lock, "members": [{**lock["members"][0], "id": 5}]}).encode()}),
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

    # Federated packs (federated packs §3-§5): the publisher WebID bound to the signing key by the host, the host inside the
    # audience, and publication receipts for one URL and the exact archive bytes.
    pub, buyer, stranger = ("https://supplier-a.example/profile/card#org", "https://buyer-b.example/profile/card#agent",
                            "https://stranger.example/profile/card#me")

    def federated_policy(name, webid=pub, host=(buyer,), more=()):
        body = {"format": "agentmemory.pack.trust/1.1", "keys": [dict(entry, **({"webId": webid} if webid else {})), *more]}
        if host:
            body["host"] = {"webIds": list(host)}
        (OUT / name).write_text(json.dumps(body, separators=(",", ":")), encoding="utf-8")
    federated_policy("trust-federated-buyer.json")
    federated_policy("trust-federated-stranger.json", host=(stranger,))
    federated_policy("trust-federated-no-host.json", host=())
    federated_policy("trust-federated-unbound.json", webid=None)
    federated_policy("trust-federated-other-webid.json", webid="https://other.example/profile/card#org")
    code, text = ampack("keygen", "--out", str(work / "keys"), "--name", "mirror", "--prefix", "io.example")
    assert code == 0, text
    mirror_entry = json.loads(text[text.index("{"):])
    mirror = serialization.load_pem_private_key((work / "keys" / "mirror.key.pem").read_bytes(), password=None)
    federated_policy("trust-federated-buyer-and-mirror.json", more=(dict(mirror_entry, webId="https://mirror.example/profile/card#org"),))
    # FED-review round 1: receipts signed by a second key of the same publisher, in each state a key can be in.
    code, text = ampack("keygen", "--out", str(work / "keys"), "--name", "curator", "--role", "curator", "--prefix", "io.example")
    assert code == 0, text
    curator_entry = json.loads(text[text.index("{"):])
    curator = serialization.load_pem_private_key((work / "keys" / "curator.key.pem").read_bytes(), password=None)
    federated_policy("trust-federated-second-key.json", more=(dict(mirror_entry, webId=pub),))
    federated_policy("trust-federated-curator.json", more=(dict(curator_entry, webId=pub),))
    federated_policy("trust-federated-second-key-expired.json", more=(dict(mirror_entry, webId=pub, notAfter="2020-01-01T00:00:00Z"),))
    revoked_policy = json.loads((OUT / "trust-federated-second-key.json").read_text(encoding="utf-8"))
    revoked_policy["revoked"] = {"digests": [], "keys": [{"keyId": mirror_entry["keyId"]}]}
    (OUT / "trust-federated-second-key-revoked.json").write_text(json.dumps(revoked_policy, separators=(",", ":")), encoding="utf-8")
    for name, body in {"trust-federated-host-empty.json": {"keys": [entry], "host": {"webIds": []}},
                       "trust-federated-host-extra.json": {"keys": [entry], "host": {"webIds": [buyer], "name": "buyer"}},
                       "trust-federated-webid-http.json": {"keys": [dict(entry, webId="http://supplier-a.example/p#me")]},
                       "trust-federated-revoker-webid.json": {"keys": [entry, dict(revoker, webId=pub)]}}.items():
        (OUT / name).write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", **body}, separators=(",", ":")), encoding="utf-8")

    def federation(audience=(buyer,), critical=True, **extra):
        f = {"format": "agentmemory.pack.federation/1", "publisher": pub, "audience": list(audience) if isinstance(audience, tuple) else audience, **extra}
        return {"federation": f, **({"critical": ["federation"]} if critical else {})}
    federated = signed(manifest_with(extensions=federation(purpose="Supply-chain handoff")))
    vectors["federated-valid"] = (federated, "trust-federated-buyer.json", "verified")
    vectors["federated-host-outside-audience"] = (federated, "trust-federated-stranger.json", "refused")
    vectors["federated-host-names-no-webid"] = (federated, "trust-federated-no-host.json", "refused")
    vectors["federated-public-host-names-no-webid"] = (signed(manifest_with(extensions=federation(audience="public"))), "trust-federated-no-host.json", "verified")
    vectors["federated-key-unbound"] = (federated, "trust-federated-unbound.json", "refused")
    vectors["federated-key-bound-elsewhere"] = (federated, "trust-federated-other-webid.json", "refused")
    vectors["federated-not-critical"] = (signed(manifest_with(extensions=federation(critical=False))), "trust-federated-buyer.json", "refused")
    vectors["federated-purpose-300"] = (signed(manifest_with(extensions=federation(purpose="x" * 300))), "trust-federated-buyer.json", "verified")
    two_readers = signed(manifest_with(extensions=federation(audience=(buyer, stranger))))
    vectors["federated-two-readers"] = (two_readers, "trust-federated-buyer.json", "verified")
    # FED-review H1: the purpose counts UTF-16 units in both verifiers.
    vectors["federated-purpose-150-astral"] = (signed(manifest_with(extensions=federation(purpose="\U0001F600" * 150))), "trust-federated-buyer.json", "verified")
    vectors["federated-purpose-151-astral"] = (signed(manifest_with(extensions=federation(purpose="\U0001F600" * 151))), "trust-federated-buyer.json", "refused")

    # FED-review M5 and H2: a federated set's members are federated, by its publisher, for no wider an audience; and every
    # member verifies.
    def fed_set(member_ext, member_key=None):
        member_content = manifest_with(extensions=member_ext) if member_ext else dict(content)
        mlb = files_list(pid, version, member_content)
        mpath, menv = sign(mlb, pid, version, member_key or key)
        set_manifest = {"format": "agentmemory.pack/1.2", "id": "io.example.bundle", "version": "1.0.0", "kind": "set", "contains": [pid],
                        "extensions": federation()}
        set_content = {"pack.json": (json.dumps(set_manifest) + chr(10)).encode(),
                       **{f"packs/{pid}/{name}": data for name, data in {**member_content, py.FILES: mlb, mpath: menv}.items()}}
        return signed(set_content, pid_="io.example.bundle")
    federated_set = fed_set(federation())
    vectors["set-federated-valid"] = (federated_set, "trust-federated-buyer.json", "verified")
    vectors["set-federated-member-not-federated"] = (fed_set(None), "trust-federated-buyer.json", "refused")
    vectors["set-federated-member-wider"] = (fed_set(federation(audience=(buyer, stranger))), "trust-federated-buyer.json", "refused")
    untrusted_member_set = fed_set(federation(), member_key=mirror)
    vectors["set-federated-member-untrusted"] = (untrusted_member_set, "trust-federated-buyer.json", "refused")
    public_federated = signed(manifest_with(extensions=federation(audience="public")))
    for name, ext in {"federated-audience-empty": federation(audience=[]),
                      "federated-audience-everyone": federation(audience="everyone"),
                      "federated-audience-http": federation(audience=["http://buyer-b.example/profile/card#agent"]),
                      "federated-audience-turtle-breaker": federation(audience=["https://buyer-b.example/profile/card>#agent"]),
                      "federated-audience-two-fragments": federation(audience=["https://buyer-b.example/card#a#b"]),
                      "federated-audience-twice": federation(audience=[buyer, buyer]),
                      "federated-audience-65": federation(audience=[f"https://b{i}.example/p#me" for i in range(65)]),
                      "federated-unknown-field": federation(maxScope="partners"),
                      "federated-other-format": {"critical": ["federation"], "federation": {**federation()["federation"], "format": "agentmemory.pack.federation/2"}},
                      "federated-no-audience": {"critical": ["federation"], "federation": {"format": "agentmemory.pack.federation/1", "publisher": pub}},
                      "federated-publisher-not-webid": {"critical": ["federation"], "federation": {**federation()["federation"], "publisher": "urn:uuid:4f0c21a0"}},
                      "federated-purpose-301": federation(purpose="x" * 301)}.items():
        vectors[name] = (signed(manifest_with(extensions=ext)), "trust-federated-buyer.json", "refused")
    for policy_name in ("trust-federated-host-empty.json", "trust-federated-host-extra.json", "trust-federated-webid-http.json",
                        "trust-federated-revoker-webid.json"):
        vectors["policy-" + policy_name[6:-5]] = (federated, policy_name, "refused")

    raw = {}
    # A federated pack built and signed by ampack itself.
    fed_folder = work / "fed-notes"
    assert ampack("init", str(fed_folder), "--id", "io.example.handoff", "--version", "1.0.0")[0] == 0
    fed_manifest = json.loads((fed_folder / "pack.json").read_text(encoding="utf-8"))
    fed_manifest["extensions"] = federation()
    (fed_folder / "pack.json").write_text(json.dumps(fed_manifest, separators=(",", ":")), encoding="utf-8")
    code, text = ampack("build", str(fed_folder), "--key", str(work / "keys" / "publisher.key.pem"), "--out", str(work / "fed-dotnet.ampack"))
    assert code == 0, text
    fed_dotnet = (work / "fed-dotnet.ampack").read_bytes()
    raw["federated-valid-dotnet"] = (fed_dotnet, "trust-federated-buyer.json", "verified")
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
    raw["deflate-without-its-final-block"] = (write_deflated_zip(s, unfinished={record}), "trust.json", "refused")

    # The pack card (v1.2 §15): card.json, read strictly by both verifiers. ampack init writes one into the base pack.
    def with_card(card_):
        return signed({**content, "card.json": json.dumps(card_, ensure_ascii=False).encode("utf-8") + b"\n"})
    full_card = {"format": "agentmemory.pack.card/1", "owner": {"name": "Example Supplier", "contact": "https://example.com/contact"},
                 "intendedUse": "Notes for one team.", "limitations": "Not for anything else.\nIt may be out of date.\tCheck the date.",
                 "boundaries": ["Do not export the notes."], "risks": ["Stale notes."], "provenance": "Written by the team.",
                 "audience": "The team.", "domain": "notes", "references": ["https://example.com/notes"]}
    vectors["card-valid-full"] = (with_card(full_card), "trust.json", "verified")
    vectors["card-absent"] = (signed({k: v for k, v in content.items() if k != "card.json"}), "trust.json", "verified")
    vectors["card-unknown-field"] = (with_card({**full_card, "rating": "five stars"}), "trust.json", "refused")
    vectors["card-without-intended-use"] = (with_card({k: v for k, v in full_card.items() if k != "intendedUse"}), "trust.json", "refused")
    vectors["card-other-format"] = (with_card({**full_card, "format": "agentmemory.pack.card/2"}), "trust.json", "refused")
    vectors["card-terminal-escape"] = (with_card({**full_card, "owner": {"name": "Example\u001b[31m Supplier"}}), "trust.json", "refused")
    vectors["card-c1-control"] = (with_card({**full_card, "domain": "notes\u0085"}), "trust.json", "refused")
    # Lengths in UTF-16 code units, as every text bound in the format: 1,000 astral characters are 2,000 units.
    vectors["card-astral-at-limit"] = (with_card({**full_card, "intendedUse": "\U0001F600" * 1000}), "trust.json", "verified")
    vectors["card-astral-over-limit"] = (with_card({**full_card, "intendedUse": "\U0001F600" * 1000 + "a"}), "trust.json", "refused")
    vectors["card-boundary-repeated"] = (with_card({**full_card, "boundaries": ["Same.", "Same."]}), "trust.json", "refused")
    vectors["card-owner-empty"] = (with_card({**full_card, "owner": {"name": ""}}), "trust.json", "refused")

    # The attestations area (v1.2 §16): outside the files list, shaped and bounded; a certificate's content is not trusted here.
    stem = "0123456789abcdef-20261028t120000z"
    beside = signed(content)
    raw["attestation-certificate-content-unchecked"] = (write_zip({**beside, f"META/attestations/{stem}.certificate.dsse.json": b"not even JSON"}),
                                                        "trust.json", "verified")
    raw["attestation-stray-file"] = (write_zip({**beside, "META/attestations/notes.txt": b"x"}), "trust.json", "refused")
    raw["attestation-run-without-certificate"] = (write_zip({**beside, f"META/attestations/{stem}.aef/run.json": b"{}"}), "trust.json", "refused")
    raw["attestation-certificate-too-large"] = (write_zip({**beside, f"META/attestations/{stem}.certificate.dsse.json": b" " * (64 * 1024 + 1)}),
                                                "trust.json", "refused")
    raw["attestation-nine-certificates"] = (write_zip({**beside, **{f"META/attestations/{i:016x}-20261028t120000z.certificate.dsse.json": b"{}"
                                                                    for i in range(9)}}), "trust.json", "refused")
    raw["attestation-uppercase-stem"] = (write_zip({**beside, "META/attestations/ABC.certificate.dsse.json": b"{}"}), "trust.json", "refused")

    # Review 2: the rules of round 1 and 2 held to agreement by both verifiers, not only by .NET unit tests.
    vectors["folders-differ-only-by-case"] = (signed({**content, "assets/Icons/a.png": b"x", "assets/icons/b.png": b"y"}), "trust.json", "refused")
    vectors["file-named-like-a-folder"] = (signed({**content, "notes": b"x", "notes/a.json": b'{}\n'}), "trust.json", "refused")
    set_manifest = json.loads(bundle_content["pack.json"])
    def set_of(set_id, members):
        files = {"pack.json": (json.dumps({**set_manifest, "id": set_id, "contains": sorted(members)}, indent=2) + chr(10)).encode()}
        for member_id, member_files in members.items():
            files.update({f"packs/{member_id}/{name}": data for name, data in member_files.items()})
        return signed(files, pid_=set_id)
    vectors["set-inside-a-set"] = (set_of("io.example.outer", {"io.example.bundle": dict(bundle)}), "trust.json", "refused")
    same_id = json.loads(content["pack.json"])
    same_id_content = {**content, "pack.json": (json.dumps({**same_id, "id": "io.example.bundle"}, indent=2) + chr(10)).encode()}
    vectors["set-member-with-the-set-id"] = (set_of("io.example.bundle", {"io.example.bundle": signed(same_id_content, pid_="io.example.bundle")}),
                                             "trust.json", "refused")
    vectors["composite-with-records"] = (family_with(**{"records/x.jsonl": b'{"key":"x"}\n'}), "trust.json", "refused")
    vectors["composite-member-extra-field"] = (family_with(**{"members.json": (json.dumps({"members": [{"id": pid, "version": "[1,2)", "note": "x"}]}) + chr(10)).encode()}),
                                               "trust.json", "refused")
    for name, member in {"member-without-version": {"id": pid}, "member-priority-text": {"id": pid, "version": "[1,2)", "priority": "1"},
                         "member-enabled-text": {"id": pid, "version": "[1,2)", "enabled": "yes"},
                         "member-contains-a-number": {"id": pid, "version": "[1,2)", "contains": [1]}}.items():
        vectors[f"composite-{name}"] = (family_with(**{"members.json": (json.dumps({"members": [member]}) + chr(10)).encode()}), "trust.json", "refused")
    vectors["valid-until-fraction-with-offset"] = (signed(edited(validUntil="2099-01-01T00:00:00.5+01:00")), "trust.json", "verified")
    vectors["valid-until-seven-digit-fraction"] = (signed(edited(validUntil="2099-01-01T00:00:00.1234567Z")), "trust.json", "verified")
    (OUT / "trust-key-window-fraction-offset.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [
        dict(entry, notBefore="2020-01-01T00:00:00.25-05:30", notAfter="2099-01-01T00:00:00.5+01:00")]}, separators=(",", ":")), encoding="utf-8")
    vectors["key-window-fraction-with-offset"] = (dict(base), "trust-key-window-fraction-offset.json", "verified")
    private_pem = (work / "keys" / "publisher.key.pem").read_text(encoding="utf-8")
    # A private key never lands in the tree: this policy lives in the work folder, which is git-ignored and removed after the run.
    (work / "trust-private-key.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [dict(entry, publicKey=private_pem)]},
                                                            separators=(",", ":")), encoding="utf-8")
    vectors["policy-with-a-private-key"] = (dict(base), ".work/trust-private-key.json", "refused")
    (OUT / "trust-two-key-blocks.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [
        dict(entry, publicKey=entry["publicKey"].rstrip("\n") + "\n" + entry["publicKey"])]}, separators=(",", ":")), encoding="utf-8")
    vectors["policy-with-two-key-blocks"] = (dict(base), "trust-two-key-blocks.json", "refused")
    # Review 3: the key itself in one form. A compressed or hybrid point, or base64 with stray bits, was read differently.
    publisher_public = serialization.load_pem_public_key(entry["publicKey"].encode())
    uncompressed = publisher_public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    point = uncompressed[27:]
    def pem_of(der):
        body = base64.b64encode(der).decode()
        return "-----BEGIN PUBLIC KEY-----\n" + "\n".join(body[i:i + 64] for i in range(0, len(body), 64)) + "\n-----END PUBLIC KEY-----\n"
    compressed = bytes.fromhex("3039301306072a8648ce3d020106082a8648ce3d030107032200") + bytes([2 + (point[63] & 1)]) + point[:32]
    hybrid = uncompressed[:26] + bytes([6 + (point[63] & 1)]) + point
    body = base64.b64encode(uncompressed).decode()
    alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
    stray = body[:-3] + alphabet[alphabet.index(body[-3]) | 1] + body[-2:]
    for name, pem in {"compressed-point": pem_of(compressed), "hybrid-point": pem_of(hybrid),
                      "stray-base64-bits": "-----BEGIN PUBLIC KEY-----\n" + "\n".join(stray[i:i + 64] for i in range(0, len(stray), 64)) + "\n-----END PUBLIC KEY-----\n"}.items():
        (OUT / f"trust-key-{name}.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [dict(entry, publicKey=pem)]},
                                                              separators=(",", ":")), encoding="utf-8")
        vectors[f"policy-key-{name}"] = (dict(base), f"trust-key-{name}.json", "refused")
    p256 = 2**256 - 2**224 + 2**192 + 2**96 - 1
    b256 = 0x5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b
    small_x = next(x for x in range(1, 1000) if pow((x**3 - 3 * x + b256) % p256, (p256 + 1) // 4, p256) ** 2 % p256 == (x**3 - 3 * x + b256) % p256)
    small_y = pow((small_x**3 - 3 * small_x + b256) % p256, (p256 + 1) // 4, p256)
    off_curve_y = bytearray(point[32:]); off_curve_y[-1] ^= 1
    for name, der in {"off-curve": uncompressed[:27] + point[:32] + bytes(off_curve_y),
                      "x-not-reduced": uncompressed[:27] + (small_x + p256).to_bytes(32, "big") + small_y.to_bytes(32, "big")}.items():
        second_key = dict(entry, keyId=hashlib.sha256(der).hexdigest(), publicKey=pem_of(der))
        (OUT / f"trust-second-key-{name}.json").write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", "keys": [entry, second_key]},
                                                                      separators=(",", ":")), encoding="utf-8")
        vectors[f"policy-second-key-{name}"] = (dict(base), f"trust-second-key-{name}.json", "refused")
    vectors["revocations-empty-list-in-two-envelopes"] = (dict(base), "trust-with-revoker.json", "verified",
                                                          ["revocations-none.json", "revocations-none-reencoded.json"])
    (OUT / "revocations-none-reencoded.json").write_text("{ " + (OUT / "revocations-none.json").read_text(encoding="utf-8")[1:], encoding="utf-8")
    digest_list = (OUT / "revocations-digest.json").read_text(encoding="utf-8")
    (OUT / "revocations-digest-reencoded.json").write_text("{ " + digest_list[1:], encoding="utf-8")
    vectors["revocations-one-list-in-two-envelopes"] = (dict(base), "trust-with-revoker.json", "refused",
                                                         ["revocations-digest.json", "revocations-digest-reencoded.json"])
    vectors["revocations-65-lists"] = (dict(base), "trust-with-revoker.json", "refused", ["revocations-none.json"] * 65)
    none_list = (OUT / "revocations-none.json").read_bytes()
    for how in SHAPES:
        (OUT / f"revocations-shape-{how}.json").write_bytes(reshaped(none_list, how))
        vectors[f"revocations-shape-{how}"] = (dict(base), "trust-with-revoker.json", "refused", f"revocations-shape-{how}.json")

    # Review 2, L7: every shape in an envelope closed, in both verifiers.
    def signed_statement(content_, statement_extra=None, predicate_extra=None, envelope_extra=None):
        lb = files_list(pid, version, content_)
        statement = {"_type": py.STATEMENT, "subject": [{"name": py.FILES, "digest": {"sha256": hashlib.sha256(lb).hexdigest()}}],
                     "predicateType": "urn:agentmemory:pack:publisher:v1",
                     "predicate": {"id": pid, "version": version, "role": "publisher", "signedAt": "2026-10-26T12:00:00Z", **(predicate_extra or {})},
                     **(statement_extra or {})}
        payload = py.canonical(statement).encode()
        spki = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
        kid = hashlib.sha256(spki).hexdigest()
        signature = base64.b64encode(key.sign(py.pae(payload), ec.ECDSA(hashes.SHA256()))).decode()
        envelope = py.canonical({"payloadType": py.PAYLOAD_TYPE, "payload": base64.b64encode(payload).decode(),
                                 "signatures": [{"keyid": kid, "sig": signature}], **(envelope_extra or {})}).encode()
        return {**content_, py.FILES: lb, f"{py.SIGNATURES}publisher-{kid[:16]}.dsse.json": envelope}
    vectors["envelope-closed-shapes-hold"] = (signed_statement(content), "trust.json", "verified")
    vectors["envelope-extra-field"] = (signed_statement(content, envelope_extra={"note": "x"}), "trust.json", "refused")
    vectors["statement-extra-field"] = (signed_statement(content, statement_extra={"note": "x"}), "trust.json", "refused")
    vectors["predicate-extra-field"] = (signed_statement(content, predicate_extra={"note": "x"}), "trust.json", "refused")

    results, failures = [], 0
    for name, (files, policy_file, expected, *document) in {**{k: (write_zip(v[0]), *v[1:]) for k, v in vectors.items()}, **raw}.items():
        path = VECTORS / f"{name}.ampack"
        path.write_bytes(files)
        documents = ([document[0]] if isinstance(document[0], str) else list(document[0])) if document else []
        extra = [arg for name_ in documents for arg in ("--revocations", str(OUT / name_))]
        dn_code, dn_text = ampack("verify", str(path), "--policy", str(OUT / policy_file), *extra)
        pr = subprocess.run([sys.executable, "-I", str(HERE / "python" / "ampack_verify.py"), str(path), str(OUT / policy_file),
                             *[str(OUT / name_) for name_ in documents]],
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
        # Exit 1 is a refusal; anything else but 0 is a crash, which agrees with nothing.
        dotnet = {0: "verified", 1: "refused"}.get(dn_code, f"error ({dn_code})")
        python = {0: "verified", 1: "refused"}.get(pr.returncode, f"error ({pr.returncode})")
        agree = dotnet == python == expected
        failures += not agree
        results.append({"vector": name, "expected": expected, "dotnet": dotnet, "python": python, "agree": agree,
                        "dotnetSays": dn_text.splitlines()[0] if dn_text else "", "pythonSays": pr.stdout.strip().splitlines()[0] if pr.stdout.strip() else pr.stderr.strip()[-200:],
                        "sha256": hashlib.sha256(files).hexdigest()})
        print(f"{'ok  ' if agree else 'FAIL'} {name:34} expected {expected:8} dotnet {dotnet:8} python {python}")
    # Publication receipts: each verified by `ampack receipt verify` and by the Python verifier's --receipt.
    here = "https://supplier-a.example/ampack/handoff.ampack"
    fed_bytes = write_zip(federated)

    def receipt(pack_bytes, location=here, audience=(buyer,), access="wac", denied=("anonymous",), allowed=(buyer,),
                published="2026-10-26T12:05:00Z", signer=None, archive_sha=None, subjects=None, extra=None, edit=None):
        files_ = py.read_zip(pack_bytes)
        listing = json.loads(files_[py.FILES])
        pred = {"id": listing["id"], "version": listing["version"], "location": location,
                "archiveSha256": archive_sha or hashlib.sha256(pack_bytes).hexdigest(),
                "audience": list(audience) if isinstance(audience, tuple) else audience, "access": access,
                "checked": {"denied": list(denied), "allowed": list(allowed)}, "publishedAt": published, **(extra or {})}
        statement = {"_type": py.STATEMENT, "predicateType": "urn:agentmemory:pack:publication:v1", "predicate": pred,
                     "subject": subjects or [{"name": py.FILES, "digest": {"sha256": hashlib.sha256(files_[py.FILES]).hexdigest()}}]}
        payload = py.canonical(statement).encode()
        who = signer or key
        spki = who.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
        sig = who.sign(py.pae(payload), ec.ECDSA(hashes.SHA256()))
        if edit:
            payload = edit(payload)
        return py.canonical({"payloadType": py.PAYLOAD_TYPE, "payload": base64.b64encode(payload).decode(),
                             "signatures": [{"keyid": hashlib.sha256(spki).hexdigest(), "sig": base64.b64encode(sig).decode()}]}).encode()
    code, text = ampack("receipt", "sign", str(work / "fed-dotnet.ampack"), "--location", here, "--key", str(work / "keys" / "publisher.key.pem"),
                        "--denied", "anonymous," + stranger, "--allowed", buyer, "--out", str(work / "fed-dotnet.receipt.json"))
    assert code == 0, text
    two_bytes = write_zip(two_readers)
    pub_bytes = write_zip(public_federated)
    set_bytes = write_zip(federated_set)
    untrusted_set_bytes = write_zip(untrusted_member_set)
    receipts = {
        "receipt-valid-python": (fed_bytes, receipt(fed_bytes), here, "trust-federated-buyer.json", "verified"),
        **{f"receipt-shape-{how}": (fed_bytes, reshaped(receipt(fed_bytes), how), here, "trust-federated-buyer.json", "refused") for how in SHAPES},
        "receipt-valid-dotnet": (fed_dotnet, (work / "fed-dotnet.receipt.json").read_bytes(), here, "trust-federated-buyer.json", "verified"),
        "receipt-narrower-placement": (two_bytes, receipt(two_bytes), here, "trust-federated-buyer.json", "verified"),
        "receipt-other-location": (fed_bytes, receipt(fed_bytes), "https://mirror.example/ampack/handoff.ampack", "trust-federated-buyer.json", "refused"),
        "receipt-other-bytes": (fed_bytes, receipt(fed_bytes, archive_sha="0" * 64), here, "trust-federated-buyer.json", "refused"),
        "receipt-wider-audience": (fed_bytes, receipt(fed_bytes, audience=(buyer, stranger)), here, "trust-federated-buyer.json", "refused"),
        "receipt-public-placement-of-named-pack": (fed_bytes, receipt(fed_bytes, audience="public", access="public", denied=()), here, "trust-federated-buyer.json", "refused"),
        "receipt-host-outside-placement": (two_bytes, receipt(two_bytes, audience=(stranger,), allowed=()), here, "trust-federated-buyer.json", "refused"),
        # FED-review round 1: the probe's record agrees with the placement.
        "receipt-named-without-anonymous-refused": (fed_bytes, receipt(fed_bytes, denied=()), here, "trust-federated-buyer.json", "refused"),
        "receipt-allowed-outside-audience": (fed_bytes, receipt(fed_bytes, allowed=(stranger,)), here, "trust-federated-buyer.json", "refused"),
        "receipt-audience-member-refused": (fed_bytes, receipt(fed_bytes, denied=("anonymous", buyer), allowed=()), here, "trust-federated-buyer.json", "refused"),
        "receipt-public-valid": (pub_bytes, receipt(pub_bytes, audience="public", access="public", denied=()), here, "trust-federated-buyer.json", "verified"),
        "receipt-public-anonymous-refused": (pub_bytes, receipt(pub_bytes, audience="public", access="public", denied=("anonymous",)), here, "trust-federated-buyer.json", "refused"),
        # FED-review round 2: a public placement refuses nobody, so its receipt lists nobody as refused.
        "receipt-public-webid-refused": (pub_bytes, receipt(pub_bytes, audience="public", access="public", denied=(stranger,)), here, "trust-federated-buyer.json", "refused"),
        # A second key of the same publisher: live (accepted), curator only, revoked, expired.
        "receipt-signed-by-second-bound-key": (fed_bytes, receipt(fed_bytes, signer=mirror), here, "trust-federated-second-key.json", "verified"),
        "receipt-signer-curator-only": (fed_bytes, receipt(fed_bytes, signer=curator), here, "trust-federated-curator.json", "refused"),
        "receipt-signer-revoked": (fed_bytes, receipt(fed_bytes, signer=mirror), here, "trust-federated-second-key-revoked.json", "refused"),
        "receipt-signer-expired": (fed_bytes, receipt(fed_bytes, signer=mirror), here, "trust-federated-second-key-expired.json", "refused"),
        "receipt-subject-other-digest": (fed_bytes, receipt(fed_bytes, subjects=[{"name": py.FILES, "digest": {"sha256": "0" * 64}}]), here, "trust-federated-buyer.json", "refused"),
        # FED-review H2: a set's receipt holds only when every member verifies.
        "receipt-set-valid": (set_bytes, receipt(set_bytes), here, "trust-federated-buyer.json", "verified"),
        "receipt-set-member-untrusted": (untrusted_set_bytes, receipt(untrusted_set_bytes), here, "trust-federated-buyer.json", "refused"),
        "receipt-signer-bound-elsewhere": (fed_bytes, receipt(fed_bytes, signer=mirror), here, "trust-federated-buyer-and-mirror.json", "refused"),
        "receipt-signer-untrusted": (fed_bytes, receipt(fed_bytes, signer=mirror), here, "trust-federated-buyer.json", "refused"),
        "receipt-edited-after-signing": (fed_bytes, receipt(fed_bytes, location="https://mirror.example/ampack/handoff.ampack",
                                                             edit=lambda b: b.replace(b"https://mirror.example/ampack/handoff.ampack", here.encode())),
                                         here, "trust-federated-buyer.json", "refused"),
        "receipt-extra-field": (fed_bytes, receipt(fed_bytes, extra={"note": "x"}), here, "trust-federated-buyer.json", "refused"),
        "receipt-access-public-with-names": (fed_bytes, receipt(fed_bytes, access="public"), here, "trust-federated-buyer.json", "refused"),
        "receipt-access-unknown": (fed_bytes, receipt(fed_bytes, access="ftp"), here, "trust-federated-buyer.json", "refused"),
        "receipt-in-the-future": (fed_bytes, receipt(fed_bytes, published="2099-01-01T00:00:00Z"), here, "trust-federated-buyer.json", "refused"),
        "receipt-time-not-utc": (fed_bytes, receipt(fed_bytes, published="2026-10-26T12:05:00+00:00"), here, "trust-federated-buyer.json", "refused"),
        "receipt-location-fragment": (fed_bytes, receipt(fed_bytes, location=here + "#x"), here + "#x", "trust-federated-buyer.json", "refused"),
        "receipt-two-subjects": (fed_bytes, receipt(fed_bytes, subjects=[{"name": py.FILES, "digest": {"sha256": "0" * 64}}] * 2), here,
                                 "trust-federated-buyer.json", "refused"),
        "receipt-probe-not-webid": (fed_bytes, receipt(fed_bytes, allowed=("anonymous",)), here, "trust-federated-buyer.json", "refused"),
        "receipt-for-unfederated-pack": ((VECTORS / "valid-dotnet.ampack").read_bytes(), receipt((VECTORS / "valid-dotnet.ampack").read_bytes()), here,
                                         "trust-federated-buyer.json", "refused"),
    }
    for name, (pack_bytes, receipt_bytes, location, policy_file, expected) in receipts.items():
        pack_path, receipt_path = VECTORS / f"{name}.ampack", VECTORS / f"{name}.receipt.dsse.json"
        pack_path.write_bytes(pack_bytes)
        receipt_path.write_bytes(receipt_bytes)
        dn_code, dn_text = ampack("receipt", "verify", str(pack_path), str(receipt_path), "--location", location, "--policy", str(OUT / policy_file))
        pr = subprocess.run([sys.executable, "-I", str(HERE / "python" / "ampack_verify.py"), str(pack_path), str(OUT / policy_file),
                             "--receipt", str(receipt_path), "--location", location],
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
        dotnet = {0: "verified", 1: "refused"}.get(dn_code, f"error ({dn_code})")
        python = {0: "verified", 1: "refused"}.get(pr.returncode, f"error ({pr.returncode})")
        agree = dotnet == python == expected
        failures += not agree
        results.append({"vector": name, "expected": expected, "dotnet": dotnet, "python": python, "agree": agree,
                        "dotnetSays": dn_text.splitlines()[0] if dn_text else "", "pythonSays": pr.stdout.strip().splitlines()[-1] if pr.stdout.strip() else pr.stderr.strip()[-200:],
                        "sha256": hashlib.sha256(pack_bytes + receipt_bytes).hexdigest()})
        print(f"{'ok  ' if agree else 'FAIL'} {name:34} expected {expected:8} dotnet {dotnet:8} python {python}")
    # Evaluation certificates (v1.2 §16): each verified by `ampack certificate verify` and by the Python verifier's --certificate.
    code, text = ampack("keygen", "--out", str(work / "keys"), "--name", "evaluator", "--role", "evaluator", "--prefix", "io.example")
    assert code == 0, text
    evaluator_entry = json.loads(text[text.index("{"):])
    evaluator = serialization.load_pem_private_key((work / "keys" / "evaluator.key.pem").read_bytes(), password=None)
    code, text = ampack("keygen", "--out", str(work / "keys"), "--name", "host", "--prefix", "io.example")
    assert code == 0, text
    host = serialization.load_pem_private_key((work / "keys" / "host.key.pem").read_bytes(), password=None)
    certificate_policies = {
        "trust-evaluator.json": {"keys": [entry, evaluator_entry]},
        "trust-evaluator-other-prefix.json": {"keys": [entry, dict(evaluator_entry, idPrefixes=["io.other"])]},
        "trust-evaluator-expired.json": {"keys": [entry, dict(evaluator_entry, notAfter="2020-01-01T00:00:00Z")]},
        "trust-evaluator-revoked.json": {"keys": [entry, evaluator_entry], "revoked": {"digests": [], "keys": [{"keyId": evaluator_entry["keyId"]}]}},
        "trust-evaluator-as-assurer.json": {"keys": [entry, dict(evaluator_entry, roles=["assurer"])]},
        # The key that signs a pack never certifies it too: an evaluator key holds that role alone, as a revoker's does.
        "trust-evaluator-also-publisher.json": {"keys": [entry, dict(evaluator_entry, roles=["evaluator", "publisher"])]},
    }
    for name_, body in certificate_policies.items():
        (OUT / name_).write_text(json.dumps({"format": "agentmemory.pack.trust/1.1", **body}, separators=(",", ":")), encoding="utf-8")
    federated_policy("trust-federated-buyer-evaluator.json", more=(evaluator_entry,))
    trial = {"measure": "test-impact", "tasks": 40, "baseline": "none", "targets": {"hitRate": 0.5}}
    certified = signed({**content, "evals/trial.json": json.dumps(trial).encode() + b"\n"})

    def certificate(files_, signer=None, evaluated="2026-10-27T12:00:00Z", checks=None, with_arm=None, without=None, passed=True, extra=None,
                    aef=None, subjects=None, edit=None, pid_=None, version_=None, trial_=None, payload_override=None):
        listing = json.loads(files_[py.FILES])
        pred = {"id": pid_ or listing["id"], "version": version_ or listing["version"], "evaluator": "Kit evaluator",
                "tool": {"name": "kit", "version": "1.0.0"}, "evaluatedAt": evaluated,
                "trial": trial_ or {"measure": "test-impact", "baseline": "none", "taskSource": "the kit's synthetic tasks"},
                "checks": checks or [{"target": "hitRate", "requiredMilli": 500, "observedMilli": 600, "met": True}],
                "with": with_arm or {"tasks": 40, "hits": 24, "offered": 120, "correct": 30, "p95Micros": 12500}, "passed": passed, **(extra or {})}
        if without is not None:
            pred["without"] = without
        if aef is not None:
            pred["aef"] = aef
        statement = {"_type": py.STATEMENT, "predicateType": py.CERTIFICATE, "predicate": pred,
                     "subject": subjects or [{"name": py.FILES, "digest": {"sha256": hashlib.sha256(files_[py.FILES]).hexdigest()}}]}
        payload = payload_override(statement) if payload_override else py.canonical(statement).encode()
        who = signer or evaluator
        spki = who.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
        sig = who.sign(py.pae(payload), ec.ECDSA(hashes.SHA256()))
        if edit:
            payload = edit(payload)
        return py.canonical({"payloadType": py.PAYLOAD_TYPE, "payload": base64.b64encode(payload).decode(),
                             "signatures": [{"keyid": hashlib.sha256(spki).hexdigest(), "sig": base64.b64encode(sig).decode()}]}).encode()

    certificate_path = f"META/attestations/{stem}.certificate.dsse.json"

    def carrying(envelope, run=None, files_=certified):
        out = {**files_, certificate_path: envelope}
        for path_, bytes_ in (run or {}).items():
            out[f"META/attestations/{stem}.aef/{path_}"] = bytes_
        return write_zip(out)

    run = {"run.json": json.dumps({"schemaVersion": "1.0", "runId": "trial.io.example.notes.1.0.0.20261028T120000000Z"}).encode(),
           "results.ndjson": b'{"lane":"with-pack","passed":true}\n', "seal.json": b"{}", "attestation.dsse.json": b"{}"}
    aef = {"runId": "trial.io.example.notes.1.0.0.20261028T120000000Z", "runHash": py.run_hash(run)}
    tampered = {**run, "results.ndjson": b'{"lane":"with-pack","passed":false}\n'}
    certified_bytes = write_zip(certified)
    other = signed({**content, "evals/trial.json": json.dumps(trial).encode() + b"\n", "records/extra.jsonl": content[record]})

    # One certificate signed by .NET itself: ampack certify from a host-signed trial decision for exactly this pack.
    (work / "cert-pack.ampack").write_bytes(certified_bytes)
    code, text = ampack("verify", str(work / "cert-pack.ampack"), "--policy", str(OUT / "trust-evaluator.json"))
    assert code == 0, text
    declarations = next(line.split()[1] for line in text.splitlines() if line.strip().startswith("declarations "))
    pack_digest = "sha256:" + hashlib.sha256(certified[py.FILES]).hexdigest()
    decision = {"ModuleId": pid, "Version": version, "ManifestDigest": declarations, "PackDigest": pack_digest, "Active": True,
                "Reason": "every target met", "DecidedAt": "2026-10-27T12:00:00+00:00",
                "Checks": [{"Target": "hitRate", "Required": 0.5, "Observed": 0.6, "Met": True, "Estimate": None}],
                "With": {"Tasks": 40, "Hits": 24, "Offered": 120, "Correct": 30, "P95Ms": 12.5},
                "Without": {"Tasks": 40, "Hits": 0, "Offered": 0, "Correct": 0, "P95Ms": 2.5},
                # Review 3: certify takes only a trial at each commit's parent.
                "History": "parent"}
    decision_payload = json.dumps({"_type": py.STATEMENT, "subject": [{"name": f"{pid}@{version}", "digest": {"sha256": pack_digest[7:]}}],
                                   "predicateType": "urn:agentmemory:pack:trial:v1", "predicate": decision}).encode()
    (work / "decisions").mkdir()
    (work / "decisions" / "notes.dsse.json").write_text(json.dumps({
        "payloadType": py.PAYLOAD_TYPE, "payload": base64.b64encode(decision_payload).decode(),
        "signatures": [{"keyid": "host", "sig": base64.b64encode(host.sign(py.pae(decision_payload), ec.ECDSA(hashes.SHA256()))).decode()}]}),
        encoding="utf-8")
    code, text = ampack("certify", str(work / "cert-pack.ampack"), "--policy", str(OUT / "trust-evaluator.json"),
                        "--activations", str(work / "decisions"), "--host-key", str(work / "keys" / "host.pub.pem"),
                        "--key", str(work / "keys" / "evaluator.key.pem"), "--evaluator", "Kit evaluator (.NET)",
                        "--task-source", "the kit's synthetic tasks", "--out", str(work / "cert-dotnet.ampack"))
    assert code == 0, text
    dotnet_certified = (work / "cert-dotnet.ampack").read_bytes()
    dotnet_certificate = next(p for p in py.read_zip(dotnet_certified) if p.startswith(py.ATTESTATIONS))

    certificates = {  # name -> (pack bytes, certificate inside the pack or None, certificate file or None, policy, expected)
        "certificate-valid-python": (carrying(certificate(certified)), certificate_path, None, "trust-evaluator.json", "verified"),
        "certificate-valid-dotnet": (dotnet_certified, dotnet_certificate, None, "trust-evaluator.json", "verified"),
        "certificate-as-a-separate-file": (certified_bytes, None, certificate(certified), "trust-evaluator.json", "verified"),
        **{f"certificate-shape-{how}": (certified_bytes, None, reshaped(certificate(certified), how), "trust-evaluator.json", "refused") for how in SHAPES},
        "certificate-honest-failure": (carrying(certificate(certified, checks=[{"target": "hitRate", "requiredMilli": 500, "observedMilli": 400, "met": False}],
                                                            passed=False)), certificate_path, None, "trust-evaluator.json", "verified"),
        "certificate-with-host-baseline": (carrying(certificate(certified, trial_={"measure": "test-impact", "baseline": "host", "confidenceMilli": 950,
                                                                                    "taskSource": "the kit's synthetic tasks"},
                                                                without={"tasks": 40, "hits": 20, "offered": 120, "correct": 24, "p95Micros": 9000},
                                                                checks=[{"target": "hitRate", "requiredMilli": 500, "observedMilli": 520, "met": True, "estimateMilli": 600},
                                                                        {"target": "lift", "requiredMilli": 0, "observedMilli": 100, "met": True}])),
                                           certificate_path, None, "trust-evaluator.json", "verified"),
        # The AEF run beside the certificate: named by its run hash, exactly.
        "certificate-aef-run-matches": (carrying(certificate(certified, aef=aef), run), certificate_path, None, "trust-evaluator.json", "verified"),
        "certificate-aef-run-named-elsewhere": (carrying(certificate(certified, aef=aef)), certificate_path, None, "trust-evaluator.json", "verified"),
        "certificate-aef-run-tampered": (carrying(certificate(certified, aef=aef), tampered), certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-aef-run-not-named": (carrying(certificate(certified), run), certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-aef-run-json-outside-profile": (carrying(certificate(certified, aef={**aef, "runHash": py.run_hash({**run, "run.json": b'{"runId":"another.run","runId":"another.run"}'})}),
                                                     {**run, "run.json": b'{"runId":"another.run","runId":"another.run"}'}),
                                                     certificate_path, None, "trust-evaluator.json", "verified"),
        "certificate-aef-run-other-id": (carrying(certificate(certified, aef={**aef, "runId": "another.run"}), run), certificate_path, None,
                                         "trust-evaluator.json", "refused"),
        # Whose certificate: a key the host names as evaluator for this pack's id, valid when it evaluated and now.
        "certificate-key-not-trusted": (carrying(certificate(certified)), certificate_path, None, "trust.json", "refused"),
        "certificate-key-other-prefix": (carrying(certificate(certified)), certificate_path, None, "trust-evaluator-other-prefix.json", "refused"),
        "certificate-key-expired": (carrying(certificate(certified)), certificate_path, None, "trust-evaluator-expired.json", "refused"),
        "certificate-key-revoked": (carrying(certificate(certified)), certificate_path, None, "trust-evaluator-revoked.json", "refused"),
        "certificate-key-assurer-not-evaluator": (carrying(certificate(certified)), certificate_path, None, "trust-evaluator-as-assurer.json", "refused"),
        # Review 1, H3: a certified set verifies whole, member by member, before its certificate is read.
        "certificate-set-valid": (carrying(certificate(federated_set), files_=federated_set), certificate_path, None,
                                  "trust-federated-buyer-evaluator.json", "verified"),
        "certificate-set-member-untrusted": (carrying(certificate(untrusted_member_set), files_=untrusted_member_set), certificate_path, None,
                                             "trust-federated-buyer-evaluator.json", "refused"),
        "certificate-path-of-a-run-file": (carrying(certificate(certified, aef=aef), run), f"META/attestations/{stem}.aef/run.json", None,
                                           "trust-evaluator.json", "refused"),
        "certificate-policy-evaluator-also-publisher": (carrying(certificate(certified)), certificate_path, None, "trust-evaluator-also-publisher.json", "refused"),
        "certificate-by-the-publisher": (carrying(certificate(certified, signer=key)), certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-in-the-future": (carrying(certificate(certified, evaluated="2099-01-01T00:00:00Z")), certificate_path, None, "trust-evaluator.json", "refused"),
        # For exactly this pack.
        "certificate-for-another-pack": (carrying(certificate(other)), certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-other-version": (carrying(certificate(certified, version_="1.0.1")), certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-two-subjects": (carrying(certificate(certified, subjects=[{"name": py.FILES, "digest": {"sha256": hashlib.sha256(certified[py.FILES]).hexdigest()}}] * 2)),
                                     certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-edited-after-signing": (carrying(certificate(certified, edit=lambda b: b.replace(b'"observedMilli":600', b'"observedMilli":900'))),
                                             certificate_path, None, "trust-evaluator.json", "refused"),
        # A verdict that agrees with its record.
        "certificate-passed-with-a-missed-check": (carrying(certificate(certified, checks=[{"target": "hitRate", "requiredMilli": 500, "observedMilli": 400, "met": False}])),
                                                   certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-passed-on-nine-tasks": (carrying(certificate(certified, with_arm={"tasks": 9, "hits": 9, "offered": 9, "correct": 9, "p95Micros": 1})),
                                             certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-arms-differ": (carrying(certificate(certified, without={"tasks": 39, "hits": 1, "offered": 1, "correct": 1, "p95Micros": 1})),
                                    certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-host-baseline-without-arm": (carrying(certificate(certified, trial_={"measure": "test-impact", "baseline": "host", "taskSource": "x"})),
                                                  certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-hits-above-tasks": (carrying(certificate(certified, with_arm={"tasks": 40, "hits": 41, "offered": 1, "correct": 1, "p95Micros": 1})),
                                         certificate_path, None, "trust-evaluator.json", "refused"),
        # Strict shape: integers only, known fields, known targets, one line of text.
        "certificate-fraction-not-integer": (carrying(certificate(certified, payload_override=lambda st: json.dumps(st, separators=(",", ":")).replace(
            '"requiredMilli":500', '"requiredMilli":0.5').encode())), certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-extra-field": (carrying(certificate(certified, extra={"note": "x"})), certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-unknown-target": (carrying(certificate(certified, checks=[{"target": "recall", "requiredMilli": 500, "observedMilli": 600, "met": True}])),
                                       certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-confidence-unknown": (carrying(certificate(certified, trial_={"measure": "test-impact", "baseline": "none", "confidenceMilli": 800, "taskSource": "x"})),
                                           certificate_path, None, "trust-evaluator.json", "refused"),
        "certificate-evaluator-two-lines": (carrying(certificate(certified, extra={"evaluator": "Kit\nevaluator"})), certificate_path, None,
                                            "trust-evaluator.json", "refused"),
    }
    for name, (pack_bytes, inside, external, policy_file, expected) in certificates.items():
        pack_path = VECTORS / f"{name}.ampack"
        pack_path.write_bytes(pack_bytes)
        target = inside
        if external is not None:
            target = str(VECTORS / f"{name}.certificate.dsse.json")
            Path(target).write_bytes(external)
        dn_code, dn_text = ampack("certificate", "verify", str(pack_path), "--policy", str(OUT / policy_file), "--certificate", target)
        pr = subprocess.run([sys.executable, "-I", str(HERE / "python" / "ampack_verify.py"), str(pack_path), str(OUT / policy_file),
                             "--certificate", target],
                            capture_output=True, text=True, encoding="utf-8", errors="replace")
        dotnet = {0: "verified", 1: "refused"}.get(dn_code, f"error ({dn_code})")
        python = {0: "verified", 1: "refused"}.get(pr.returncode, f"error ({pr.returncode})")
        agree = dotnet == python == expected
        failures += not agree
        results.append({"vector": name, "expected": expected, "dotnet": dotnet, "python": python, "agree": agree,
                        "dotnetSays": dn_text.splitlines()[0] if dn_text else "", "pythonSays": pr.stdout.strip().splitlines()[-1] if pr.stdout.strip() else pr.stderr.strip()[-200:],
                        "sha256": hashlib.sha256(pack_bytes + (external or b"")).hexdigest()})
        print(f"{'ok  ' if agree else 'FAIL'} {name:34} expected {expected:8} dotnet {dotnet:8} python {python}")
    (OUT / "results.json").write_text(json.dumps({"format": "agentmemory.pack.conformance/1.2", "vectors": results,
                                                  "agreed": len(results) - failures, "total": len(results)}, indent=1) + "\n", encoding="utf-8")
    shutil.rmtree(work, ignore_errors=True)
    print(f"{len(results) - failures}/{len(results)} vectors: both verifiers agree with the expected outcome")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
