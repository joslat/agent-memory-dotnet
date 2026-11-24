"""The C/C++ archives of a native release (NATIVE-RELEASE): one per platform, for the GitHub Release.

Usage: python native_archives.py --platforms <downloaded artifacts> --header native/include/agentmemory_native.h
       --license LICENSE --version 0.3.0 --out archives

Each platform's gated build (native-platforms.yml) uploads `agentmemory-native-<rid>/bin/` with the library and its
MANIFEST.json. Each archive holds `agentmemory-native-<version>-<rid>/` with lib/<library>, include/agentmemory_native.h,
MANIFEST.json and LICENSE: a .tar.gz for Linux and macOS, a .zip for Windows. A library that is missing, or is not the one
its manifest's SHA-256 names, stops the release. The archives are byte for byte the same when built again (fixed times,
sorted entries), and SHA256SUMS lists them. Standard library only.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import sys
import tarfile
import zipfile
from pathlib import Path

# Each platform's library file name and archive format, as native-platforms.yml builds them.
PLATFORMS = {
    "linux-x64": ("agentmemory_native.so", "tar.gz"),
    "linux-arm64": ("agentmemory_native.so", "tar.gz"),
    "osx-arm64": ("agentmemory_native.dylib", "tar.gz"),
    "win-x64": ("agentmemory_native.dll", "zip"),
}

# 1980-01-01, the earliest time a zip can hold: one fixed time for every entry, so a rebuild gives the same bytes.
FIXED_TIME = 315532800


class ArchiveProblem(Exception):
    """A platform's build cannot be packed as it is."""


def build(platforms: Path, header: Path, license: Path, version: str, out: Path) -> list[Path]:
    out.mkdir(parents=True, exist_ok=True)
    archives = []
    for rid, (library_name, kind) in PLATFORMS.items():
        bin_dir = platforms / f"agentmemory-native-{rid}" / "bin"
        library = bin_dir / library_name
        manifest = bin_dir / "MANIFEST.json"
        if not library.is_file() or not manifest.is_file():
            raise ArchiveProblem(f"{rid}: {library_name} or MANIFEST.json is missing from {bin_dir}")
        data = library.read_bytes()
        recorded = json.loads(manifest.read_text(encoding="utf-8")).get("library", {}).get("sha256")
        if recorded != hashlib.sha256(data).hexdigest():
            raise ArchiveProblem(f"{rid}: {library_name} is not the library its MANIFEST.json names (SHA-256 {recorded})")
        stem = f"agentmemory-native-{version}-{rid}"
        entries = sorted([
            (f"{stem}/LICENSE", license.read_bytes(), 0o644),
            (f"{stem}/MANIFEST.json", manifest.read_bytes(), 0o644),
            (f"{stem}/include/{header.name}", header.read_bytes(), 0o644),
            (f"{stem}/lib/{library_name}", data, 0o755),
        ])
        target = out / f"{stem}.{kind}"
        target.write_bytes(_zip(entries) if kind == "zip" else _tar_gz(entries))
        archives.append(target)
    sums = "".join(f"{hashlib.sha256(a.read_bytes()).hexdigest()}  {a.name}\n" for a in sorted(archives))
    (out / "SHA256SUMS").write_text(sums, encoding="utf-8")
    return archives


def _tar_gz(entries: list[tuple[str, bytes, int]]) -> bytes:
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as tar:
        for name, data, mode in entries:
            info = tarfile.TarInfo(name)
            info.size, info.mode, info.mtime = len(data), mode, FIXED_TIME
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            tar.addfile(info, io.BytesIO(data))
    packed = io.BytesIO()
    with gzip.GzipFile(fileobj=packed, mode="wb", mtime=FIXED_TIME, filename="") as gz:
        gz.write(raw.getvalue())
    return packed.getvalue()


def _zip(entries: list[tuple[str, bytes, int]]) -> bytes:
    packed = io.BytesIO()
    with zipfile.ZipFile(packed, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for name, data, mode in entries:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.external_attr = (0o100000 | mode) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
    return packed.getvalue()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--platforms", type=Path, required=True)
    parser.add_argument("--header", type=Path, required=True)
    parser.add_argument("--license", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    try:
        archives = build(args.platforms, args.header, args.license, args.version, args.out)
    except ArchiveProblem as problem:
        print(f"PROBLEM: {problem}", file=sys.stderr)
        return 1
    for archive in archives:
        print(f"  {archive.name}  {archive.stat().st_size} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
