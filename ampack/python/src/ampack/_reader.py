# SPDX-License-Identifier: Apache-2.0
"""Load a memory pack: verify it against a trust policy, then read what it holds.

Verification is the conformance kit's Python verifier, unchanged (ampack._verify). Nothing of a pack is handed out before
it verified. This module adds reading: the manifest, the card, the ontology, the record lines and, for a pack set, its
members.
"""
from __future__ import annotations

import os
import unicodedata
from dataclasses import dataclass
from datetime import datetime, timezone
from types import MappingProxyType
from typing import Any, Iterator, Mapping, Sequence, Union



def _import_verifier():
    """The kit's verifier, as it is. It reads the format registry when it is imported: from the file AMPACK_REGISTRY names,
    else from a repository's spec folder. This package carries its own copy beside this file, so the variable names it for
    the time of the import, unless the caller already set one, and is then put back as it was."""
    name = "AMPACK_REGISTRY"
    given = os.environ.get(name)
    if given is None:
        os.environ[name] = os.path.join(os.path.dirname(os.path.abspath(__file__)), "registry.json")
    try:
        from . import _verify
    finally:
        if given is None:
            os.environ.pop(name, None)
    return _verify


_v = _import_verifier()

Refused = _v.Refused
Source = Union[str, "os.PathLike[str]", bytes, bytearray, memoryview]

_RECORD_FIELDS = frozenset(("type", "key", "properties", "links")) | frozenset(_v.REGISTRY["records"]["lifeFields"]) \
    | frozenset(_v.REGISTRY["records"]["trustFields"])
# A knowledge pack as a host imports it (specification, R-CORE-102).
_MAX_RECORDS, _MAX_RECORD_FILES, _MAX_LINE_BYTES = 5000, 16, 64 * 1024


class UnsupportedPython(RuntimeError):
    """This Python cannot give the format's verdicts: its Unicode data is not the version the format decides with."""


def _check_unicode() -> None:
    if unicodedata.unidata_version != _v.UNICODE_VERSION:
        raise UnsupportedPython(
            f"ampack decides with Unicode {_v.UNICODE_VERSION} data and this Python has {unicodedata.unidata_version}; use Python 3.12")


def _bytes(source: Source, cap: int, what: str) -> bytes:
    if isinstance(source, (bytes, bytearray, memoryview)):
        data = bytes(source)
        if len(data) > cap:
            raise Refused(f"{what} is larger than {cap} bytes")
        return data
    return _v.read_regular(os.fspath(source), cap, what)


def _policy(trust: Source, revocations: Sequence[Source], now: datetime) -> Any:
    policy = _v.load_policy(_bytes(trust, _v.MAX_POLICY, "the trust policy"))
    documents = [_bytes(r, _v.MAX_REVOCATIONS, "the revocation document") for r in revocations]
    return _v.apply_all(policy, documents, now) if documents else policy


def _now(at: datetime | None) -> datetime:
    if at is None:
        return datetime.now(timezone.utc)
    if at.tzinfo is None:
        raise ValueError("'at' needs a time zone, for example datetime.now(timezone.utc)")
    return at.astimezone(timezone.utc)


@dataclass(frozen=True)
class Verdict:
    """What verified: the pack's id, its version and the digest of its content."""
    id: str
    version: str
    digest: str


@dataclass(frozen=True)
class Record:
    """One record line of a pack, as it is written."""
    type: str
    key: str
    properties: Mapping[str, Any]
    #: Each link is {"relation": ..., "target": ...}; the target is another record's key.
    links: Sequence[Mapping[str, str]]
    #: The other fields the format allows on a line (validFrom, expiresAt, confidenceMicro, ...), not checked here.
    extra: Mapping[str, Any]
    #: Where the line stands, for a message: the file inside the pack and the line number.
    file: str
    line: int


