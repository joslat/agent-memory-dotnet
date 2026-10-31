"""A second, independent verifier for memory packs (format v1.2), in Python, to prove the format is language-neutral.

It shares no code with the .NET implementation: it reads the strict ZIP profile itself, checks the canonical
META/files.json, every file's length and SHA-256, the DSSE envelopes of in-toto statements against a trust policy (key
scopes, roles, validity windows, revocations and their expiry), pack.json (fields, kinds, enumerations, validUntil,
source), what each kind must hold, a composite's lock against its members, and a pack set with each member on its own.
It must agree with `ampack verify` on every vector in ../vectors (see run_vectors.py).

It also checks the federated profile (federated packs §3-§5): the publisher WebID bound to the signing key by the host's
policy, the host inside the audience, and a publication receipt for one URL and the exact archive bytes.

It also verifies an evaluation certificate (v1.2 §16) with --certificate: one carried in META/attestations/ or a file.

usage: python ampack_verify.py <pack.ampack> <trust.json> [<revocations.json>...] [--receipt <file> --location <url>]
       [--certificate <META/attestations/...|file>]
       exit 0 verified, 1 refused, 2 usage or a defect
Needs: Python 3.12 (its Unicode 15.0 data decides NFC exactly as .NET's tables do), the `cryptography` package.
"""
import base64
import bisect
import hashlib
import binascii
import errno
import functools
import json
import math
import os
import stat
import re
import struct
import sys
import unicodedata
import zlib
from datetime import datetime, timedelta, timezone

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils


def _load_registry():
    """The one definition of every name the format defines (feature 01): spec/registry.json, read by both verifiers and the spec.
    AMPACK_REGISTRY names another copy of the file; otherwise it is found beside the repository's spec folder."""
    path = os.environ.get("AMPACK_REGISTRY") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "spec", "registry.json")

    def no_duplicates(pairs):
        keys = [k for k, _ in pairs]
        if len(keys) != len(set(keys)):
            raise ValueError("the format registry repeats a key")
        return dict(pairs)
    with open(path, "rb") as f:
        registry = json.loads(f.read().decode("utf-8"), object_pairs_hook=no_duplicates)
    if registry.get("format") != "ampack.registry/1":
        raise ValueError("the format registry is not ampack.registry/1")
    return registry


REGISTRY = _load_registry()
SHAPE = {name: set(doc["fields"]) for name, doc in REGISTRY["documents"].items()}

FILES = "META/files.json"
SIGNATURES = "META/signatures/"
# Beside the signatures, outside the files list: evaluation certificates and the AEF runs they name (v1.2 §16).
ATTESTATIONS = "META/attestations/"
CERT_FILE = re.compile(r"META/attestations/([a-z0-9][a-z0-9-]{0,63})\.certificate\.dsse\.json")
RUN_FILE = re.compile(r"META/attestations/([a-z0-9][a-z0-9-]{0,63})\.aef/.+")
MAX_CERTIFICATES, MAX_RUN_FILES, MAX_RUN_BYTES = 8, 256, 8 << 20
CERTIFICATE = "urn:agentmemory:pack:certificate:v1"
MEASURES = set(REGISTRY["certificate"]["measures"])
BASELINES = set(REGISTRY["certificate"]["baselines"])
# A certificate states the declared targets; no regression is a host's comparison it carries no arm for (review 7, G3).
TARGETS = set(REGISTRY["trial"]["targets"])
HISTORY_WINDOW = 2000  # the most recent eligible commits a trial draws its tasks from (review 7, G5)
CONFIDENCES = set(REGISTRY["units"]["fields"]["confidencePermille"]["values"])
RUN_ID = re.compile(r"[A-Za-z0-9._:-]{1,128}", re.ASCII)
MINIMUM_TASKS = 10
# The pack card (v1.2 §15): card.json, read strictly when present.
CARD = "card.json"
CARD_FORMAT = "agentmemory.pack.card/1"
FILES_FORMAT = "agentmemory.pack.files/1.1"
PACK_FORMAT = "agentmemory.pack/1.2"
# Readable minors (v1.2 §2.1, review 1 M18): this verifier reads 1.2 strictly and a later 1.x as far as it knows it; a field of a
# later minor it does not know is ignored unless the pack names it in "critical". Earlier minors were drafts.

PACK_MINOR = 2
PACK_FORMAT_RE = re.compile(r"agentmemory\.pack/1\.([1-9][0-9]{0,2})")
MAX_CRITICAL = 64
# Every field pack.json may hold at this minor; anything the format does not define goes in "extensions" (PACK-strict).
PACK_FIELDS = set(REGISTRY["manifest"]["fields"])
# A timestamp token beside its envelope (<envelope>.tsr), reserved for trusted time (format 1.3): accepted, bounded, not judged.
TIMESTAMP_SUFFIX = ".tsr"
UNDERSTOOD_EXTENSIONS = set(REGISTRY["manifest"]["extensions"])
FEDERATION_FORMAT = "agentmemory.pack.federation/1"
PUBLICATION = "urn:agentmemory:pack:publication:v1"
# One WebID grammar for every verifier (federated packs §3): https and a DNS host, or http for a loopback host; an optional
# port; a path of characters that need no escaping in Turtle; at most one '#'; at most 512 characters.
WEBID = re.compile(r"(?:https://[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*"
                   r"|http://(?:localhost|127\.0\.0\.1|\[::1\]))(?::[0-9]{1,5})?/[A-Za-z0-9\-._~:/?#@!$&'()*+,;=%]*", re.ASCII)
ACCESS_KINDS = set(REGISTRY["federation"]["accessKinds"])
KINDS = set(REGISTRY["manifest"]["values"]["kind"])
TIERS = set(REGISTRY["manifest"]["values"]["tier"])
PROTECTIONS = set(REGISTRY["manifest"]["values"]["protection"])
CODE_TIERS = set(REGISTRY["manifest"]["values"]["code"])
LOCK = "META/lock.json"
LOCK_FORMAT = "agentmemory.pack.lock/1.1"
COMMIT = re.compile(r"[0-9a-f]{7,64}")
# RFC 3339 date-times, the one form a time in a pack or a policy takes.
REVOCATIONS_FORMAT = "agentmemory.pack.revocations/1.2"
REVOCATIONS_TYPE = "application/vnd.agentmemory.revocations+json"
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
KEY_ID = re.compile(r"[0-9a-f]{64}")
PUBLIC_PEM = re.compile(r"-----BEGIN PUBLIC KEY-----\r?\n(?:[A-Za-z0-9+/=]{1,76}\r?\n){1,16}-----END PUBLIC KEY-----(?:\r?\n)?", re.ASCII)
INSTANT = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})", re.ASCII)
PAYLOAD_TYPE = "application/vnd.in-toto+json"
STATEMENT = "https://in-toto.io/Statement/v1"
ROLES = set(REGISTRY["roles"]["signing"])
SEGMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}")
DEVICES = set(REGISTRY["paths"]["deviceNames"])
ID = re.compile(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*){2,}")
# A pack id prefix: one or more id segments, optionally ending with a dot (as .NET's PackManifest.IsValidPrefix).
PREFIX = re.compile(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*)*\.?")
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?", re.ASCII)
SIGNED_AT = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", re.ASCII)
KEY_ROLES = set(REGISTRY["roles"]["keys"])
MAX_DEPTH = 64
MAX_COMBINING_RUN = 30
UNICODE_VERSION = "15.0.0"
INT64_MAX = 2 ** 63 - 1
# Bounds before any signature holds (fifth review): every check then costs at most what was read.
MAX_ENVELOPE, MAX_SIGNATURES, MAX_ENVELOPES, MAX_VERSION, MAX_REVOCATIONS = 64 * 1024, 16, 16, 256, 4 << 20
MAX_FILES, MAX_FILE, MAX_TOTAL, MAX_RATIO = 4096, 64 << 20, 256 << 20, 250
# Review 5, H1, as .NET's PackVerifier: the signed list read before any signature holds, and each listed JSON file and JSONL
# line parsed after (a parse tree is many times the bytes).
MAX_FILES_LIST, MAX_JSON = 2 << 20, 1 << 20


class Refused(Exception):
    pass


def safe_path(path):
    if not 0 < len(path) <= 255:
        return False
    parts = path.split("/")
    return len(parts) <= 8 and all(SEGMENT.fullmatch(p) and not p.endswith(".") and p.split(".")[0].upper() not in DEVICES for p in parts)


MEMBER_FIELDS = set(REGISTRY["members"]["fields"])
KEY_ID = re.compile(r"[0-9a-f]{64}", re.ASCII)


def entry_shaped(s):
    """A DSSE signature entry as the format writes it, for every envelope kind (review 2, 3; as .NET's Dsse.IsEntry): a key id of
    64 lowercase hex and a signature of strict base64 decoding to 1 to 72 bytes, nothing else."""
    return isinstance(s, dict) and set(s) == SHAPE["signatureEntry"] and isinstance(s["keyid"], str) and bool(KEY_ID.fullmatch(s["keyid"])) \
        and isinstance(s["sig"], str) and 0 < len(strict_b64(s["sig"]) or b"") <= 72


