"""A second, independent verifier for memory packs (format v1.2), in Python, to prove the format is language-neutral.

It shares no code with the .NET implementation: it reads the strict ZIP profile itself, checks the canonical
META/files.json, every file's length and SHA-256, the DSSE envelopes of in-toto statements against a trust policy (key
scopes, roles, validity windows, revocations and their expiry), pack.json (fields, kinds, enumerations, validUntil,
source), what each kind must hold, a composite's lock against its members, and a pack set with each member on its own.
It must agree with `ampack verify` on every vector in ../vectors (see run_vectors.py).

usage: python ampack_verify.py <pack.ampack> <trust.json> [<revocations.json>]      exit 0 verified, 1 refused
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
# Every field pack.json may hold; anything the format does not define goes in "extensions" (PACK-strict).
PACK_FIELDS = {"format", "id", "version", "kind", "sdk", "requires", "display", "containableBy", "contains", "tier", "licence",
               "validUntil", "protection", "code", "source", "extensions"}
UNDERSTOOD_EXTENSIONS = set()
KINDS = {"knowledge", "definition", "snapshot", "composite", "set"}
TIERS = {"open", "curated", "premium", "private", "hosted"}
PROTECTIONS = {"open", "audience", "licensed", "attested", "hosted"}
CODE_TIERS = {"E0", "E1", "E2"}
LOCK = "META/lock.json"
LOCK_FORMAT = "agentmemory.pack.lock/1.1"
COMMIT = re.compile(r"[0-9a-f]{7,64}")
# RFC 3339 date-times, the one form a time in a pack or a policy takes.
REVOCATIONS_FORMAT = "agentmemory.pack.revocations/1.2"
REVOCATIONS_TYPE = "application/vnd.agentmemory.revocations+json"
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
KEY_ID = re.compile(r"[0-9a-f]{64}")
INSTANT = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})")
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
            if d.unused_data:
                raise Refused(f"entry '{name}' has bytes after the end of its deflate stream")
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


def instant(text):
    """An RFC 3339 date-time as an aware datetime, or None when the text is not one."""
    if not isinstance(text, str) or not INSTANT.fullmatch(text):
        return None
    head, _, rest = text.partition(".")
    if rest:
        digits = "".join(c for c in rest if c.isdigit())
        text = head + "." + (digits + "000000")[:6] + rest[len(digits):]
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError:
        return None


def policy_time(value):
    """A time in the host's policy: absent, or an RFC 3339 date-time; anything else is a malformed policy."""
    if value is None:
        return None
    parsed = instant(value)
    if parsed is None:
        raise Refused("the trust policy has a time that is not an RFC 3339 date-time")
    return parsed


def apply_revocations(policy, data, now):
    """A signed revocation document (v1.2 §8) applied to the policy: signed by a revoker key valid now, its revocations
    added and never removed, its expiry the policy's when earlier."""
    env = profile(data)
    if not isinstance(env, dict) or env.get("payloadType") != REVOCATIONS_TYPE or not isinstance(env.get("payload"), str) \
            or not isinstance(env.get("signatures"), list):
        raise Refused("the revocation document is not a DSSE envelope of a revocation document")
    try:
        payload = base64.b64decode(env["payload"], validate=True)
    except ValueError:
        raise Refused("the revocation document has a payload that is not base64")
    signed = f"DSSEv1 {len(REVOCATIONS_TYPE)} {REVOCATIONS_TYPE} {len(payload)} ".encode() + payload
    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    revoked_keys = {r["keyId"] for r in policy.get("revoked", {}).get("keys", [])}
    verified = False
    for s in env["signatures"]:
        if not isinstance(s, dict) or not isinstance(s.get("keyid"), str) or not isinstance(s.get("sig"), str):
            continue
        key = keys.get(s["keyid"])
        if key is None or "revoker" not in key.get("roles", []) or key["keyId"] in revoked_keys:
            continue
        not_before, not_after = policy_time(key.get("notBefore")), policy_time(key.get("notAfter"))
        if (not_before is not None and now < not_before) or (not_after is not None and now > not_after):
            continue
        public = serialization.load_pem_public_key(key["publicKey"].encode())
        spki = public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
        if hashlib.sha256(spki).hexdigest() != key["keyId"]:
            continue
        try:
            public.verify(base64.b64decode(s["sig"]), signed, ec.ECDSA(hashes.SHA256()))
            verified = True
            break
        except (InvalidSignature, ValueError):
            continue
    if not verified:
        raise Refused("the revocation document is not signed by a key the trust policy names as revoker and that is valid now")
    doc = profile(payload)
    if not isinstance(doc, dict) or set(doc) - {"format", "issuedAt", "expires", "digests", "keys"} or doc.get("format") != REVOCATIONS_FORMAT:
        raise Refused("the revocation document is not a v1.2 revocation document")
    issued, expires = instant(doc.get("issuedAt")), instant(doc.get("expires"))
    if issued is None or expires is None or expires <= issued:
        raise Refused("the revocation document needs issuedAt and expires, expiring after it was issued")
    if issued.timestamp() > now.timestamp() + 300:
        raise Refused("the revocation document claims to be issued in the future")
    digests, revoked = doc.get("digests"), doc.get("keys")
    if not isinstance(digests, list) or not all(isinstance(d, str) and DIGEST.fullmatch(d) for d in digests):
        raise Refused("the revocation document has a malformed digests list")
    if not isinstance(revoked, list) or not all(isinstance(k, dict) and not (set(k) - {"keyId", "compromisedAt"}) and isinstance(k.get("keyId"), str)
                                                 and KEY_ID.fullmatch(k["keyId"]) and ("compromisedAt" not in k or instant(k["compromisedAt"]) is not None)
                                                 for k in revoked):
        raise Refused("the revocation document has a malformed keys list")
    merged = dict(policy)
    current = dict(policy.get("revoked", {}))
    current["digests"] = sorted(set(current.get("digests", [])) | set(digests))
    by_id = {k["keyId"]: k for k in current.get("keys", [])}
    for k in revoked:
        known = by_id.get(k["keyId"])
        if known is None or ("compromisedAt" in known and ("compromisedAt" not in k or instant(k["compromisedAt"]) < instant(known["compromisedAt"]))):
            by_id[k["keyId"]] = k
    current["keys"] = [by_id[i] for i in sorted(by_id)]
    old = policy_time(current.get("expires"))
    if old is None or expires < old:
        current["expires"] = doc["expires"]
    merged["revoked"] = current
    return merged


