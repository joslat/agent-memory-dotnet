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
    vectors["second-signature-entry-not-base64"] = (signed_with(content, extra_signatures=[{"keyid": entry["keyId"], "sig": "abc"}]), "trust.json", "verified")
    vectors["second-signature-entry-without-sig"] = (signed_with(content, extra_signatures=[{"keyid": entry["keyId"]}]), "trust.json", "verified")
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
    vectors["extra-envelope-of-wrong-types"] = ({**signed(content), py.SIGNATURES + "zz.dsse.json": odd}, "trust.json", "verified")
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
    vectors["signature-entry-keyid-a-list"] = (signed_with(content, extra_signatures=({"keyid": ["x"], "sig": "AAAA"},)), "trust.json", "verified")
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
    raw["deflate-without-its-final-block"] = (write_deflated_zip(s, unfinished={record}), "trust.json", "refused")

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
    (OUT / "results.json").write_text(json.dumps({"format": "agentmemory.pack.conformance/1.2", "vectors": results,
                                                  "agreed": len(results) - failures, "total": len(results)}, indent=1) + "\n", encoding="utf-8")
    shutil.rmtree(work, ignore_errors=True)
    print(f"{len(results) - failures}/{len(results)} vectors: both verifiers agree with the expected outcome")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