def member_typed(m):
    """A composite member's values typed as the loader reads them (review 3, as .NET's PackVerifier.IsMember)."""
    def text(v):
        return isinstance(v, str) and v != ""
    priority = m.get("priority", 0)
    return text(m.get("id")) and text(m.get("version")) \
        and isinstance(priority, int) and not isinstance(priority, bool) and -2 ** 31 <= priority < 2 ** 31 \
        and isinstance(m.get("enabled", True), bool) \
        and isinstance(m.get("contains", []), list) and all(text(c) for c in m.get("contains", []))


P256_SPKI_PREFIX = bytes.fromhex("3059301306072a8648ce3d020106082a8648ce3d030107034200")


def canonical_p256(pem):
    """A P-256 key in the one form both verifiers read alike (review 3, as .NET's TrustPolicy.IsCanonicalP256): the 91-byte
    SubjectPublicKeyInfo of an uncompressed point, in canonical base64."""
    body = "".join(line.rstrip("\r") for line in pem.split("\n") if not line.startswith("-----"))
    der = strict_b64(body)
    return der is not None and len(der) == 91 and der[:26] == P256_SPKI_PREFIX and der[26] == 4 \
        and base64.b64encode(der).decode() == body


def read_regular(path, cap, what):
    """A file's bytes, at most cap of them (review 3, 4, 5): never through a link and never a folder, checked on the handle that
    is read rather than on the path before it is opened (review 5, L3: a local writer could swap a link in between). A pipe or a
    device, whose size reads as 0 and whose stream may never end, is read with the same bound, as .NET reads it. A file that
    cannot be read is refused, not a defect."""
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0)
    try:
        before = os.lstat(path)
        if stat.S_ISLNK(before.st_mode) or getattr(before, "st_file_attributes", 0) & 0x400:  # 0x400: a Windows reparse point
            raise Refused(f"{what} is a link")
        fd = os.open(path, flags)
    except OSError as e:
        # With O_NOFOLLOW, a link swapped in after the check fails to open (ELOOP): it is refused as a link.
        raise Refused(f"{what} is a link" if e.errno == getattr(errno, "ELOOP", None) else f"{what} cannot be read: {e.strerror}")
    try:
        status = os.fstat(fd)
        if stat.S_ISDIR(status.st_mode):
            raise Refused(f"{what} is a folder")
        # Where there is no O_NOFOLLOW (Windows), the file opened is the one checked: the same identity as the path's.
        if not getattr(os, "O_NOFOLLOW", 0):
            after = os.lstat(path)
            if (after.st_ino, after.st_dev) != (status.st_ino, status.st_dev) or stat.S_ISLNK(after.st_mode) \
                    or getattr(after, "st_file_attributes", 0) & 0x400:
                raise Refused(f"{what} changed while it was opened")
        if status.st_size > cap:
            raise Refused(f"{what} is larger than {cap >> 20 if cap >= 1 << 20 else cap >> 10} {'MiB' if cap >= 1 << 20 else 'KiB'}")
        # A pipe was opened without blocking, so that opening one with no writer does not hang; it is read blocking, to its end.
        if (stat.S_ISFIFO(status.st_mode) or stat.S_ISCHR(status.st_mode)) and hasattr(os, "set_blocking"):
            os.set_blocking(fd, True)
        chunks, total = [], 0
        while total <= cap:
            more = os.read(fd, min(1 << 20, cap + 1 - total))
            if not more:
                return b"".join(chunks)
            chunks.append(more)
            total += len(more)
    except OSError as e:
        raise Refused(f"{what} cannot be read: {e.strerror}")
    finally:
        os.close(fd)
    raise Refused(f"{what} is larger than {cap >> 20 if cap >= 1 << 20 else cap >> 10} {'MiB' if cap >= 1 << 20 else 'KiB'}")


# The most a document the command names may hold, read before anything parses it (review 5, L2: read whole, then checked).
MAX_POLICY = 4 << 20       # a trust policy with its revocations
MAX_SMALL = 64 * 1024      # a receipt or a certificate envelope, as MAX_ENVELOPE


def read_file(path, what, cap=MAX_SMALL):
    """A small file the command names (a policy, a receipt, a certificate), bounded and read as read_regular reads a pack."""
    return read_regular(path, cap, what)


def strict_b64(text):
    """The bytes of strict base64 (the alphabet, padding, no whitespace), or None."""
    try:
        return base64.b64decode(text, validate=True)
    except (ValueError, binascii.Error):
        return None
MAX_ARCHIVE = 512 * 1024 * 1024  # as .NET's PackLimits.MaxArchiveBytes (review 2: Python had no cap)


