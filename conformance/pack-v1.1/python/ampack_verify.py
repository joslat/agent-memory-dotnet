"""A second, independent verifier for memory packs (format v1.2), in Python, to prove the format is language-neutral.

It shares no code with the .NET implementation: it reads the strict ZIP profile itself, checks the canonical
META/files.json, every file's length and SHA-256, the DSSE envelopes of in-toto statements against a trust policy (key
scopes, roles, validity windows, revocations and their expiry), pack.json (fields, kinds, enumerations, validUntil,
source), what each kind must hold, a composite's lock against its members, and a pack set with each member on its own.
It must agree with `ampack verify` on every vector in ../vectors (see run_vectors.py).

It also checks the federated profile (federated packs §3-§5): the publisher WebID bound to the signing key by the host's
policy, the host inside the audience, and a publication receipt for one URL and the exact archive bytes.

usage: python ampack_verify.py <pack.ampack> <trust.json> [<revocations.json>...] [--receipt <file> --location <url>]
       exit 0 verified, 1 refused
Needs: Python 3.10+, the `cryptography` package.
"""
import base64
import hashlib
import binascii
import functools
import json
import math
import re
import struct
import sys
import unicodedata
import zlib
from datetime import datetime, timezone

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils

FILES = "META/files.json"
SIGNATURES = "META/signatures/"
FILES_FORMAT = "agentmemory.pack.files/1.1"
PACK_FORMAT = "agentmemory.pack/1.2"
# Every field pack.json may hold; anything the format does not define goes in "extensions" (PACK-strict).
PACK_FIELDS = {"format", "id", "version", "kind", "sdk", "requires", "display", "containableBy", "contains", "tier", "licence",
               "validUntil", "protection", "code", "source", "extensions"}
UNDERSTOOD_EXTENSIONS = {"federation"}
FEDERATION_FORMAT = "agentmemory.pack.federation/1"
PUBLICATION = "urn:agentmemory:pack:publication:v1"
# One WebID grammar for every verifier (federated packs §3): https and a DNS host, or http for a loopback host; an optional
# port; a path of characters that need no escaping in Turtle; at most one '#'; at most 512 characters.
WEBID = re.compile(r"(?:https://[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*"
                   r"|http://(?:localhost|127\.0\.0\.1|\[::1\]))(?::[0-9]{1,5})?/[A-Za-z0-9\-._~:/?#@!$&'()*+,;=%]*", re.ASCII)
