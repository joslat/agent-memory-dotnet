"""A second, independent verifier for memory packs (format v1.1), in Python, to prove the format is language-neutral.

It shares no code with the .NET implementation: it reads the strict ZIP profile itself, checks the canonical
META/files.json, every file's length and SHA-256, and the DSSE envelopes of in-toto statements against a trust policy.
It must agree with `ampack verify` on every vector in ../vectors (see run_vectors.py).

usage: python ampack_verify.py <pack.ampack> <trust.json>      exit 0 verified, 1 refused
Needs: Python 3.10+, the `cryptography` package.
"""
import base64
import hashlib
import json
import re
import struct
import sys
import unicodedata
import zlib
from datetime import datetime, timezone

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec

FILES = "META/files.json"
SIGNATURES = "META/signatures/"
FILES_FORMAT = "agentmemory.pack.files/1.1"
PACK_FORMAT = "agentmemory.pack/1.2"
PAYLOAD_TYPE = "application/vnd.in-toto+json"
STATEMENT = "https://in-toto.io/Statement/v1"
ROLES = {"publisher", "curator", "assurer"}
SEGMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}")
DEVICES = {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
ID = re.compile(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*){2,}")
SEMVER = re.compile(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?")
MAX_FILES, MAX_FILE, MAX_TOTAL, MAX_RATIO = 4096, 64 << 20, 256 << 20, 250


class Refused(Exception):
    pass


def safe_path(path):
    if not 0 < len(path) <= 255:
        return False
    parts = path.split("/")
    return len(parts) <= 8 and all(SEGMENT.fullmatch(p) and not p.endswith(".") and p.split(".")[0].upper() not in DEVICES for p in parts)


def read_zip(data):
    """The strict ZIP profile: contiguous, sorted, local equals central, stored or deflate, nothing optional."""
    if len(data) < 22 or data[-22:-18] != b"PK\x05\x06":
        raise Refused("the archive does not end with the end record")
    _, disk, cd_disk, n, total, cd_size, cd_start, comment = struct.unpack("<IHHHHIIH", data[-22:])
    if disk or cd_disk or comment or n != total or n == 0xFFFF:
        raise Refused("the end record is not in the profile")
    if n > MAX_FILES or cd_start + cd_size != len(data) - 22:
        raise Refused("the central directory is not where the end record says")
    files, cursor, pos, previous, folded, unpacked = {}, 0, cd_start, None, set(), 0
    for _ in range(n):
        h = data[pos:pos + 46]
        (sig, made, need, flags, method, time, date, crc, csize, size, nlen, xlen, clen, dsk, iattr, eattr, off) = struct.unpack("<IHHHHHHIIIHHHHHII", h)
        name_bytes = data[pos + 46:pos + 46 + nlen]
        if sig != 0x02014B50 or any(b < 0x20 or b > 0x7E for b in name_bytes):
            raise Refused("a central entry is malformed")
        name = name_bytes.decode("ascii")
        if (made >> 8) or (made & 0xFF) > 63 or need not in (10, 20) or flags or method not in (0, 8) or time or date != 0x21:
            raise Refused(f"entry '{name}' is not in the profile")
        if xlen or clen or dsk or iattr or eattr or name.endswith("/") or not safe_path(name):
            raise Refused(f"entry '{name}' is not in the profile")
        if (previous is not None and previous >= name) or name.lower() in folded or off != cursor:
            raise Refused(f"entry '{name}' is out of order, repeated or not contiguous")
        local = data[cursor:cursor + 30]
        if local[:4] != b"PK\x03\x04" or local[4:30] != struct.pack("<HHHHHIIIHH", need, flags, method, time, date, crc, csize, size, nlen, 0) \
                or data[cursor + 30:cursor + 30 + nlen] != name_bytes:
            raise Refused(f"entry '{name}' has a local header that differs from the central directory")
        start = cursor + 30 + nlen
        if start + csize > cd_start or size > MAX_FILE or (method == 8 and csize and size // csize > MAX_RATIO) or (method == 0 and csize != size):
            raise Refused(f"entry '{name}' breaks a size rule")
        unpacked += size
        if unpacked > MAX_TOTAL:
            raise Refused("the archive unpacks to too much")
        raw = data[start:start + csize]
        if method == 8:
            d = zlib.decompressobj(-15)
            content = d.decompress(raw, size + 1)
            if len(content) != size or d.unconsumed_tail or not d.eof:
                raise Refused(f"entry '{name}' does not inflate to its declared size")
        else:
            content = raw
        if zlib.crc32(content) & 0xFFFFFFFF != crc:
            raise Refused(f"entry '{name}' fails its CRC")
        files[name] = content
        previous, cursor, pos = name, start + csize, pos + 46 + nlen
        folded.add(name.lower())
    if cursor != cd_start or pos != cd_start + cd_size:
        raise Refused("there are bytes outside the entries")
    return files


def no_duplicates(pairs):
    keys = [k for k, _ in pairs]
    if len(keys) != len(set(keys)):
        raise Refused("a JSON object repeats a key")
    return dict(pairs)


def profile(data, integers_only=False):
    """The JSON profile: no BOM, LF only, no duplicate keys, NFC strings, integers within 2^53."""
    if data.startswith(b"\xef\xbb\xbf") or b"\r" in data:
        raise Refused("a JSON file has a BOM or a carriage return")
    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=no_duplicates, parse_constant=lambda c: (_ for _ in ()).throw(Refused(c)))
    except (UnicodeDecodeError, ValueError) as e:
        raise Refused(f"a JSON file is not valid: {e}")

    def walk(v):
        if isinstance(v, dict):
            for k, x in v.items():
                walk(k)
                walk(x)
        elif isinstance(v, list):
            for x in v:
                walk(x)
        elif isinstance(v, str):
            if unicodedata.normalize("NFC", v) != v or any(0xD800 <= ord(c) <= 0xDFFF for c in v):
                raise Refused("a JSON string is not NFC")
        elif isinstance(v, bool) or v is None:
            pass
        elif isinstance(v, int):
            if abs(v) > 2 ** 53 - 1:
                raise Refused("a JSON integer is beyond 2^53")
        elif isinstance(v, float) and integers_only:
            raise Refused("a canonical file holds a fraction")
    walk(value)
    return value


def canonical(value):
    """RFC 8785 for strings, integers, booleans, null, arrays and objects (keys sorted by UTF-16 code units)."""
    if isinstance(value, dict):
        items = sorted(value.items(), key=lambda kv: kv[0].encode("utf-16-be"))
        return "{" + ",".join(canonical(k) + ":" + canonical(v) for k, v in items) + "}"
    if isinstance(value, list):
        return "[" + ",".join(canonical(v) for v in value) + "]"
    if isinstance(value, str):
        out = ['"']
        for c in value:
            out.append({'"': '\\"', "\\": "\\\\", "\b": "\\b", "\f": "\\f", "\n": "\\n", "\r": "\\r", "\t": "\\t"}.get(c)
                       or (f"\\u{ord(c):04x}" if ord(c) < 0x20 else c))
        return "".join(out) + '"'
    if value is True:
        return "true"
    if value is False:
        return "false"
    if value is None:
        return "null"
    return str(value)


def pae(payload):
    return f"DSSEv1 {len(PAYLOAD_TYPE)} {PAYLOAD_TYPE} {len(payload)} ".encode() + payload


def verify(files, policy, now):
    for p in files:
        if not safe_path(p):
            raise Refused(f"'{p}' is not a safe pack path")
    if FILES not in files:
        raise Refused("there is no META/files.json")
    list_bytes = files[FILES]
    listing = profile(list_bytes, integers_only=True)
    if canonical(listing).encode("utf-8") != list_bytes:
        raise Refused("META/files.json is not canonical")
    if set(listing) != {"format", "id", "version", "files"} or listing["format"] != FILES_FORMAT:
        raise Refused("META/files.json is not a v1.1 files list")
    pid, version = listing["id"], listing["version"]
    if not ID.fullmatch(pid) or len(pid) > 64 or not SEMVER.fullmatch(version):
        raise Refused("META/files.json names no valid id or version")
    listed = {}
    for entry in listing["files"]:
        listed[entry["path"]] = entry
    paths = [e["path"] for e in listing["files"]]
    if paths != sorted(paths) or len(set(paths)) != len(paths):
        raise Refused("META/files.json is not sorted or repeats a path")
    content = {p: b for p, b in files.items() if p != FILES and not p.startswith(SIGNATURES)}
    if set(content) != set(listed):
        raise Refused("the pack does not hold exactly the listed files")
    for p, e in listed.items():
        if len(content[p]) != e["length"] or hashlib.sha256(content[p]).hexdigest() != e["sha256"]:
            raise Refused(f"'{p}' does not match META/files.json")
    for p, b in content.items():
        if p.endswith(".json"):
            profile(b, integers_only=(p == "META/lock.json"))
        elif p.endswith(".jsonl") and b:
            if not b.endswith(b"\n"):
                raise Refused(f"'{p}' does not end with a line feed")
            for line in b[:-1].split(b"\n"):
                if not line:
                    raise Refused(f"'{p}' has an empty line")
                profile(line)
    manifest = profile(content.get("pack.json", b"null"))
    if not isinstance(manifest, dict) or manifest.get("format") != PACK_FORMAT or manifest.get("id") != pid or manifest.get("version") != version:
        raise Refused("pack.json does not name the same pack as META/files.json")
    digest = "sha256:" + hashlib.sha256(list_bytes).hexdigest()
    if digest in policy.get("revoked", {}).get("digests", []):
        raise Refused("the pack is revoked")

    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    revoked_keys = {r["keyId"] for r in policy.get("revoked", {}).get("keys", [])}
    roles = set()
    for path in sorted(p for p in files if p.startswith(SIGNATURES)):
        try:
            env = profile(files[path])
            if env.get("payloadType") != PAYLOAD_TYPE:
                continue
            payload = base64.b64decode(env["payload"], validate=True)
            st = profile(payload)
            pred = st["predicate"]
            role = pred["role"]
            ok = (st["_type"] == STATEMENT and len(st["subject"]) == 1 and st["subject"][0]["name"] == FILES
                  and st["subject"][0]["digest"]["sha256"] == hashlib.sha256(list_bytes).hexdigest()
                  and role in ROLES and st["predicateType"] == f"urn:agentmemory:pack:{role}:v1"
                  and pred["id"] == pid and pred["version"] == version)
            signed_at = datetime.strptime(pred["signedAt"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
        except (Refused, KeyError, TypeError, ValueError):
            continue
        if not ok:
            continue
        for s in env.get("signatures", []):
            key = keys.get(s.get("keyid"))
            if key is None or key["keyId"] in revoked_keys or role not in key["roles"]:
                continue
            if not any(pid == pre or pid.startswith(pre.rstrip(".") + ".") for pre in key["idPrefixes"]):
                continue
            public = serialization.load_pem_public_key(key["publicKey"].encode())
            spki = public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
            if hashlib.sha256(spki).hexdigest() != key["keyId"] or signed_at.timestamp() > now.timestamp() + 300:
                continue
            try:
                public.verify(base64.b64decode(s["sig"]), pae(payload), ec.ECDSA(hashes.SHA256()))
                roles.add(role)
            except InvalidSignature:
                continue
    missing = set(policy.get("requireRoles", ["publisher"])) - roles
    if missing:
        raise Refused("no valid signature in role " + ", ".join(sorted(missing)))
    return pid, version, digest


def main(argv):
    pack_path, policy_path = argv[1], argv[2]
    policy = json.load(open(policy_path, encoding="utf-8"))
    try:
        pid, version, digest = verify(read_zip(open(pack_path, "rb").read()), policy, datetime.now(timezone.utc))
    except Refused as e:
        print(f"refused: {e}")
        return 1
    print(f"verified {pid} {version} {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