def read_zip(data):
    """The strict ZIP profile: contiguous, sorted, local equals central, stored or deflate, nothing optional."""
    if len(data) > MAX_ARCHIVE:
        raise Refused(f"the archive is larger than {MAX_ARCHIVE >> 20} MiB")
    if len(data) < 22 or data[-22:-18] != b"PK\x05\x06":
        raise Refused("the archive does not end with the end record")
    _, disk, cd_disk, n, total, cd_size, cd_start, comment = struct.unpack("<IHHHHIIH", data[-22:])
    if disk or cd_disk or comment or n != total or n == 0xFFFF:
        raise Refused("the end record is not in the profile")
    if n > MAX_FILES:
        raise Refused(f"the archive holds more than {MAX_FILES} files")
    if cd_start + cd_size != len(data) - 22:
        raise Refused("the central directory is not where the end record says")
    files, cursor, pos, previous, folded, unpacked = {}, 0, cd_start, None, set(), 0
    for _ in range(n):
        h = data[pos:pos + 46]
        if len(h) < 46:  # a truncated central directory is a refusal, as .NET's reader says (review 1, L11)
            raise Refused("a central entry is truncated")
        (sig, made, need, flags, method, time, date, crc, csize, size, nlen, xlen, clen, dsk, iattr, eattr, off) = struct.unpack("<IHHHHHHIIIHHHHHII", h)
        name_bytes = data[pos + 46:pos + 46 + nlen]
        if sig != 0x02014B50 or any(b < 0x20 or b > 0x7E for b in name_bytes):
            raise Refused("a central entry is malformed")
        name = name_bytes.decode("ascii")
        if (made >> 8) or (made & 0xFF) > 63 or need not in (10, 20) or flags or method not in (0, 8) or time or date != 0x21:
            raise Refused(f"entry '{name}' is not in the profile")
        if xlen or clen or dsk or iattr or eattr or name.endswith("/") or not safe_path(name):
            raise Refused(f"entry '{name}' is not in the profile")
        if (previous is not None and previous >= name) or off != cursor:
            raise Refused(f"entry '{name}' is out of order, repeated or not contiguous")
        # Its own rule, named as .NET names it (review 7, L8: a case collision was reported as an ordering error, so the vector
        # for it could not tell the rule from another).
        if name.lower() in folded:
            raise Refused(f"entry '{name}' differs from another only by case")
        local = data[cursor:cursor + 30]
        if local[:4] != b"PK\x03\x04" or local[4:30] != struct.pack("<HHHHHIIIHH", need, flags, method, time, date, crc, csize, size, nlen, 0) \
                or data[cursor + 30:cursor + 30 + nlen] != name_bytes:
            raise Refused(f"entry '{name}' has a local header that differs from the central directory")
        start = cursor + 30 + nlen
        # A deflated entry of 0 bytes that declares content is refused before anything is allocated (review 6, I8).
        if start + csize > cd_start or size > MAX_FILE or (method == 8 and (size > 0 if csize == 0 else size // csize > MAX_RATIO)) or (method == 0 and csize != size):
            raise Refused(f"entry '{name}' breaks a size rule")
        unpacked += size
        if unpacked > MAX_TOTAL:
            raise Refused("the archive unpacks to too much")
        raw = data[start:start + csize]
        if method == 8:
            d = zlib.decompressobj(-15)
            # A damaged deflate stream is a refusal, as .NET refuses it (review 7, M8: zlib.error escaped as a crash).
            try:
                content = d.decompress(raw, size + 1)
            except zlib.error as e:
                raise Refused(f"entry '{name}' is not a valid deflate stream") from e
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


_EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


def _ticks(value):
    """An aware datetime as 100 ns ticks since 1970, its seventh fraction digit included when it is an Instant."""
    return ((value - _EPOCH) // timedelta(microseconds=1)) * 10 + getattr(value, "seventh", 0)


class Instant(datetime):
    """A datetime that keeps the seventh fraction digit (100 ns), as a .NET DateTimeOffset does, and compares by it (review 1,
    M14: truncating to microseconds made two lists 100 ns apart the same instant, where .NET tells them apart)."""
    seventh = 0

    def __lt__(self, other): return _ticks(self) < _ticks(other) if isinstance(other, datetime) else NotImplemented
    def __le__(self, other): return _ticks(self) <= _ticks(other) if isinstance(other, datetime) else NotImplemented
    def __gt__(self, other): return _ticks(self) > _ticks(other) if isinstance(other, datetime) else NotImplemented
    def __ge__(self, other): return _ticks(self) >= _ticks(other) if isinstance(other, datetime) else NotImplemented
    def __eq__(self, other): return _ticks(self) == _ticks(other) if isinstance(other, datetime) else NotImplemented
    def __ne__(self, other): return _ticks(self) != _ticks(other) if isinstance(other, datetime) else NotImplemented
    def __hash__(self): return hash(_ticks(self))


def instant(text):
    """An RFC 3339 date-time as an aware datetime (an Instant, to 100 ns), or None when the text is not one."""
    if not isinstance(text, str) or not INSTANT.fullmatch(text):
        return None
    head, _, rest = text.partition(".")
    seventh = 0
    if rest:
        # The fraction is the leading run of digits; what follows is the offset, kept as written (review 2: joining every
        # digit took the offset's too, and a fraction with a numeric offset crashed where .NET reads it).
        digits = re.match(r"[0-9]*", rest).group(0)
        seventh = int(digits[6]) if len(digits) >= 7 else 0
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
        precise = Instant(parsed.year, parsed.month, parsed.day, parsed.hour, parsed.minute, parsed.second, parsed.microsecond, tzinfo=parsed.tzinfo)
        precise.seventh = seventh
        return precise
    except (ValueError, OverflowError, AttributeError, TypeError):
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
            or not isinstance(env.get("signatures"), list) \
            or set(env) != SHAPE["envelope"] or not all(entry_shaped(e) for e in env["signatures"]):
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
    if not isinstance(doc, dict) or set(doc) - SHAPE["revocations"] or doc.get("format") != REVOCATIONS_FORMAT:
        raise Refused("the revocation document is not a v1.2 revocation document")
    issued, expires = instant(doc.get("issuedAt")), instant(doc.get("expires"))
    if issued is None or expires is None or expires <= issued:
        raise Refused("the revocation document needs issuedAt and expires, expiring after it was issued")
    if issued.timestamp() > now.timestamp() + 300:
        raise Refused("the revocation document claims to be issued in the future")
    digests, revoked = doc.get("digests"), doc.get("keys")
    if not isinstance(digests, list) or not all(isinstance(d, str) and DIGEST.fullmatch(d) for d in digests):
        raise Refused("the revocation document has a malformed digests list")
    if not isinstance(revoked, list) or not all(isinstance(k, dict) and not (set(k) - SHAPE["revokedKey"]) and isinstance(k.get("keyId"), str)
                                                 and KEY_ID.fullmatch(k["keyId"]) and ("compromisedAt" not in k or instant(k["compromisedAt"]) is not None)
                                                 for k in revoked):
        raise Refused("the revocation document has a malformed keys list")
    return {"signers": sorted(signers), "issued": issued, "expires": expires, "expires_text": doc["expires"], "digests": digests, "keys": revoked,
            "identity": digest.hex()}


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

    # At most 64 lists in one run; the same list in any envelope is one list, its signers taken together (review 1, L8, as .NET).
    if len(documents) > 64:
        raise Refused("the revocation documents of this run are more than 64")
    by_identity = {}
    for data in documents:
        doc = read_revocations(policy, data, now)
        if doc["identity"] in by_identity:
            known = by_identity[doc["identity"]]
            known["signers"] = sorted(set(known["signers"]) | set(doc["signers"]))
        else:
            by_identity[doc["identity"]] = doc
    read = list(by_identity.values())
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
    for field in REGISTRY["manifest"]["stringFields"]:
        if field in manifest and not isinstance(manifest[field], str):
            raise Refused(f"pack.json has a '{field}' that is not a string")
    for field in ("requires", "display"):
        if field in manifest and not isinstance(manifest[field], dict):
            raise Refused(f"pack.json has a '{field}' that is not an object")
    if isinstance(manifest.get("display"), dict) and "icon" in manifest["display"] and not isinstance(manifest["display"]["icon"], str):
        raise Refused("pack.json has a display.icon that is not a string")
    # What a pack shows a person is display-safe (feature 04), as .NET's PackManifest.
    if isinstance(manifest.get("display"), dict):
        for name, maximum, line in (("title", 60, True), ("description", 300, False)):
            if name in manifest["display"]:
                text = manifest["display"][name]
                if not is_display_text(text, maximum, line):
                    raise Refused(f"pack.json has a display.{name} that is not display-safe text of 1 to {maximum} characters")
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
        if not isinstance(source, dict) or not all(k in SHAPE["manifestSource"] and isinstance(v, str) and 0 < len(v) <= 200
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
    # A composite or a set declares no restriction of its own (review 7, F11, as .NET): it would load as if absent.
    if kind in ("composite", "set"):
        for field in ("sdk", "requires", "containableBy"):
            if field in manifest:
                raise Refused(f"a {kind} pack declares no {field}: its members carry their own")
    if kind == "composite":
        if LOCK not in content or not manifest.get("contains"):
            raise Refused("a composite names what it contains and pins it in META/lock.json")
        if records:  # review 1, L6 (as .NET)
            raise Refused("a composite pack holds no records of its own")
        composite_checks(manifest, content)
    if kind == "set" and not any(p.startswith("packs/") for p in content):
        raise Refused("a pack set carries its members under packs/")
    if manifest.get("protection", REGISTRY["manifest"]["defaults"]["protection"]) != "open" and "META/seal.json" not in content:
        raise Refused("a protected pack carries META/seal.json")


def profiles_used(manifest, files):
    """The profiles a pack uses, in the registry's order, derived from what it holds (as .NET's PackProfiles.Used)."""
    used = []
    for name, p in REGISTRY["profiles"].items():
        if (any(path.startswith(f) if f.endswith("/") else path == f for f in p.get("files", []) for path in files)
                or any(field in manifest for field in p.get("fields", []))
                or manifest.get("kind") in p.get("kinds", [])
                or any(isinstance(manifest.get("extensions"), dict) and e in manifest["extensions"] for e in p.get("extensions", []))
                or any(manifest.get(field, REGISTRY["manifest"]["defaults"].get(field)) in values
                       for field, values in p.get("values", {}).items())):
            used.append(name)
    return used


def composite_checks(manifest, content):
    """The lock is canonical and strict; members.json, pack.json and the lock name the same members."""
    lock_bytes = content[LOCK]
    lock = profile(lock_bytes, integers_only=True)
    if canonical(lock).encode("utf-8") != lock_bytes:
        raise Refused("META/lock.json is not canonical")
    if not isinstance(lock, dict) or set(lock) != SHAPE["familyFile"] or lock["format"] != LOCK_FORMAT or not isinstance(lock["members"], list):
        raise Refused("META/lock.json is not a lock: format and members, nothing else")
    pinned = []
    for m in lock["members"]:
        if not isinstance(m, dict) or set(m) != SHAPE["familyMember"] or not valid_id(m["id"]) \
                or not isinstance(m["version"], str) or not valid_version(m["version"]) \
                or not isinstance(m["digest"], str) or not m["digest"].startswith("sha256:") or len(m["digest"]) != 71:
            raise Refused("META/lock.json has a malformed member")
        pinned.append(m["id"])
    if len(set(pinned)) != len(pinned):
        raise Refused("META/lock.json pins one id twice")
    if "members.json" not in content:
        raise Refused("a composite pack carries members.json")
    try:
        members_doc = profile(content["members.json"])
        if not isinstance(members_doc, dict) or set(members_doc) != {"members"}:  # review 1, L6: nothing but its members
            raise Refused("members.json is malformed")
        # Each member holds what a composite member is and nothing else, as the loader reads it (review 2, L6).
        if not isinstance(members_doc["members"], list) or not all(isinstance(m, dict) and set(m) <= MEMBER_FIELDS and member_typed(m)
                                                                  for m in members_doc["members"]):
            raise Refused("members.json is malformed")
        named = [m["id"] for m in members_doc["members"]]
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
    # Review 1, L5 (as .NET): no member shares the set's id, and no member is itself a set.
    if manifest.get("id") in members:
        raise Refused("the set carries a member with its own id")
    federation = read_federation(manifest.get("extensions", {}))
    for member, member_files in sorted(members.items()):
        try:
            inner_id, _, _, inner_manifest = verify(member_files, policy, now)
        except Refused as e:
            raise Refused(f"packs/{member}/: {e}")
        if inner_id != member:
            raise Refused(f"the member in packs/{member}/ is {inner_id}")
        if inner_manifest.get("kind") == "set":
            raise Refused(f"packs/{member}/ is a pack set: a set carries packs, not sets")
        if federation is not None:
            own = read_federation(inner_manifest.get("extensions", {}))
            if own is None:
                raise Refused(f"packs/{member}/: a member of a federated set is not federated itself")
            if own["publisher"] != federation["publisher"]:
                raise Refused(f"packs/{member}/: the member names another publisher than its set")
            if federation["audience"] is not None and (own["audience"] is None or any(a not in federation["audience"] for a in own["audience"])):
                raise Refused(f"packs/{member}/: the member is published for a wider audience than its set")
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
    # Review 1, L1 (as .NET's PackPaths.Problems): folders that differ only by case, and a file named like a folder.
    folders = {}
    for p in sorted(files):
        parts = p.split("/")
        for i in range(1, len(parts)):
            folder = "/".join(parts[:i])
            if folders.setdefault(folder.lower(), folder) != folder:
                raise Refused(f"folders differ only by case: {folders[folder.lower()]}, {folder}")
    for p in files:
        if p.lower() in folders:
            raise Refused(f"'{p}' is both a file and a folder")
    if FILES not in files:
        raise Refused("there is no META/files.json")
    list_bytes = files[FILES]
    # Review 5, H1: authenticate first. Before any signature holds, nothing is parsed but this list and the envelopes, each bounded.
    if len(list_bytes) > MAX_FILES_LIST:
        raise Refused(f"META/files.json is larger than {MAX_FILES_LIST >> 20} MiB")
    listing = profile(list_bytes, integers_only=True)
    if not isinstance(listing, dict):
        raise Refused("META/files.json is not a v1.1 files list")
    if canonical(listing).encode("utf-8") != list_bytes:
        raise Refused("META/files.json is not canonical")
    if set(listing) != set(REGISTRY["documents"]["files"]["fields"]) or listing["format"] != FILES_FORMAT:
        raise Refused("META/files.json is not a v1.1 files list")
    pid, version = listing["id"], listing["version"]
    if not isinstance(pid, str) or not isinstance(version, str) or not ID.fullmatch(pid) or len(pid) > 64 or not valid_version(version):
        raise Refused("META/files.json names no valid id or version")
    listed = {}
    if not isinstance(listing["files"], list):
        raise Refused("META/files.json has no files array")
    for entry in listing["files"]:
        if not isinstance(entry, dict) or set(entry) != SHAPE["filesEntry"] or not isinstance(entry["path"], str) \
                or isinstance(entry["length"], bool) or not isinstance(entry["length"], int) or entry["length"] < 0 \
                or not isinstance(entry["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", entry["sha256"]):
            raise Refused("META/files.json has a malformed entry")
        listed[entry["path"]] = entry
    paths = [e["path"] for e in listing["files"]]
    if paths != sorted(paths) or len(set(paths)) != len(paths):
        raise Refused("META/files.json is not sorted or repeats a path")
    content = {p: b for p, b in files.items() if p != FILES and not p.startswith(SIGNATURES) and not p.startswith(ATTESTATIONS)}
    if set(content) != set(listed):
        raise Refused("the pack does not hold exactly the listed files")
    attestation_checks(files)
    for p, e in listed.items():
        if len(content[p]) != e["length"] or hashlib.sha256(content[p]).hexdigest() != e["sha256"]:
            raise Refused(f"'{p}' does not match META/files.json")
    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    revoked_keys = {r["keyId"] for r in policy.get("revoked", {}).get("keys", [])}
    roles, publisher_keys, reasons = set(), set(), []
    envelopes = sorted(p for p in files if p.startswith(SIGNATURES) and not p.endswith(TIMESTAMP_SUFFIX))
    if len(envelopes) > MAX_ENVELOPES:
        raise Refused(f"the pack carries {len(envelopes)} signature envelopes; at most {MAX_ENVELOPES}")
    # Timestamp tokens (§2.1, reserved for format 1.3): one beside each envelope at most, as small as an envelope, not judged here.
    for token in sorted(p for p in files if p.startswith(SIGNATURES) and p.endswith(TIMESTAMP_SUFFIX)):
        if token[:-len(TIMESTAMP_SUFFIX)] not in envelopes:
            raise Refused(f"{token} is a timestamp token beside no envelope")
        if len(files[token]) > MAX_ENVELOPE:
            raise Refused(f"{token} is larger than {MAX_ENVELOPE // 1024} KiB")
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
        # Review 1, L7 (as .NET): an envelope with no entries, or whose statement is not a pack role's for this list, refuses.
        if not env["signatures"]:
            raise Refused(f"{path} holds no signature")
        # Review 2, L7 (as .NET): closed shapes. The envelope holds its three fields; each entry a key id (64 lowercase hex) and
        # a signature (strict base64, 1 to 72 bytes), and nothing else.
        if set(env) != SHAPE["envelope"]:
            raise Refused(f"{path} holds a field a DSSE envelope does not have")
        if not all(entry_shaped(s) for s in env["signatures"]):
            raise Refused(f"{path} has a signature entry that is not a key id and a signature")
        try:
            payload = base64.b64decode(env["payload"], validate=True)
        except ValueError:
            raise Refused(f"{path} has a payload that is not base64")
        try:
            st = profile(payload)
            pred = st["predicate"]
            role = pred["role"]
            # Closed, as .NET reads them (review 2, L7).
            if set(st) != SHAPE["statement"] or set(pred) != SHAPE["signaturePredicate"] \
                    or not isinstance(st["subject"], list) or not st["subject"] or not isinstance(st["subject"][0], dict) \
                    or set(st["subject"][0]) != SHAPE["subject"] or not isinstance(st["subject"][0]["digest"], dict) \
                    or set(st["subject"][0]["digest"]) != {"sha256"}:
                raise Refused(f"{path} does not sign this pack")
            ok = (st["_type"] == STATEMENT and len(st["subject"]) == 1 and st["subject"][0]["name"] == FILES
                  and st["subject"][0]["digest"]["sha256"] == list_hex
                  and role in ROLES and st["predicateType"] == f"urn:agentmemory:pack:{role}:v1"
                  and pred["id"] == pid and pred["version"] == version)
            if not isinstance(pred["signedAt"], str) or not SIGNED_AT.fullmatch(pred["signedAt"]):
                raise Refused(f"{path} does not sign this pack")
            signed_at = datetime.strptime(pred["signedAt"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
        except (Refused, KeyError, TypeError, ValueError):
            raise Refused(f"{path} does not sign this pack")
        if not ok:
            raise Refused(f"{path} does not sign this pack")
        # Hashed once per envelope (fifth review); at most MAX_SIGNATURES entries are tried.
        digest_of_pae = hashlib.sha256(pae(payload)).digest()
        for s in env.get("signatures", []):
            if not isinstance(s, dict) or not isinstance(s.get("keyid"), str):
                continue
            key = keys.get(s.get("keyid"))
            if key is None:
                continue
            public = serialization.load_pem_public_key(key["publicKey"].encode())
            spki = public.public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
            if hashlib.sha256(spki).hexdigest() != key["keyId"]:
                continue
            try:
                public.verify(base64.b64decode(s.get("sig") if isinstance(s.get("sig"), str) else "!", validate=True),
                              digest_of_pae, ec.ECDSA(utils.Prehashed(hashes.SHA256())))
            except (InvalidSignature, ValueError, binascii.Error):
                continue
            # The one rule for whose signature counts, the same as for receipts and certificates (review 6, L6: the pack path
            # kept its own copy of the key window, which no vector reached), and why not, as .NET says it.
            refusal = key_refusal(key, role, pid, signed_at, now, revoked_keys)
            if refusal is not None:
                reasons.append(f"{path}: {refusal}")
                continue
            roles.add(role)
            if role == "publisher":
                publisher_keys.add(key["keyId"])
    missing = set(policy.get("requireRoles", ["publisher"])) - roles
    if missing:
        raise Refused("no valid signature in role " + ", ".join(sorted(missing)) + "".join(f"; {r}" for r in reasons))
    # Once the signatures hold: the JSON profile (a JSON file at most 1 MiB), pack.json, the kind, the card, the lifetime.
    for p, b in sorted(content.items()):
        if p.endswith(".json"):
            if len(b) > MAX_JSON:
                raise Refused(f"'{p}' is larger than {MAX_JSON >> 20} MiB")
            profile(b, integers_only=(p == "META/lock.json"))
    manifest = profile(content.get("pack.json", b"null"))
    if not isinstance(manifest, dict):
        raise Refused("pack.json is not an object")
    # The pack's minor decides how strict this verifier is (§2.1), as in .NET: an unknown field refuses at its own minor.
    minor = format_minor(manifest.get("format"))
    unknown = sorted(set(manifest) - PACK_FIELDS)
    if unknown and not (minor is not None and minor > PACK_MINOR):
        raise Refused(f"pack.json has an unknown field '{unknown[0]}'")
    critical_fields(manifest)
    if minor is None:
        raise Refused(f"pack.json has a format this verifier does not read; it knows {PACK_FORMAT} and reads the later 1.x minors")
    if manifest.get("id") != pid or manifest.get("version") != version:
        raise Refused("pack.json does not name the same pack as META/files.json")
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
    if CARD in content:
        read_card(content[CARD])
    digest = "sha256:" + hashlib.sha256(list_bytes).hexdigest()
    if digest in policy.get("revoked", {}).get("digests", []):
        raise Refused("the pack is revoked")
    expires = policy_time(policy.get("revoked", {}).get("expires"))
    if expires is not None and expires < now:
        raise Refused("the host's revocation list has expired")

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
    # Records are parsed only once the signatures hold, as .NET parses them (review 1, L3; review 4: Python parsed every line
    # of an unsigned pack first), each line found in place rather than split into copies.
    for p, b in content.items():
        if p.endswith(".jsonl") and b:
            if not b.endswith(b"\n"):
                raise Refused(f"'{p}' does not end with a line feed")
            start = 0
            while start < len(b):
                stop = b.index(b"\n", start)
                if stop == start:
                    raise Refused(f"'{p}' has an empty line")
                if stop - start > MAX_JSON:
                    raise Refused(f"'{p}' has a line longer than {MAX_JSON >> 20} MiB")
                profile(b[start:stop])
                start = stop + 1
    # The Embeddings profile once every record line has passed the JSON profile, as .NET (review F03, H2: before them, a
    # hostile line crashed this verifier instead of refusing).
    read_embeddings(content)
    return pid, version, digest, manifest


def format_minor(value):
    """The minor of a pack format name, or None when this verifier does not read it: 1.2 up to 1.999, no leading zeros (§2.1)."""
    match = PACK_FORMAT_RE.fullmatch(value) if isinstance(value, str) else None
    return int(match.group(1)) if match and int(match.group(1)) >= PACK_MINOR else None


def critical_fields(manifest):
    """pack.json's "critical" (§2.1): 1 to 64 distinct names of fields it holds; one this verifier does not know refuses."""
    if "critical" not in manifest:
        return
    names = manifest["critical"]
    if not isinstance(names, list) or not 1 <= len(names) <= MAX_CRITICAL or not all(isinstance(n, str) for n in names):
        raise Refused(f"pack.json has a malformed 'critical': a list of 1 to {MAX_CRITICAL} field names")
    if len(set(names)) != len(names):
        raise Refused("pack.json lists a critical field twice")
    for name in names:
        if name == "critical" or name not in manifest:
            raise Refused(f"pack.json marks '{name}' critical, but holds no such field")
        if name not in PACK_FIELDS:
            raise Refused(f"pack.json marks the field '{name}' critical, and this verifier of {PACK_FORMAT} does not understand it")


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
    if set(f) - SHAPE["federation"]:
        raise Refused("the 'federation' extension has an unknown field")
    if f.get("format") != FEDERATION_FORMAT:
        raise Refused(f"the 'federation' extension must name format '{FEDERATION_FORMAT}'")
    if not valid_webid(f.get("publisher")):
        raise Refused("the federation publisher is not a WebID")
    if "audience" not in f:
        raise Refused("the 'federation' extension names no audience")
    audience = read_audience(f["audience"], "the federation audience")
    if "purpose" in f and not is_text(f["purpose"], 300):
        raise Refused("the federation purpose is display-safe text of 1 to 300 characters")
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
    if not_after is not None and signed_at > not_after:
        return "the key had expired at the signing time"
    # As .NET: the signing time is the signer's claim and can be back-dated, so expiry is checked against now too (review 6, L6:
    # one message for both, so no vector could tell the rules apart).
    if not_after is not None and now > not_after:
        return "the key has expired; without a verified timestamp token its signatures no longer count"
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
    if not isinstance(env, dict) or set(env) != SHAPE["envelope"] or env["payloadType"] != PAYLOAD_TYPE \
            or not isinstance(env["payload"], str) or not isinstance(env["signatures"], list) or not 1 <= len(env["signatures"]) <= MAX_SIGNATURES \
            or not all(entry_shaped(e) for e in env["signatures"]):
        raise Refused("the receipt is not a DSSE envelope of an in-toto statement with 1 to 16 signatures")
    try:
        payload = base64.b64decode(env["payload"], validate=True)
    except (ValueError, binascii.Error):
        raise Refused("the receipt's payload is not base64")
    st = profile(payload)
    if not isinstance(st, dict) or set(st) != SHAPE["statement"] or st["_type"] != STATEMENT \
            or st["predicateType"] != PUBLICATION:
        raise Refused("the receipt is not a publication statement")
    subject = st["subject"]
    if not isinstance(subject, list) or len(subject) != 1 or not isinstance(subject[0], dict) or set(subject[0]) != SHAPE["subject"] \
            or subject[0]["name"] != FILES or not isinstance(subject[0]["digest"], dict) or set(subject[0]["digest"]) != {"sha256"} \
            or not isinstance(subject[0]["digest"]["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", subject[0]["digest"]["sha256"]):
        raise Refused("the receipt must name exactly one subject: META/files.json and its SHA-256")
    pred = st["predicate"]
    fields = SHAPE["receiptPredicate"]
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
    if not isinstance(checked, dict) or set(checked) != SHAPE["receiptCheck"]:
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
    denied, allowed = checked["denied"], checked["allowed"]
    if placed_for is not None:
        if "anonymous" not in denied:
            raise Refused("a receipt for named readers records that anonymous was refused")
        if any(a not in placed_for for a in allowed):
            raise Refused("a receipt records a reader outside its audience as allowed")
        if any(d in placed_for for d in denied):
            raise Refused("a receipt records a reader in its audience as refused")
    elif denied:
        raise Refused("a public receipt records nobody as refused")
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


def text_length(text):
    """Characters, that is code points (feature 02): Python's own string length."""
    return len(text)


# Display-safe text (feature 04): the registry's refused code points, fixed at Unicode 15.0, never this platform's categories.
DISPLAY_REFUSED = sorted((int(a, 16), int(b, 16)) for a, b in REGISTRY["text"]["displayRefused"])
DISPLAY_STARTS = [a for a, _ in DISPLAY_REFUSED]


def display_safe(text):
    for c in text:
        i = bisect.bisect_right(DISPLAY_STARTS, ord(c)) - 1
        if i >= 0 and ord(c) <= DISPLAY_REFUSED[i][1]:
            return False
    return True


# Unicode's White_Space property (stable since 6.3), listed as .NET's PackText lists it.
WHITE_SPACE = frozenset([*range(0x09, 0x0E), 0x20, 0x85, 0xA0, 0x1680, *range(0x2000, 0x200B), 0x2028, 0x2029, 0x202F, 0x205F, 0x3000])
LINE_BREAKS = frozenset(["\n", "\t", "\u2028", "\u2029"])


def is_display_text(text, maximum, line):
    """Display text (as .NET's PackText.IsDisplayText): 1 to `maximum` characters, display-safe, not blank, and for one line no
    line feed, tab, line or paragraph separator."""
    return isinstance(text, str) and 1 <= text_length(text) <= maximum and display_safe(text) \
        and any(ord(c) not in WHITE_SPACE for c in text) and not (line and any(c in LINE_BREAKS for c in text))


def is_text(text, maximum):
    return is_display_text(text, maximum, False)


def is_line(text, maximum):
    return is_display_text(text, maximum, True)


# The Embeddings profile (feature 03): embeddings/<name>.st, one tensor in the safetensors layout, and embeddings/<name>.json,
# its index, each read strictly and checked against the other and the pack's records, as .NET's PackEmbeddings.
EMBEDDINGS_FOLDER = "embeddings/"
EMBEDDINGS_FORMAT = "ampack.embeddings/1"
EMBEDDINGS_SET = re.compile(r"\Aembeddings/([a-z0-9][a-z0-9-]{0,63})\.(?:st|json)\Z")
EMBEDDINGS_MAX_HEADER = 64 * 1024
EMBEDDINGS_MAX_DIMENSION = 4096
EMBEDDINGS_MAX_SETS = 8
EMBEDDINGS_MAX_ROWS = 5000
EMBEDDINGS_DTYPES = set(REGISTRY["embeddings"]["dataTypes"])
EMBEDDINGS_DISTANCES = set(REGISTRY["embeddings"]["distances"])


def embedding_records(content):
    """Every (type, key) the record lines name, read leniently: a malformed line is the record reader's to refuse."""
    records = set()
    for path in sorted(p for p in content if p.startswith("records/") and p.endswith(".jsonl")):
        for line in content[path].decode("utf-8", "replace").split("\n"):
            if not line:
                continue
            try:
                value = json.loads(line)
            except ValueError:
                continue
            if isinstance(value, dict) and isinstance(value.get("type"), str) and isinstance(value.get("key"), str):
                records.add((value["type"], value["key"]))
    return records


def read_embeddings(content):
    paths = sorted(p for p in content if p.startswith(EMBEDDINGS_FOLDER))
    if not paths:
        return
    names = set()
    for path in paths:
        m = EMBEDDINGS_SET.match(path)
        if not m:
            raise Refused(f"'{shown(path)}' is not an embeddings file")
        names.add(m.group(1))
    if len(names) > EMBEDDINGS_MAX_SETS:
        raise Refused(f"a pack carries at most {EMBEDDINGS_MAX_SETS} embedding sets")
    records = embedding_records(content)
    for name in sorted(names):
        st, ix = content.get(f"{EMBEDDINGS_FOLDER}{name}.st"), content.get(f"{EMBEDDINGS_FOLDER}{name}.json")
        if st is None or ix is None:
            raise Refused(f"the embedding set '{name}' needs both its .st and its .json")
        index = profile(ix)
        if not isinstance(index, dict) or set(index) - SHAPE["embeddingsIndex"]:
            raise Refused("an embeddings index holds other fields than its own")
        if index.get("format") != EMBEDDINGS_FORMAT:
            raise Refused(f"an embeddings index names format '{EMBEDDINGS_FORMAT}'")
        if not is_line(index.get("model"), 200) or ("modelDigest" in index and not is_line(index["modelDigest"], 200)):
            raise Refused("an embeddings index names its model, and its digest when it gives one, as one line of 1 to 200 characters")
        if not isinstance(index.get("dtype"), str) or index["dtype"] not in EMBEDDINGS_DTYPES:
            raise Refused("an embeddings index names a known dtype")
        dimension = as_int(index.get("dimension"))
        if dimension is None or not 1 <= dimension <= EMBEDDINGS_MAX_DIMENSION:
            raise Refused(f"an embeddings index names a dimension of 1 to {EMBEDDINGS_MAX_DIMENSION}")
        if not isinstance(index.get("normalized"), bool):
            raise Refused("an embeddings index says whether its vectors are normalized")
        if not isinstance(index.get("distance"), str) or index["distance"] not in EMBEDDINGS_DISTANCES:
            raise Refused("an embeddings index names a known distance")
        rows = index.get("rows")
        if not isinstance(rows, list) or not 1 <= len(rows) <= EMBEDDINGS_MAX_ROWS:
            raise Refused(f"an embeddings index lists 1 to {EMBEDDINGS_MAX_ROWS} rows")
        seen = set()
        for row in rows:
            if not isinstance(row, list) or len(row) != 2 or not all(isinstance(x, str) for x in row):
                raise Refused("each row of an embeddings index is [type, key], two strings")
            pair = (row[0], row[1])
            if pair in seen:
                raise Refused("an embeddings index lists a row twice")
            if pair not in records:
                raise Refused("an embeddings index names a row the pack holds no record of")
            seen.add(pair)
        # The safetensors layout, strictly.
        if len(st) < 8:
            raise Refused("an embeddings tensor is shorter than its header length")
        length = int.from_bytes(st[:8], "little")
        if length == 0 or length > EMBEDDINGS_MAX_HEADER or len(st) < 8 + length:
            raise Refused("an embeddings tensor states a header past its bound or its end")
        body = st[8:8 + length].rstrip(b" ")
        if body[:1] != b"{" or body[-1:] != b"}":
            raise Refused("an embeddings tensor's header is a JSON object from its first byte, padded with spaces only")
        header = profile(body)
        if not isinstance(header, dict) or set(header) != {"vectors"}:
            raise Refused("an embeddings tensor's header holds exactly one tensor, 'vectors', and no metadata")
        tensor = header["vectors"]
        if not isinstance(tensor, dict) or set(tensor) - {"dtype", "shape", "data_offsets"}:
            raise Refused("an embeddings tensor is exactly dtype, shape and data_offsets")
        if not isinstance(tensor.get("dtype"), str) or tensor["dtype"] != index["dtype"]:
            raise Refused("an embeddings tensor is not of its index's dtype")
        size = 4 if index["dtype"] == "F32" else 2
        expected = len(rows) * dimension * size
        shape, offsets = tensor.get("shape"), tensor.get("data_offsets")
        if not isinstance(shape, list) or [as_int(x) for x in shape] != [len(rows), dimension]:
            raise Refused("an embeddings tensor's shape is not its rows and dimension")
        if not isinstance(offsets, list) or [as_int(x) for x in offsets] != [0, expected]:
            raise Refused("an embeddings tensor's data offsets are not [0, its bytes]")
        data = st[8 + length:]
        if len(data) != expected:
            raise Refused("an embeddings tensor holds other than its tensor's bytes")
        mask, top = (0x7F800000, "<I") if size == 4 else (0x7C00, "<H")
        for (value,) in struct.iter_unpack(top, data):
            if value & mask == mask:
                raise Refused("an embeddings tensor holds a NaN or infinite value")


def read_card(data):
    """The pack card (v1.2 §15), strictly: exactly its fields, each of its type and length."""
    card = profile(data)
    fields = SHAPE["card"]
    if not isinstance(card, dict) or set(card) - fields:
        raise Refused("card.json holds other fields than a pack card")
    if card.get("format") != CARD_FORMAT:
        raise Refused("card.json must name format 'agentmemory.pack.card/1'")
    owner = card.get("owner")
    if not isinstance(owner, dict) or set(owner) - SHAPE["cardOwner"] or not is_text(owner.get("name"), 200) \
            or ("contact" in owner and not is_text(owner["contact"], 200)):
        raise Refused("card.json names its owner as 'name' and, optionally, 'contact'")
    for name, maximum, required in (("intendedUse", 2000, True), ("limitations", 2000, True), ("provenance", 2000, False),
                                    ("audience", 500, False), ("domain", 100, False)):
        if (required or name in card) and not is_text(card.get(name), maximum):
            raise Refused(f"card.json: '{name}' is text of 1 to {maximum} characters")
    for name, maximum in (("boundaries", 32), ("risks", 32), ("references", 16)):
        items = card.get(name, [])
        if not isinstance(items, list) or len(items) > maximum or not all(is_text(i, 500) for i in items) or len(set(items)) != len(items):
            raise Refused(f"card.json: '{name}' is a list of at most {maximum} distinct texts")
    return card


def attestation_checks(files):
    """What travels in META/attestations/: certificates and the AEF runs beside them, shaped and bounded (v1.2 §16)."""
    stems, runs = set(), {}
    for path in sorted(p for p in files if p.startswith(ATTESTATIONS)):
        match = CERT_FILE.fullmatch(path)
        if match:
            stems.add(match.group(1))
            if len(files[path]) > MAX_ENVELOPE:
                raise Refused(f"'{path}' is larger than 64 KiB")
            continue
        run = RUN_FILE.fullmatch(path)
        if not run:
            raise Refused(f"'{path}' is not an attestation")
        runs.setdefault(run.group(1), []).append(len(files[path]))
    if len(stems) > MAX_CERTIFICATES:
        raise Refused(f"the pack carries {len(stems)} certificates; at most {MAX_CERTIFICATES}")
    for stem, sizes in runs.items():
        if stem not in stems:
            raise Refused(f"the AEF run {stem}.aef/ travels without its certificate")
        if len(sizes) > MAX_RUN_FILES or sum(sizes) > MAX_RUN_BYTES:
            raise Refused(f"the AEF run {stem}.aef/ is too large")


def run_hash(run):
    """An AEF run's run hash (AEF 1.0 SEAL-1 to SEAL-4)."""
    lines = "".join(f"{hashlib.sha256(run[p]).hexdigest()}  {len(run[p])}  {p}\n" for p in sorted(run, key=lambda x: x.encode("utf-8"))
                    if p not in tuple(SHAPE["aefRunUnhashed"]) and not p.startswith("overlays/"))
    return hashlib.sha256(lines.encode("utf-8")).hexdigest()


def run_id(run):
    """The run id run.json names, read in the format's JSON profile (as .NET's PackAttestations.RunId), or None."""
    try:
        value = profile(run["run.json"]) if "run.json" in run and len(run["run.json"]) <= 1 << 20 else None
    except Refused:
        return None
    return value.get("runId") if isinstance(value, dict) and isinstance(value.get("runId"), str) else None


def run_is_of(run, pid, digest, version):
    """Whether run.json's subject names this pack at this digest and version (as .NET's PackAttestations.RunIsOf)."""
    try:
        value = profile(run["run.json"]) if "run.json" in run and len(run["run.json"]) <= 1 << 20 else None
    except Refused:
        return False
    subject = value.get("subject") if isinstance(value, dict) else None
    return (isinstance(subject, dict) and subject.get("ref") == f"pack:{pid}@{digest}" and isinstance(subject.get("version"), str)
            and subject.get("version") == version)


def one_of(value, names):
    return isinstance(value, str) and value in names


def as_int(value):
    return value if isinstance(value, int) and not isinstance(value, bool) else None


CERTIFICATE_MAX_TASKS = 200      # a trial runs 10 to 200 tasks
CERTIFICATE_MAX_OFFERED = 16     # the format's traversal limit
CERTIFICATE_MAX_SPACING = 2000   # every how many eligible commits a task was drawn, from the last 2,000


def read_arm(arm):
    """An arm with its evidence (review 5, root RB): counts, each task's [offered, correct] within the arm's cap, summing to the
    counts, with a right item on exactly its hits."""
    fields = SHAPE["certificateArm"] - {"perTask"}
    if not isinstance(arm, dict) or set(arm) != fields | {"perTask"} or any(as_int(arm[k]) is None for k in fields):
        return None
    t, h, o, c, p95, cap = arm["tasks"], arm["hits"], arm["offered"], arm["correct"], arm["p95Micros"], arm["maximumOffered"]
    # A cap of 0 offers nothing by design (the baseline none); the spacing says how far apart the tasks were drawn (review 6).
    if not (0 <= t <= CERTIFICATE_MAX_TASKS and 0 <= cap <= CERTIFICATE_MAX_OFFERED and 1 <= arm["spacing"] <= CERTIFICATE_MAX_SPACING
            and 0 <= p95 <= 86_400_000_000):
        return None
    per = arm["perTask"]
    if not isinstance(per, list) or len(per) != t:
        return None
    for entry in per:
        if not isinstance(entry, list) or len(entry) != 2 or any(as_int(x) is None for x in entry) or not 0 <= entry[1] <= entry[0] <= cap:
            return None
    if sum(e[0] for e in per) != o or sum(e[1] for e in per) != c or sum(1 for e in per if e[1] > 0) != h:
        return None
    return arm


def read_certificate(payload):
    """An evaluation certificate's statement (v1.2 §16), strictly, as .NET's PackCertificate.Read."""
    st = profile(payload)
    if not isinstance(st, dict) or set(st) != SHAPE["statement"] or st["_type"] != STATEMENT \
            or st["predicateType"] != CERTIFICATE:
        raise Refused("the certificate is not an evaluation statement")
    subject = st["subject"]
    if not isinstance(subject, list) or len(subject) != 1 or not isinstance(subject[0], dict) or set(subject[0]) != SHAPE["subject"] \
            or subject[0]["name"] != FILES or not isinstance(subject[0]["digest"], dict) or set(subject[0]["digest"]) != {"sha256"} \
            or not isinstance(subject[0]["digest"]["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", subject[0]["digest"]["sha256"]):
        raise Refused("the certificate must name exactly one subject: META/files.json and its SHA-256")
    pred = st["predicate"]
    fields = SHAPE["certificatePredicate"]
    if not isinstance(pred, dict) or set(pred) - fields:
        raise Refused("the certificate's predicate holds other fields than this format defines")
    if not (isinstance(pred.get("id"), str) and ID.fullmatch(pred["id"]) and len(pred["id"]) <= 64 and valid_version(pred.get("version"))):
        raise Refused("the certificate names no valid pack id and version")
    if not is_line(pred.get("evaluator"), 200):
        raise Refused("the certificate names its evaluator as one line of 1 to 200 characters")
    tool = pred.get("tool")
    if not isinstance(tool, dict) or set(tool) != SHAPE["certificateTool"] or not is_line(tool["name"], 100) or not is_line(tool["version"], 64):
        raise Refused("the certificate names its tool and version")
    if not isinstance(pred.get("evaluatedAt"), str) or not SIGNED_AT.fullmatch(pred["evaluatedAt"]):
        raise Refused("the certificate has no evaluation time in UTC")
    try:
        evaluated_at = datetime.strptime(pred["evaluatedAt"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError:
        raise Refused("the certificate has no evaluation time in UTC")
    trial = pred.get("trial")
    if not isinstance(trial, dict) or set(trial) - SHAPE["certificateTrial"] \
            or not one_of(trial.get("measure"), MEASURES) or not one_of(trial.get("baseline"), BASELINES) or not is_line(trial.get("taskSource"), 500):
        raise Refused("the certificate's trial names its measure, baseline and task source")
    if not is_line(pred.get("statistic"), 100):
        raise Refused("the certificate names the statistic its checks were computed with")
    if "confidencePermille" in trial and (as_int(trial["confidencePermille"]) is None or trial["confidencePermille"] not in CONFIDENCES):
        raise Refused("the certificate's confidencePermille is 900, 950, 975 or 990")
    checks = pred.get("checks")
    if not isinstance(checks, list) or not 1 <= len(checks) <= 16:
        raise Refused("the certificate lists 1 to 16 checks")
    for c in checks:
        if not isinstance(c, dict) or set(c) - SHAPE["certificateCheck"] \
                or not one_of(c.get("target"), TARGETS) or as_int(c.get("requiredMicro")) is None or as_int(c.get("observedMicro")) is None \
                or not isinstance(c.get("met"), bool) or ("estimateMicro" in c and as_int(c["estimateMicro"]) is None):
            raise Refused("a certificate check names a known target, requiredMicro and observedMicro as integers, and met")
    if len({c["target"] for c in checks}) != len(checks):
        raise Refused("the certificate checks one target twice")
    with_arm = read_arm(pred.get("with"))
    if with_arm is None:
        raise Refused("the certificate's 'with' arm is malformed")
    without = None
    if "without" in pred and (without := read_arm(pred["without"])) is None:
        raise Refused("the certificate's 'without' arm is malformed")
    if not isinstance(pred.get("passed"), bool):
        raise Refused("the certificate says whether every target was met ('passed')")
    if "aef" in pred:
        aef = pred["aef"]
        if not isinstance(aef, dict) or set(aef) != SHAPE["certificateAef"] or not isinstance(aef["runId"], str) or not RUN_ID.fullmatch(aef["runId"]) \
                or not isinstance(aef["runHash"], str) or not re.fullmatch(r"[0-9a-f]{64}", aef["runHash"]):
            raise Refused("the certificate's 'aef' names an AEF runId and its runHash")
    # The verdict agrees with the record: passed exactly when every check held on enough tasks.
    if pred["passed"] != (all(c["met"] for c in checks) and with_arm["tasks"] >= MINIMUM_TASKS):
        raise Refused("the certificate's verdict disagrees with its checks")
    # Review 7, G4 and G5, as .NET: one spacing for arms that ran the same tasks, within the history a trial draws from; a
    # baseline of none offers nothing, a host baseline can offer.
    if without is not None and without["spacing"] != with_arm["spacing"]:
        raise Refused("the certificate's arms were drawn at different spacings")
    if with_arm["tasks"] > 1 and with_arm["spacing"] * (with_arm["tasks"] - 1) >= HISTORY_WINDOW:
        raise Refused("the certificate's tasks span more than the history a trial draws from")
    if trial["baseline"] == "none" and without is not None and (without["maximumOffered"] != 0 or without["offered"] != 0):
        raise Refused("a certificate measured against none records a baseline arm that offers nothing")
    if trial["baseline"] == "host" and without is not None and without["maximumOffered"] == 0:
        raise Refused("a certificate measured against the host's retrieval records that arm with a cap of 1 or more")
    if without is not None and without["tasks"] != with_arm["tasks"]:
        raise Refused("the certificate's arms ran different numbers of tasks")
    if trial["baseline"] == "host" and without is None:
        raise Refused("a certificate measured against the host's retrieval records that arm")
    return st, pred, evaluated_at


def stem_for(key_id, evaluated_at):
    """A certificate's stem, as .NET's PackAttestations.StemFor: the key's first 16 hex digits and the evaluation time (UTC)."""
    t = evaluated_at.astimezone(timezone.utc)
    return f"{key_id[:16]}-{t.year:04d}{t.month:02d}{t.day:02d}t{t.hour:02d}{t.minute:02d}{t.second:02d}z"


def verify_certificate(envelope_bytes, pid, version, digest, policy, now, run=None, stem=None):
    """An evaluation certificate (v1.2 §16) for a verified pack: by a key the host trusts as evaluator for its id; one the
    pack carries travels under its evaluator's key and evaluation time (review 6, M3)."""
    if len(envelope_bytes) > MAX_ENVELOPE:
        raise Refused("the certificate is larger than 64 KiB")
    env = profile(envelope_bytes)
    if not isinstance(env, dict) or set(env) != SHAPE["envelope"] or env["payloadType"] != PAYLOAD_TYPE \
            or not isinstance(env["payload"], str) or not isinstance(env["signatures"], list) or not 1 <= len(env["signatures"]) <= MAX_SIGNATURES \
            or not all(entry_shaped(e) for e in env["signatures"]):
        raise Refused("the certificate is not a DSSE envelope of an in-toto statement with 1 to 16 signatures")
    try:
        payload = base64.b64decode(env["payload"], validate=True)
    except (ValueError, binascii.Error):
        raise Refused("the certificate's payload is not base64")
    st, pred, evaluated_at = read_certificate(payload)
    if "sha256:" + st["subject"][0]["digest"]["sha256"] != digest:
        raise Refused("the certificate is for another pack")
    if pred["id"] != pid or pred["version"] != version:
        raise Refused("the certificate names another pack id or version")
    if run is not None:
        if "aef" not in pred:
            raise Refused("an AEF run travels with the certificate, but the certificate names none")
        if run_hash(run) != pred["aef"]["runHash"]:
            raise Refused("the AEF run beside the certificate is not the run it names")
        named = run_id(run)
        if named is not None and named != pred["aef"]["runId"]:
            raise Refused("the AEF run beside the certificate has another run id")
        if not run_is_of(run, pid, digest, version):
            raise Refused("the AEF run beside the certificate is the evidence of another decision")
    keys = {k["keyId"]: k for k in policy.get("keys", [])}
    revoked_keys = {r["keyId"] for r in policy.get("revoked", {}).get("keys", [])}
    digest_of_pae = hashlib.sha256(pae(payload)).digest()
    signers = []
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
        if key_refusal(key, "evaluator", pid, evaluated_at, now, revoked_keys) is None:
            signers.append(entry["keyid"])
    if not signers:
        raise Refused("no valid certificate signature by a key this host trusts as evaluator for the pack")
    if stem is not None and not any(stem_for(k, evaluated_at) == stem for k in signers):
        raise Refused(f"the certificate travels as {stem}, not as its evaluator's key and evaluation time")
    return pred


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


def closed(value, fields, what):
    """An object holding only the fields named (as .NET's TrustPolicy.Closed)."""
    if not isinstance(value, dict):
        raise Refused(f"{what} is not an object")
    unknown = sorted(set(value) - fields)
    if unknown:
        raise Refused(f"{what} holds a field the trust policy does not define: '{unknown[0]}'")


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
    # Review 1, H3: every level is closed, as .NET reads it; a misspelt field is refused, never ignored.
    closed(policy, SHAPE["trustPolicy"], "the trust policy")
    if policy.get("format") != "agentmemory.pack.trust/1.1":
        raise Refused("the trust policy must name format 'agentmemory.pack.trust/1.1'")
    seen = set()
    for key in policy.get("keys", []) if isinstance(policy.get("keys", []), list) else [None]:
        if not isinstance(key, dict) or not isinstance(key.get("keyId"), str) or not isinstance(key.get("publicKey"), str):
            raise Refused("the trust policy has a malformed key")
        closed(key, SHAPE["trustKey"], "a trusted key")
        prefixes, roles = key.get("idPrefixes", []), key.get("roles", [])
        if not isinstance(prefixes, list) or not prefixes or not all(isinstance(p, str) for p in prefixes):
            raise Refused("a trusted key names no pack id prefixes")
        if not all(len(p) <= 64 and PREFIX.fullmatch(p) for p in prefixes):
            raise Refused("a trusted key names something that is not a pack id prefix")
        if not isinstance(roles, list) or not roles or not all(isinstance(r, str) and r in KEY_ROLES for r in roles):
            raise Refused("a trusted key needs known roles")
        if "revoker" in roles and len(roles) > 1:
            raise Refused("a revoker key holds that role alone")
        if "evaluator" in roles and len(roles) > 1:
            raise Refused("an evaluator key holds that role alone")
        # One PUBLIC KEY block and nothing else, as .NET reads it (review 2: a private key, or several blocks, read differently).
        if not isinstance(key["publicKey"], str) or not PUBLIC_PEM.fullmatch(key["publicKey"]):
            raise Refused("a trusted key's publicKey is not one PEM 'PUBLIC KEY' block and nothing else")
        if not canonical_p256(key["publicKey"]):
            raise Refused("a trusted key's publicKey is not a P-256 key in its one form (an uncompressed point, canonical base64)")
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
    closed(revoked, SHAPE["trustRevoked"], "'revoked'")
    if not isinstance(revoked.get("digests", []), list) or not all(isinstance(d, str) for d in revoked.get("digests", [])):
        raise Refused("the trust policy has a malformed revoked digest list")
    if not all(DIGEST.fullmatch(d) for d in revoked.get("digests", [])):
        raise Refused("a revoked digest is not sha256: and 64 lowercase hexadecimal digits")
    for r in revoked.get("keys", []) if isinstance(revoked.get("keys", []), list) else [None]:
        if not isinstance(r, dict) or not isinstance(r.get("keyId"), str):
            raise Refused("the trust policy has a malformed revoked key")
        closed(r, SHAPE["revokedKey"], "a revoked key")
        if not KEY_ID.fullmatch(r["keyId"]):
            raise Refused("a revoked key id is not 64 lowercase hexadecimal digits")
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


def shown(value):
    """A value from a pack as it is printed: every control or format character (Unicode Cc and Cf: escapes, bidi controls,
    zero-width characters, line breaks) as \\uXXXX, so pack text cannot forge or hide a line (round 10, A-M1)."""
    return "".join(f"\\u{ord(c):04X}" if unicodedata.category(c) in ("Cc", "Cf") else c for c in str(value))


def main(argv):
    # A console that cannot encode a character shows it escaped, never fails (round 10, A-M1: a crash read as a refusal).
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="backslashreplace")
    # NFC and the Stream-Safe count are Unicode's: the verdicts are Unicode 15.0's, as .NET's tables are, on every host.
    if unicodedata.unidata_version != UNICODE_VERSION:
        print(f"error: this verifier decides with Unicode {UNICODE_VERSION} data; this Python has {unicodedata.unidata_version} (use Python 3.12)")
        return 2
    args = list(argv[1:])
    usage = "usage: python ampack_verify.py <pack.ampack> <trust.json> [<revocations.json>...] [--receipt <file> --location <url>] [--certificate <path>]"

    def option(name):
        if name not in args:
            return None
        i = args.index(name)
        if i + 1 >= len(args):
            raise IndexError(name)
        value = args[i + 1]
        del args[i:i + 2]
        return value
    # A usage error is exit 2, never 1, which reads as a refusal (review 1, L11).
    try:
        receipt_path, location, certificate_path = option("--receipt"), option("--location"), option("--certificate")
    except IndexError as missing:
        print(f"error: {missing} needs a value\n{usage}")
        return 2
    if (receipt_path is None) != (location is None):
        print("error: --receipt and --location go together")
        return 2
    if len(args) < 2 or any(a.startswith("--") for a in args):
        print(usage)
        return 2
    pack_path, policy_path = args[0], args[1]
    now = datetime.now(timezone.utc)
    try:
        policy = load_policy(read_file(policy_path, "the trust policy", MAX_POLICY))
        documents = []
        for name in args[2:]:
            documents.append(read_regular(name, MAX_REVOCATIONS, "the revocation document"))
        if documents:
            policy = apply_all(policy, documents, now)
        archive = read_regular(pack_path, MAX_ARCHIVE, "the archive")
        # Unpacked once, for the pack, its receipt and its certificate (review 4: up to three times, two held at once).
        files = read_zip(archive)
        pid, version, digest = verify_any(files, policy, now)
        if receipt_path is not None:
            _, _, _, manifest = verify(files, policy, now)
            federation = read_federation(manifest.get("extensions", {}))
            verify_receipt(read_file(receipt_path, "the receipt"), pid, version, digest, federation, archive, location, policy, now)
            print(f"verified the placement of {shown(pid)} {shown(version)} at {shown(location)}")
        if certificate_path is not None:
            if certificate_path.startswith(ATTESTATIONS):
                named = CERT_FILE.fullmatch(certificate_path)
                if named is None or certificate_path not in files:
                    raise Refused(f"the pack carries no {certificate_path}")
                stem = named.group(1)
                folder = f"{ATTESTATIONS}{stem}.aef/"
                run = {p[len(folder):]: b for p, b in files.items() if p.startswith(folder)} or None
                envelope = files[certificate_path]
            else:
                run, envelope, stem = None, read_file(certificate_path, "the certificate"), None
            pred = verify_certificate(envelope, pid, version, digest, policy, now, run, stem)
            print(f"verified the certificate of {shown(pid)} {shown(version)} by {shown(pred['evaluator'])}")
    except Refused as e:
        print(f"refused: {shown(e)}")
        return 1
    except Exception as e:  # a defect, never a verdict: the conformance run tells it apart from a refusal
        print(f"error: {type(e).__name__}: {shown(e)}")
        return 2
    print(f"verified {shown(pid)} {shown(version)} {digest}")
    used = profiles_used(json.loads(files["pack.json"]), [p for p in files if not p.startswith("META/signatures/") and not p.startswith(ATTESTATIONS)])
    missing = [n for n in used if not REGISTRY["profiles"][n]["implemented"]]
    print(f"profiles {', '.join(used) if used else 'core'}" + (f" (not implemented by this reader: {', '.join(missing)})" if missing else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