ACCESS_KINDS = {"wac", "acp", "public"}
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
INSTANT = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})", re.ASCII)
PAYLOAD_TYPE = "application/vnd.in-toto+json"
STATEMENT = "https://in-toto.io/Statement/v1"
ROLES = {"publisher", "curator", "assurer"}
SEGMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}")
DEVICES = {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
ID = re.compile(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*){2,}")
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?", re.ASCII)
SIGNED_AT = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", re.ASCII)
KEY_ROLES = {"publisher", "curator", "assurer", "revoker"}
MAX_DEPTH = 64
MAX_COMBINING_RUN = 30
UNICODE_VERSION = "15.0.0"
INT64_MAX = 2 ** 63 - 1
# Bounds before any signature holds (fifth review): every check then costs at most what was read.
MAX_ENVELOPE, MAX_SIGNATURES, MAX_ENVELOPES, MAX_VERSION, MAX_REVOCATIONS = 64 * 1024, 16, 16, 256, 4 << 20
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
    # An offset's minutes are below 60, as .NET reads them (fromisoformat takes +05:60).
    if not text.endswith("Z") and int(text[-2:]) >= 60:
        return None
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
        # As a .NET DateTimeOffset holds it: an offset within 14 hours, a UTC time within years 1 to 9999.
        if abs(parsed.utcoffset().total_seconds()) > 14 * 3600:
            return None
        parsed.astimezone(timezone.utc)
        return parsed
    except (ValueError, OverflowError):
        return None


def policy_time(value):
    """A time in the host's policy: absent, or an RFC 3339 date-time; anything else is a malformed policy."""
    if value is None:
        return None
    parsed = instant(value)
    if parsed is None:
        raise Refused("the trust policy has a time that is not an RFC 3339 date-time")
    return parsed


def present_time(obj, name):
    """A time field of the policy: absent, or an RFC 3339 date-time; null is not absent (as .NET reads it)."""
    if name in obj and obj[name] is None:
        raise Refused("the trust policy has a time that is not an RFC 3339 date-time")
    return policy_time(obj.get(name))


def read_revocations(policy, data, now):
    """One revocation document read and its signatures checked: the keys the policy names as revoker whose signatures hold
    (whether they are live is decided for the whole run), its issue time, expiry, digests and keys. Refused when no such
    key signed it, or when it is malformed or issued in the future."""
    if len(data) > MAX_REVOCATIONS:
        raise Refused(f"the revocation document is larger than {MAX_REVOCATIONS >> 20} MiB")
    env = profile(data)
    if not isinstance(env, dict) or env.get("payloadType") != REVOCATIONS_TYPE or not isinstance(env.get("payload"), str) \
            or not isinstance(env.get("signatures"), list):
        raise Refused("the revocation document is not a DSSE envelope of a revocation document")
    try:
        payload = base64.b64decode(env["payload"], validate=True)
    except ValueError:
        raise Refused("the revocation document has a payload that is not base64")
    if len(env["signatures"]) > MAX_SIGNATURES:
        raise Refused(f"the revocation document holds more than {MAX_SIGNATURES} signatures")
    digest = hashlib.sha256(f"DSSEv1 {len(REVOCATIONS_TYPE)} {REVOCATIONS_TYPE} {len(payload)} ".encode() + payload).digest()
    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    signers = set()
    for s in env["signatures"]:
        if not isinstance(s, dict) or not isinstance(s.get("keyid"), str) or not isinstance(s.get("sig"), str):
            continue
        key = keys.get(s["keyid"])
        if key is None or "revoker" not in key.get("roles", []):
            continue
        public = serialization.load_pem_public_key(key["publicKey"].encode())
        spki = public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
        if hashlib.sha256(spki).hexdigest() != key["keyId"]:
            continue
        try:
            public.verify(base64.b64decode(s["sig"], validate=True), digest, ec.ECDSA(utils.Prehashed(hashes.SHA256())))
        except (InvalidSignature, ValueError, binascii.Error):
            continue
        signers.add(key["keyId"])
    if not signers:
        raise Refused("the revocation document is not signed by a key the trust policy names as revoker")
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
    return {"signers": sorted(signers), "issued": issued, "expires": expires, "expires_text": doc["expires"], "digests": digests, "keys": revoked}


def apply_revocations(policy, data, now):
    """One signed revocation document applied to the policy: apply_all with that document alone."""
    return apply_all(policy, [data], now)


def apply_all(policy, documents, now):
    """The revocation documents of one run applied to the policy, by the rule .NET's Revocations.ApplyAll keeps (a second
    implementation of it, not a port of its code):
    - every authentic list from a key the policy names as revoker denies the packs and non-revoker keys it names;
    - only a list with a live signer (valid now, revoked by nobody: the policy, or another counted list) revokes revokers and
      brings freshness, the newest such list's expiry, capped by the policy's own;
    - a list revokes a key against its will only when a live signer other than that key signed it;
    - a list without a live signer that revokes a revoker that has not expired, which no counted list revokes, refuses the
      run (a live revoker carries it forward); lists that revoke each other's signers in a circle refuse it too;
    - the same list twice is one list."""
    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    revoked_before = {r["keyId"] for r in policy.get("revoked", {}).get("keys", [])}
    revoker_ids = {k for k, v in keys.items() if "revoker" in v.get("roles", [])}

    def window(key_id):
        key = keys.get(key_id, {})
        return policy_time(key.get("notBefore")), policy_time(key.get("notAfter"))

    def valid_now(key_id):
        not_before, not_after = window(key_id)
        return not (not_before is not None and now < not_before) and not (not_after is not None and now > not_after)

    def expired(key_id):
        not_after = window(key_id)[1]
        return not_after is not None and now > not_after

    seen, read = set(), []
    for data in documents:
        fingerprint = hashlib.sha256(data).hexdigest()
        if fingerprint not in seen:
            seen.add(fingerprint)
            read.append(read_revocations(policy, data, now))
    live = [[s for s in r["signers"] if s not in revoked_before and valid_now(s)] for r in read]

    def revokes_other(j, key_id):
        return any(k["keyId"] == key_id for k in read[j]["keys"]) and any(s != key_id for s in live[j])

    state = [0] * len(read)  # 0 open, 1 counts, 2 without a live signer
    changed = True
    while changed:
        changed = False
        for i in range(len(read)):
            if state[i]:
                continue
            def revoked_by(signer, which):
                return any(j != i and which(j) and revokes_other(j, signer) for j in range(len(read)))
            if any(not revoked_by(s, lambda j: state[j] != 2) for s in live[i]):
                state[i], changed = 1, True
            elif all(revoked_by(s, lambda j: state[j] == 1) for s in live[i]):
                state[i], changed = 2, True
    if 0 in state:
        raise Refused("the revocation documents of this run revoke their own signers in a circle")
    revoked_now = revoked_before | {k["keyId"] for j in range(len(read)) if state[j] == 1 for k in read[j]["keys"] if revokes_other(j, k["keyId"])}
    for i in range(len(read)):
        if state[i] == 2 and any(k["keyId"] in revoker_ids and k["keyId"] not in revoked_now and not expired(k["keyId"]) for k in read[i]["keys"]):
            raise Refused("a revocation document without a live signer revokes a revoker that has not expired; a live revoker must carry it forward")

    merged = dict(policy)
    current = dict(policy.get("revoked", {}))
    static = policy_time(current.get("expires"))
    by_id = {k["keyId"]: k for k in current.get("keys", [])}
    digests = set(current.get("digests", []))
    newest = None  # (issued, expires, text) of the newest counted list; of two in one second, the earlier expiry
    for i, r in enumerate(read):
        counts = state[i] == 1
        digests |= set(r["digests"])
        for k in (r["keys"] if counts else [k for k in r["keys"] if k["keyId"] not in revoker_ids]):
            known = by_id.get(k["keyId"])
            if known is None or ("compromisedAt" in known and ("compromisedAt" not in k or instant(k["compromisedAt"]) < instant(known["compromisedAt"]))):
                by_id[k["keyId"]] = k
        if counts and (newest is None or r["issued"] > newest[0] or (r["issued"] == newest[0] and r["expires"] < newest[1])):
            newest = (r["issued"], r["expires"], r["expires_text"])
    current["digests"] = sorted(digests)
    current["keys"] = [by_id[i] for i in sorted(by_id)]
    if newest is not None and (static is None or newest[1] < static):
        current["expires"] = newest[2]
    merged["revoked"] = current
    return merged


def valid_id(value):
    return isinstance(value, str) and len(value) <= 64 and ID.fullmatch(value) is not None


def manifest_checks(manifest, content, now):
    """pack.json's fields and what its kind must hold (v1.2 §2, §3, §5), as `ampack verify` checks them."""
    for field in ("format", "id", "version", "kind", "sdk", "tier", "licence", "validUntil", "protection", "code"):
        if field in manifest and not isinstance(manifest[field], str):
            raise Refused(f"pack.json has a '{field}' that is not a string")
    for field in ("requires", "display"):
        if field in manifest and not isinstance(manifest[field], dict):
            raise Refused(f"pack.json has a '{field}' that is not an object")
    if isinstance(manifest.get("display"), dict) and "icon" in manifest["display"] and not isinstance(manifest["display"]["icon"], str):
        raise Refused("pack.json has a display.icon that is not a string")
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
        if not isinstance(source, dict) or not all(k in ("repository", "commit", "builtAt") and isinstance(v, str) and 0 < len(v.encode("utf-16-le")) // 2 <= 200
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
                or not isinstance(m["version"], str) or not valid_version(m["version"]) \
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
    if not all(isinstance(m, str) for m in named):
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


@functools.lru_cache(maxsize=65536)
def stream_safe(c):
    """A character's Stream-Safe profile (UAX #15 D4): the non-starters its NFKD leads and trails with, and whether it is all non-starters."""
    if c < "\u00a0":
        return 0, 0, False
    classes = [unicodedata.combining(x) for x in unicodedata.normalize("NFKD", c)]
    lead = next((i for i, x in enumerate(classes) if x == 0), len(classes))
    trail = next((i for i, x in enumerate(reversed(classes)) if x == 0), len(classes))
    return lead, trail, lead == len(classes)


def profile(data, integers_only=False):
    """The JSON profile: no BOM, LF only, no duplicate keys, NFC strings, integers within 2^53."""
    if data.startswith(b"\xef\xbb\xbf") or b"\r" in data:
        raise Refused("a JSON file has a BOM or a carriage return")
    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=no_duplicates, parse_constant=lambda c: (_ for _ in ()).throw(Refused(c)))
    except (UnicodeDecodeError, ValueError) as e:
        raise Refused(f"a JSON file is not valid: {e}")
    except RecursionError:
        raise Refused("a JSON file nests deeper than 64")

    def walk(v, depth=0):
        if isinstance(v, (dict, list)) and depth + 1 > MAX_DEPTH:
            raise Refused("a JSON file nests deeper than 64")
        if isinstance(v, dict):
            for k, x in v.items():
                walk(k, depth + 1)
                walk(x, depth + 1)
        elif isinstance(v, list):
            for x in v:
                walk(x, depth + 1)
        elif isinstance(v, str):
            if any(0xD800 <= ord(c) <= 0xDFFF for c in v):
                raise Refused("a JSON string has an unpaired surrogate")
            # UAX #15 D4: non-starters counted on each character's NFKD decomposition, not on the characters themselves.
            run = longest = 0
            for c in v:
                lead, trail, whole = stream_safe(c)
                run += lead
                longest = max(longest, run)
                if not whole:
                    run = trail
            if longest > MAX_COMBINING_RUN:
                raise Refused("a JSON string has more than 30 combining marks in a row")
            if unicodedata.normalize("NFC", v) != v:
                raise Refused("a JSON string is not NFC")
        elif isinstance(v, bool) or v is None:
            pass
        elif isinstance(v, int):
            if abs(v) > 2 ** 53 - 1:
                raise Refused("a JSON integer is beyond 2^53")
        elif isinstance(v, float) and integers_only:
            raise Refused("a canonical file holds a fraction")
        elif isinstance(v, float) and not math.isfinite(v):
            raise Refused("a JSON number is out of range")
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
    if not isinstance(listing, dict):
        raise Refused("META/files.json is not a v1.1 files list")
    if canonical(listing).encode("utf-8") != list_bytes:
        raise Refused("META/files.json is not canonical")
    if set(listing) != {"format", "id", "version", "files"} or listing["format"] != FILES_FORMAT:
        raise Refused("META/files.json is not a v1.1 files list")
    pid, version = listing["id"], listing["version"]
    if not isinstance(pid, str) or not isinstance(version, str) or not ID.fullmatch(pid) or len(pid) > 64 or not valid_version(version):
        raise Refused("META/files.json names no valid id or version")
    listed = {}
    if not isinstance(listing["files"], list):
        raise Refused("META/files.json has no files array")
    for entry in listing["files"]:
        if not isinstance(entry, dict) or set(entry) != {"path", "length", "sha256"} or not isinstance(entry["path"], str) \
                or isinstance(entry["length"], bool) or not isinstance(entry["length"], int) or entry["length"] < 0 \
                or not isinstance(entry["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", entry["sha256"]):
            raise Refused("META/files.json has a malformed entry")
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
    federation = read_federation(extensions)
    manifest_checks(manifest, content, now)
    digest = "sha256:" + hashlib.sha256(list_bytes).hexdigest()
    if digest in policy.get("revoked", {}).get("digests", []):
        raise Refused("the pack is revoked")
    expires = policy_time(policy.get("revoked", {}).get("expires"))
    if expires is not None and expires < now:
        raise Refused("the host's revocation list has expired")

    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    revoked_keys = {r["keyId"] for r in policy.get("revoked", {}).get("keys", [])}
    roles, publisher_keys = set(), set()
    envelopes = sorted(p for p in files if p.startswith(SIGNATURES))
    if len(envelopes) > MAX_ENVELOPES:
        raise Refused(f"the pack carries {len(envelopes)} signature envelopes; at most {MAX_ENVELOPES}")
    list_hex = hashlib.sha256(list_bytes).hexdigest()
    for path in envelopes:
        # A file under META/signatures/ that is not an envelope at all is unlisted content: it refuses the pack.
        if len(files[path]) > MAX_ENVELOPE:
            raise Refused(f"{path} is larger than {MAX_ENVELOPE // 1024} KiB")
        try:
            env = profile(files[path])
        except Refused:
            raise Refused(f"{path} is not a JSON envelope")
        if not isinstance(env, dict) or env.get("payloadType") != PAYLOAD_TYPE or not isinstance(env.get("payload"), str) \
                or not isinstance(env.get("signatures"), list):
            raise Refused(f"{path} is not a DSSE envelope of an in-toto statement")
        if len(env["signatures"]) > MAX_SIGNATURES:
            raise Refused(f"{path} holds more than {MAX_SIGNATURES} signatures")
        try:
            payload = base64.b64decode(env["payload"], validate=True)
        except ValueError:
            raise Refused(f"{path} has a payload that is not base64")
        try:
            st = profile(payload)
            pred = st["predicate"]
            role = pred["role"]
            ok = (st["_type"] == STATEMENT and len(st["subject"]) == 1 and st["subject"][0]["name"] == FILES
                  and st["subject"][0]["digest"]["sha256"] == list_hex
                  and role in ROLES and st["predicateType"] == f"urn:agentmemory:pack:{role}:v1"
                  and pred["id"] == pid and pred["version"] == version)
            if not isinstance(pred["signedAt"], str) or not SIGNED_AT.fullmatch(pred["signedAt"]):
                continue
            signed_at = datetime.strptime(pred["signedAt"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
        except (Refused, KeyError, TypeError, ValueError):
            continue
        if not ok:
            continue
        # Hashed once per envelope (fifth review); at most MAX_SIGNATURES entries are tried.
        digest_of_pae = hashlib.sha256(pae(payload)).digest()
        for s in env.get("signatures", []):
            if not isinstance(s, dict) or not isinstance(s.get("keyid"), str):
                continue
            key = keys.get(s.get("keyid"))
            if key is None or key["keyId"] in revoked_keys or role not in key["roles"]:
                continue
            # As .NET covers it: the same id, or the prefix as a namespace (one trailing dot allowed, never stripped further).
            if not any(pid == pre or pid.startswith(pre if pre.endswith(".") else pre + ".") for pre in key["idPrefixes"]):
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
                public.verify(base64.b64decode(s.get("sig") if isinstance(s.get("sig"), str) else "!", validate=True),
                              digest_of_pae, ec.ECDSA(utils.Prehashed(hashes.SHA256())))
                roles.add(role)
                if role == "publisher":
                    publisher_keys.add(key["keyId"])
            except (InvalidSignature, ValueError, binascii.Error):
                continue
    missing = set(policy.get("requireRoles", ["publisher"])) - roles
    if missing:
        raise Refused("no valid signature in role " + ", ".join(sorted(missing)))
    if federation is not None:
        # The publisher WebID is the signer's by this host's binding; no key bound to another WebID signed as publisher.
        bound = [keys[k].get("webId") for k in publisher_keys]
        if any(w is not None and w != federation["publisher"] for w in bound):
            raise Refused("a key bound to another WebID signed the pack as publisher")
        if federation["publisher"] not in bound:
            raise Refused("no publisher signature from a key this host bound to the pack's publisher")
        host = policy.get("host", {}).get("webIds", [])
        if federation["audience"] is not None and not any(h in federation["audience"] for h in host):
            raise Refused("the pack is not published for this host")
    return pid, version, digest, manifest


def valid_webid(text):
    return isinstance(text, str) and 0 < len(text) <= 512 and WEBID.fullmatch(text) is not None and text.count("#") <= 1


def read_audience(value, what):
    """'public' (None), or 1 to 64 distinct WebIDs."""
    if value == "public":
        return None
    if not isinstance(value, list) or not 1 <= len(value) <= 64 or not all(isinstance(v, str) for v in value):
        raise Refused(f"{what} is \"public\" or a list of 1 to 64 WebIDs")
    if not all(valid_webid(v) for v in value):
        raise Refused(f"{what} names something that is not a WebID")
    if len(set(value)) != len(value):
        raise Refused(f"{what} names a WebID twice")
    return value


def read_federation(extensions):
    """The federation extension of pack.json, or None. It must be critical, so a host that cannot enforce an audience refuses."""
    if "federation" not in extensions:
        return None
    if "federation" not in extensions.get("critical", []):
        raise Refused("pack.json carries the 'federation' extension without marking it critical")
    f = extensions["federation"]
    if not isinstance(f, dict):
        raise Refused("pack.json has a malformed 'federation' extension")
    if set(f) - {"format", "publisher", "audience", "purpose"}:
        raise Refused("the 'federation' extension has an unknown field")
    if f.get("format") != FEDERATION_FORMAT:
        raise Refused(f"the 'federation' extension must name format '{FEDERATION_FORMAT}'")
    if not valid_webid(f.get("publisher")):
        raise Refused("the federation publisher is not a WebID")
    if "audience" not in f:
        raise Refused("the 'federation' extension names no audience")
    audience = read_audience(f["audience"], "the federation audience")
    if "purpose" in f and (not isinstance(f["purpose"], str) or not 1 <= len(f["purpose"]) <= 300):
        raise Refused("the federation purpose is text of 1 to 300 characters")
    return {"publisher": f["publisher"], "audience": audience}


def key_refusal(key, role, pid, signed_at, now, revoked_keys):
    """Why a signature by `key` does not count (as .NET's TrustPolicy.Refusal), or None."""
    if role not in key["roles"]:
        return f"the key may not sign as {role}"
    if not any(pid == pre or pid.startswith(pre if pre.endswith(".") else pre + ".") for pre in key["idPrefixes"]):
        return f"the key may not sign for '{pid}'"
    not_before, not_after = policy_time(key.get("notBefore")), policy_time(key.get("notAfter"))
    if not_before is not None and (signed_at < not_before or now < not_before):
        return "the key was not yet valid"
    if not_after is not None and (signed_at > not_after or now > not_after):
        return "the key had expired"
    if signed_at.timestamp() > now.timestamp() + 300:
        return "the signature claims a time in the future"
    if key["keyId"] in revoked_keys:
        return "the key is revoked"
    return None


def verify_receipt(envelope_bytes, pid, version, digest, federation, archive, location, policy, now):
    """A publication receipt (federated packs §5) for a verified pack fetched from `location` as `archive`."""
    if federation is None:
        raise Refused("a publication receipt is for a federated pack")
    if len(envelope_bytes) > MAX_ENVELOPE:
        raise Refused("the receipt is larger than 64 KiB")
    env = profile(envelope_bytes)
    if not isinstance(env, dict) or set(env) != {"payloadType", "payload", "signatures"} or env["payloadType"] != PAYLOAD_TYPE \
            or not isinstance(env["payload"], str) or not isinstance(env["signatures"], list) or not 1 <= len(env["signatures"]) <= MAX_SIGNATURES:
        raise Refused("the receipt is not a DSSE envelope of an in-toto statement with 1 to 16 signatures")
    try:
        payload = base64.b64decode(env["payload"], validate=True)
    except (ValueError, binascii.Error):
        raise Refused("the receipt's payload is not base64")
    st = profile(payload)
    if not isinstance(st, dict) or set(st) != {"_type", "subject", "predicateType", "predicate"} or st["_type"] != STATEMENT \
            or st["predicateType"] != PUBLICATION:
        raise Refused("the receipt is not a publication statement")
    subject = st["subject"]
    if not isinstance(subject, list) or len(subject) != 1 or not isinstance(subject[0], dict) or set(subject[0]) != {"name", "digest"} \
            or subject[0]["name"] != FILES or not isinstance(subject[0]["digest"], dict) or set(subject[0]["digest"]) != {"sha256"} \
            or not isinstance(subject[0]["digest"]["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", subject[0]["digest"]["sha256"]):
        raise Refused("the receipt must name exactly one subject: META/files.json and its SHA-256")
    pred = st["predicate"]
    fields = {"id", "version", "location", "archiveSha256", "audience", "access", "checked", "publishedAt"}
    if not isinstance(pred, dict) or set(pred) - fields:
        raise Refused("the receipt's predicate holds other fields than this format defines")
    if not (isinstance(pred.get("id"), str) and ID.fullmatch(pred["id"]) and len(pred["id"]) <= 64 and valid_version(pred.get("version"))):
        raise Refused("the receipt names no valid pack id and version")
    if not isinstance(pred.get("location"), str):
        raise Refused("the receipt names no location")
    if not isinstance(pred.get("archiveSha256"), str) or not re.fullmatch(r"[0-9a-f]{64}", pred["archiveSha256"]):
        raise Refused("the receipt's archiveSha256 is not 64 lowercase hexadecimal characters")
    if "audience" not in pred:
        raise Refused("the receipt names no audience")
    placed_for = read_audience(pred["audience"], "the receipt's audience")
    if not isinstance(pred.get("access"), str):
        raise Refused("the receipt names no access kind")
    checked = pred.get("checked")
    if not isinstance(checked, dict) or set(checked) != {"denied", "allowed"}:
        raise Refused("the receipt's 'checked' is an object of 'denied' and 'allowed'")
    for name, anonymous in (("denied", True), ("allowed", False)):
        probes = checked[name]
        if not isinstance(probes, list) or len(probes) > 64 or len(set(map(str, probes))) != len(probes) \
                or not all(isinstance(x, str) and ((anonymous and x == "anonymous") or valid_webid(x)) for x in probes):
            raise Refused(f"the receipt's checked.{name} is a list of at most 64 WebIDs")
    if not isinstance(pred.get("publishedAt"), str) or not SIGNED_AT.fullmatch(pred["publishedAt"]):
        raise Refused("the receipt has no publishing time in UTC")
    try:
        published_at = datetime.strptime(pred["publishedAt"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError:
        raise Refused("the receipt has no publishing time in UTC")
    if not valid_webid(pred["location"]) or "#" in pred["location"]:
        raise Refused("the receipt's location is not an https URL in the WebID grammar, without a fragment")
    if pred["access"] not in ACCESS_KINDS:
        raise Refused("the receipt's access is wac, acp or public")
    if (pred["access"] == "public") != (placed_for is None):
        raise Refused("a receipt's access is public exactly when its audience is public")
    # The placement against this pack, this URL, these bytes and this host.
    if "sha256:" + subject[0]["digest"]["sha256"] != digest:
        raise Refused("the receipt is for another pack")
    if pred["id"] != pid or pred["version"] != version:
        raise Refused("the receipt names another pack id or version")
    if pred["location"] != location:
        raise Refused("the receipt places the pack elsewhere than where it was fetched")
    if pred["archiveSha256"] != hashlib.sha256(archive).hexdigest():
        raise Refused("the archive fetched is not the archive the receipt names")
    audience = federation["audience"]
    if audience is not None and (placed_for is None or any(a not in audience for a in placed_for)):
        raise Refused("the receipt grants a wider audience than the pack allows")
    host = policy.get("host", {}).get("webIds", [])
    if placed_for is not None and not any(h in placed_for for h in host):
        raise Refused("the placement is not for this host")
    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    revoked_keys = {r["keyId"] for r in policy.get("revoked", {}).get("keys", [])}
    digest_of_pae = hashlib.sha256(pae(payload)).digest()
    for entry in env["signatures"]:
        if not isinstance(entry, dict) or not isinstance(entry.get("keyid"), str) or not isinstance(entry.get("sig"), str):
            continue
        key = keys.get(entry["keyid"])
        if key is None:
            continue
        try:
            sig = base64.b64decode(entry["sig"], validate=True)
            public = serialization.load_pem_public_key(key["publicKey"].encode())
            public.verify(sig, digest_of_pae, ec.ECDSA(utils.Prehashed(hashes.SHA256())))
        except (InvalidSignature, ValueError, binascii.Error):
            continue
        if key_refusal(key, "publisher", pid, published_at, now, revoked_keys) is None and key.get("webId") == federation["publisher"]:
            return pred
    raise Refused("no valid receipt signature by a key bound to the pack's publisher")


def valid_version(text):
    if not isinstance(text, str) or len(text) > MAX_VERSION:
        return False
    match = SEMVER.fullmatch(text)
    # The length first: int() refuses a numeral past 4,300 digits, and 19 already reach past 2^63 (fourth review).
    if match is None or any(len(part) > 19 or int(part) > INT64_MAX for part in match.group(1, 2, 3)):
        return False
    # SemVer 2.0: a numeric prerelease identifier has no leading zero (1.0.0-01 is not a version).
    prerelease = text.split("+", 1)[0].partition("-")[2]
    return not any(len(p) > 1 and p[0] == "0" and p.isdigit() for p in prerelease.split(".")) if prerelease else True


def load_policy(raw):
    """The host's trust policy, as .NET reads it: a BOM and CRLF are tolerated, the JSON profile holds, every key is
    checked (its id is the SHA-256 of its key, it names prefixes and known roles), no key is listed twice, times are RFC 3339,
    and requireRoles names pack roles."""
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        raise Refused("the trust policy is not UTF-8")
    if text.startswith("\ufeff"):
        text = text[1:]
    policy = profile(text.replace("\r\n", "\n").encode("utf-8"))
    if not isinstance(policy, dict) or policy.get("format") != "agentmemory.pack.trust/1.1":
        raise Refused("the trust policy must name format 'agentmemory.pack.trust/1.1'")
    seen = set()
    for key in policy.get("keys", []) if isinstance(policy.get("keys", []), list) else [None]:
        if not isinstance(key, dict) or not isinstance(key.get("keyId"), str) or not isinstance(key.get("publicKey"), str):
            raise Refused("the trust policy has a malformed key")
        prefixes, roles = key.get("idPrefixes", []), key.get("roles", [])
        if not isinstance(prefixes, list) or not prefixes or not all(isinstance(p, str) for p in prefixes):
            raise Refused("a trusted key names no pack id prefixes")
        if not isinstance(roles, list) or not roles or not all(isinstance(r, str) and r in KEY_ROLES for r in roles):
            raise Refused("a trusted key needs known roles")
        if "revoker" in roles and len(roles) > 1:
            raise Refused("a revoker key holds that role alone")
        try:
            public = serialization.load_pem_public_key(key["publicKey"].encode())
            spki = public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
        except (ValueError, TypeError, UnicodeEncodeError):
            raise Refused("a trusted key is not a PEM public key")
        if not isinstance(public, ec.EllipticCurvePublicKey) or public.curve.name != "secp256r1":
            raise Refused("a trusted key is not a P-256 public key")
        if hashlib.sha256(spki).hexdigest() != key["keyId"]:
            raise Refused("a trusted key's id is not the SHA-256 of its key")
        if key["keyId"] in seen:
            raise Refused("the trust policy lists a key twice")
        seen.add(key["keyId"])
        if "webId" in key:
            if not valid_webid(key["webId"]):
                raise Refused("a trusted key's webId is not a WebID")
            if "revoker" in roles:
                raise Refused("a revoker key is never bound to a WebID")
        present_time(key, "notBefore")
        present_time(key, "notAfter")
    revoked = policy.get("revoked", {})
    if not isinstance(revoked, dict):
        raise Refused("the trust policy has a malformed revoked section")
    if not isinstance(revoked.get("digests", []), list) or not all(isinstance(d, str) for d in revoked.get("digests", [])):
        raise Refused("the trust policy has a malformed revoked digest list")
    for r in revoked.get("keys", []) if isinstance(revoked.get("keys", []), list) else [None]:
        if not isinstance(r, dict) or not isinstance(r.get("keyId"), str):
            raise Refused("the trust policy has a malformed revoked key")
        present_time(r, "compromisedAt")
    present_time(revoked, "expires")
    if "host" in policy:
        host = policy["host"]
        if not isinstance(host, dict) or set(host) != {"webIds"} or not isinstance(host["webIds"], list) \
                or not 1 <= len(host["webIds"]) <= 16 or not all(valid_webid(w) for w in host["webIds"]) \
                or len(set(host["webIds"])) != len(host["webIds"]):
            raise Refused("'host' is an object holding only 'webIds': 1 to 16 distinct WebIDs")
    if "requireRoles" in policy:
        required = policy["requireRoles"]
        if not isinstance(required, list) or not required or not all(isinstance(r, str) and r in ROLES for r in required):
            raise Refused("requireRoles must name known roles")
    return policy


def main(argv):
    # NFC and the Stream-Safe count are Unicode's: the verdicts are Unicode 15.0's, as .NET's tables are, on every host.
    if unicodedata.unidata_version != UNICODE_VERSION:
        print(f"error: this verifier decides with Unicode {UNICODE_VERSION} data; this Python has {unicodedata.unidata_version} (use Python 3.12)")
        return 2
    args = list(argv[1:])
    receipt_path = location = None
    if "--receipt" in args:
        i = args.index("--receipt")
        receipt_path = args[i + 1]
        del args[i:i + 2]
    if "--location" in args:
        i = args.index("--location")
        location = args[i + 1]
        del args[i:i + 2]
    if (receipt_path is None) != (location is None):
        print("error: --receipt and --location go together")
        return 2
    pack_path, policy_path = args[0], args[1]
    now = datetime.now(timezone.utc)
    try:
        policy = load_policy(open(policy_path, "rb").read())
        documents = []
        for name in args[2:]:
            with open(name, "rb") as document:
                documents.append(document.read(MAX_REVOCATIONS + 1))
        if documents:
            policy = apply_all(policy, documents, now)
        archive = open(pack_path, "rb").read()
        pid, version, digest = verify_any(read_zip(archive), policy, now)
        if receipt_path is not None:
            files = read_zip(archive)
            _, _, _, manifest = verify(files, policy, now)
            federation = read_federation(manifest.get("extensions", {}))
            verify_receipt(open(receipt_path, "rb").read(), pid, version, digest, federation, archive, location, policy, now)
            print(f"verified the placement of {pid} {version} at {location}")
    except Refused as e:
        print(f"refused: {e}")
        return 1
    except Exception as e:  # a defect, never a verdict: the conformance run tells it apart from a refusal
        print(f"error: {type(e).__name__}: {e}")
        return 2
    print(f"verified {pid} {version} {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