def valid_id(value):
    return isinstance(value, str) and len(value) <= 64 and ID.fullmatch(value) is not None


def manifest_checks(manifest, content, now):
    """pack.json's fields and what its kind must hold (v1.2 §2, §3, §5), as `ampack verify` checks them."""
    kind = manifest.get("kind")
    if kind not in KINDS:
        raise Refused("pack.json needs a known kind")
    for field, allowed in (("tier", TIERS), ("protection", PROTECTIONS), ("code", CODE_TIERS)):
        if field in manifest and manifest[field] not in allowed:
            raise Refused(f"pack.json has an unknown {field}")
    for field in ("contains", "containableBy"):
        value = manifest.get(field, [])
        if not isinstance(value, list) or not all(valid_id(v) for v in value):
            raise Refused(f"pack.json has a malformed '{field}'")
    if "validUntil" in manifest:
        until = instant(manifest["validUntil"])
        if until is None:
            raise Refused("pack.json has a validUntil that is not a date and time")
        if until < now:
            raise Refused("the pack's validUntil has passed")
    if "source" in manifest:
        source = manifest["source"]
        if not isinstance(source, dict) or not all(k in ("repository", "commit", "builtAt") and isinstance(v, str) and 0 < len(v) <= 200
                                                   for k, v in source.items()):
            raise Refused("pack.json has a malformed 'source'")
        if "builtAt" in source and instant(source["builtAt"]) is None:
            raise Refused("pack.json has a source.builtAt that is not a date and time")
        if "commit" in source and not COMMIT.fullmatch(source["commit"]):
            raise Refused("pack.json has a source.commit that is not 7 to 64 hexadecimal characters")
    records = [p for p in content if p.startswith("records/") and p.endswith(".jsonl")]
    if kind in ("knowledge", "snapshot") and not records:
        raise Refused(f"a {kind} pack holds records")
    if kind == "definition" and records:
        raise Refused("a definition pack holds no records")
    if kind == "composite":
        if LOCK not in content or not manifest.get("contains"):
            raise Refused("a composite names what it contains and pins it in META/lock.json")
        composite_checks(manifest, content)
    if kind == "set" and not any(p.startswith("packs/") for p in content):
        raise Refused("a pack set carries its members under packs/")
    if manifest.get("protection", "open") != "open" and "META/seal.json" not in content:
        raise Refused("a protected pack carries META/seal.json")


def composite_checks(manifest, content):
    """The lock is canonical and strict; members.json, pack.json and the lock name the same members."""
    lock_bytes = content[LOCK]
    lock = profile(lock_bytes, integers_only=True)
    if canonical(lock).encode("utf-8") != lock_bytes:
        raise Refused("META/lock.json is not canonical")
    if not isinstance(lock, dict) or set(lock) != {"format", "members"} or lock["format"] != LOCK_FORMAT or not isinstance(lock["members"], list):
        raise Refused("META/lock.json is not a lock: format and members, nothing else")
    pinned = []
    for m in lock["members"]:
        if not isinstance(m, dict) or set(m) != {"id", "version", "digest"} or not valid_id(m["id"]) \
                or not isinstance(m["version"], str) or not SEMVER.fullmatch(m["version"]) \
                or not isinstance(m["digest"], str) or not m["digest"].startswith("sha256:") or len(m["digest"]) != 71:
            raise Refused("META/lock.json has a malformed member")
        pinned.append(m["id"])
    if len(set(pinned)) != len(pinned):
        raise Refused("META/lock.json pins one id twice")
    if "members.json" not in content:
        raise Refused("a composite pack carries members.json")
    try:
        named = [m["id"] for m in profile(content["members.json"])["members"]]
    except (Refused, KeyError, TypeError):
        raise Refused("members.json is malformed")
    contains = sorted(manifest["contains"])
    if sorted(named) != contains:
        raise Refused("members.json and pack.json name different members")
    if sorted(pinned) != contains:
        raise Refused("META/lock.json pins other members than the composite contains")