class Pack:
    """A verified pack. Create one with ampack.load()."""

    def __init__(self, files: Mapping[str, bytes], pid: str, version: str, digest: str, manifest: Mapping[str, Any],
                 members: Sequence["Pack"] = ()) -> None:
        self._files = MappingProxyType(dict(files))
        self.id, self.version, self.digest = pid, version, digest
        self.manifest: Mapping[str, Any] = manifest
        self.members: Sequence[Pack] = tuple(members)

    def __repr__(self) -> str:
        return f"<ampack.Pack {self.id} {self.version} {self.kind}>"

    @property
    def kind(self) -> str:
        """knowledge, definition, snapshot, composite or set."""
        return self.manifest["kind"]

    @property
    def title(self) -> str | None:
        display = self.manifest.get("display")
        return display.get("title") if isinstance(display, dict) else None

    @property
    def files(self) -> Mapping[str, bytes]:
        """Every file of the pack by its path, as verified. Read-only."""
        return self._files

    def json(self, path: str) -> Any:
        """A JSON file of the pack, parsed under the format's JSON profile; KeyError when the pack has no such file."""
        return _v.profile(self._files[path])

    @property
    def card(self) -> Mapping[str, Any] | None:
        """The pack card (owner, intended use, limitations), or None when the pack carries none."""
        return _v.read_card(self._files[_v.CARD]) if _v.CARD in self._files else None

    @property
    def ontology(self) -> Mapping[str, Any] | None:
        """The types and relations the pack declares, or None when it declares none."""
        return self.json("ontology.json") if "ontology.json" in self._files else None

    @property
    def record_files(self) -> Sequence[str]:
        return tuple(sorted(p for p in self._files if p.startswith("records/") and p.endswith(".jsonl")))

    def records(self) -> Iterator[Record]:
        """The pack's own record lines, file by file in name order. A set or a composite has none: see members.

        Each line is checked for its shape only: an object with a string type and key, properties as an object, links as
        {"relation", "target"} pairs, and no field the format does not define. Whether a record fits the ontology (its
        type, its required properties, its relations) is for the host that imports it to check; this reader does not.
        """
        paths = self.record_files
        knowledge = self.kind == "knowledge"
        if knowledge and len(paths) > _MAX_RECORD_FILES:
            raise Refused(f"a knowledge pack holds at most {_MAX_RECORD_FILES} record files; this one has {len(paths)}")
        count = 0
        for path in paths:
            for number, raw in enumerate(self._files[path].split(b"\n")[:-1], start=1):
                at = f"{path}:{number}"
                if knowledge and len(raw) > _MAX_LINE_BYTES:
                    raise Refused(f"{at}: longer than {_MAX_LINE_BYTES} bytes")
                yield _record(_v.profile(raw), path, number, at)
                count += 1
                if knowledge and count > _MAX_RECORDS:
                    raise Refused(f"a knowledge pack holds at most {_MAX_RECORDS} records")


def _record(value: Any, path: str, number: int, at: str) -> Record:
    if not isinstance(value, dict):
        raise Refused(f"{at}: a record is a JSON object")
    unknown = sorted(set(value) - _RECORD_FIELDS)
    if unknown:
        raise Refused(f"{at}: unknown field {', '.join(unknown)}")
    if not isinstance(value.get("type"), str) or not isinstance(value.get("key"), str):
        raise Refused(f"{at}: a record needs a string type and key")
    properties = value.get("properties", {})
    if not isinstance(properties, dict):
        raise Refused(f"{at}: properties is an object")
    links = value.get("links", [])
    if not isinstance(links, list) or not all(
            isinstance(link, dict) and set(link) == {"relation", "target"}
            and isinstance(link["relation"], str) and isinstance(link["target"], str) for link in links):
        raise Refused(f'{at}: links is a list of {{"relation", "target"}} with string values')
    extra = {k: v for k, v in value.items() if k not in ("type", "key", "properties", "links")}
    return Record(value["type"], value["key"], MappingProxyType(properties), tuple(MappingProxyType(link) for link in links),
                  MappingProxyType(extra), path, number)


def verify(pack: Source, trust: Source, revocations: Sequence[Source] = (), *, at: datetime | None = None) -> Verdict:
    """Verifies a pack (or a pack set and each member) against a trust policy. Raises Refused with the reason.

    pack, trust and each revocation document are a path or the bytes themselves. 'at' is the moment to judge validity
    windows at (default: now).
    """
    _check_unicode()
    now = _now(at)
    policy = _policy(trust, revocations, now)
    files = _v.read_zip(_bytes(pack, _v.MAX_ARCHIVE, "the archive"))
    return Verdict(*_v.verify_any(files, policy, now))


def load(pack: Source, trust: Source, revocations: Sequence[Source] = (), *, at: datetime | None = None) -> Pack:
    """Verifies a pack as verify() does and returns it for reading. Raises Refused with the reason; nothing is returned
    from a pack that did not verify."""
    _check_unicode()
    now = _now(at)
    policy = _policy(trust, revocations, now)
    files = _v.read_zip(_bytes(pack, _v.MAX_ARCHIVE, "the archive"))
    _v.verify_any(files, policy, now)
    pid, version, digest, manifest = _v.verify(files, policy, now)
    members = []
    if manifest.get("kind") == "set":
        for member in sorted(manifest.get("contains", [])):
            prefix = f"packs/{member}/"
            inner = {p[len(prefix):]: b for p, b in files.items() if p.startswith(prefix)}
            members.append(Pack(inner, *_v.verify(inner, policy, now)))
    return Pack(files, pid, version, digest, manifest, members)