def verify_any(files, policy, now):
    """A pack, or a pack set and every member on its own (v1.2 §5)."""
    pid, version, digest, manifest = verify(files, policy, now)
    if manifest.get("kind") != "set":
        return pid, version, digest
    members = {}
    for path, data in files.items():
        if not path.startswith("packs/"):
            continue
        rest = path[len("packs/"):]
        member, slash, inner = rest.partition("/")
        if not member or not slash or not inner:
            raise Refused(f"'{path}' is not inside a member folder")
        members.setdefault(member, {})[inner] = data
    if sorted(members) != sorted(manifest.get("contains", [])):
        raise Refused("the set's pack.json names other members than it carries")
    for member, member_files in sorted(members.items()):
        try:
            inner_id = verify(member_files, policy, now)[0]
        except Refused as e:
            raise Refused(f"packs/{member}/: {e}")
        if inner_id != member:
            raise Refused(f"the member in packs/{member}/ is {inner_id}")
    return pid, version, digest


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
    unknown = sorted(set(manifest) - PACK_FIELDS)
    if unknown:
        raise Refused(f"pack.json has an unknown field '{unknown[0]}'")
    extensions = manifest.get("extensions", {})
    if not isinstance(extensions, dict):
        raise Refused("pack.json has a malformed 'extensions'")
    critical = extensions.get("critical", [])
    if not isinstance(critical, list) or not all(isinstance(c, str) for c in critical) or len(set(critical)) != len(critical):
        raise Refused("pack.json has a malformed 'extensions.critical'")
    for name in critical:
        if name == "critical" or name not in extensions:
            raise Refused(f"pack.json marks '{name}' critical, but holds no such extension")
        if name not in UNDERSTOOD_EXTENSIONS:
            raise Refused(f"pack.json marks the extension '{name}' critical, and this verifier does not understand it")
    manifest_checks(manifest, content, now)
    digest = "sha256:" + hashlib.sha256(list_bytes).hexdigest()
    if digest in policy.get("revoked", {}).get("digests", []):
        raise Refused("the pack is revoked")
    expires = policy_time(policy.get("revoked", {}).get("expires"))
    if expires is not None and expires < now:
        raise Refused("the host's revocation list has expired")

    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    revoked_keys = {r["keyId"] for r in policy.get("revoked", {}).get("keys", [])}
    roles = set()
    for path in sorted(p for p in files if p.startswith(SIGNATURES)):
        # A file under META/signatures/ that is not an envelope at all is unlisted content: it refuses the pack.
        try:
            env = profile(files[path])
        except Refused:
            raise Refused(f"{path} is not a JSON envelope")
        if not isinstance(env, dict) or env.get("payloadType") != PAYLOAD_TYPE or not isinstance(env.get("payload"), str) \
                or not isinstance(env.get("signatures"), list):
            raise Refused(f"{path} is not a DSSE envelope of an in-toto statement")
        try:
            payload = base64.b64decode(env["payload"], validate=True)
        except ValueError:
            raise Refused(f"{path} has a payload that is not base64")
        try:
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
            if not isinstance(s, dict):
                continue
            key = keys.get(s.get("keyid"))
            if key is None or key["keyId"] in revoked_keys or role not in key["roles"]:
                continue
            if not any(pid == pre or pid.startswith(pre.rstrip(".") + ".") for pre in key["idPrefixes"]):
                continue
            public = serialization.load_pem_public_key(key["publicKey"].encode())
            spki = public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
            if hashlib.sha256(spki).hexdigest() != key["keyId"] or signed_at.timestamp() > now.timestamp() + 300:
                continue
            # A key's window: the signing time is the signer's own claim, so expiry is also checked against now.
            not_before, not_after = policy_time(key.get("notBefore")), policy_time(key.get("notAfter"))
            if not_before is not None and (signed_at < not_before or now < not_before):
                continue
            if not_after is not None and (signed_at > not_after or now > not_after):
                continue
            try:
                public.verify(base64.b64decode(s["sig"]), pae(payload), ec.ECDSA(hashes.SHA256()))
                roles.add(role)
            except InvalidSignature:
                continue
    missing = set(policy.get("requireRoles", ["publisher"])) - roles
    if missing:
        raise Refused("no valid signature in role " + ", ".join(sorted(missing)))
    return pid, version, digest, manifest


def main(argv):
    pack_path, policy_path = argv[1], argv[2]
    policy = json.load(open(policy_path, encoding="utf-8"))
    now = datetime.now(timezone.utc)
    try:
        if len(argv) > 3:
            policy = apply_revocations(policy, open(argv[3], "rb").read(), now)
        pid, version, digest = verify_any(read_zip(open(pack_path, "rb").read()), policy, now)
    except Refused as e:
        print(f"refused: {e}")
        return 1
    print(f"verified {pid} {version} {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
