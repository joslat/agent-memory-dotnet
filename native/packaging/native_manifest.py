"""Writes the build manifest of one platform's native library (NATIVE-PLAT-M1): what was built, from what, how big, its SHA-256.

Usage: python native_manifest.py --rid <rid> --bin <publish dir> --assets <project.assets.json> --out <MANIFEST.json>
                                 [--max-glibc 2.34] [--commit <sha>]

The library is agentmemory_native.so, .dylib or .dll by the runtime identifier. On Linux, the newest GLIBC_ symbol
version the library asks for is recorded, and with --max-glibc a newer one is refused: the wheel's platform tag promises
that baseline. On macOS the oldest macOS the library runs on (its LC_BUILD_VERSION) is recorded, for the wheel's tag.
The manifest is read back and its SHA-256 compared with the file before it counts. Standard library only.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

NAMES = {"win": "agentmemory_native.dll", "osx": "agentmemory_native.dylib", "linux": "agentmemory_native.so"}
HEADER = Path(__file__).resolve().parents[1] / "include" / "agentmemory_native.h"


def library_name(rid: str) -> str:
    family = rid.split("-")[0]
    if family not in NAMES:
        sys.exit(f"unknown runtime identifier {rid}")
    return NAMES[family]


def native_runtime(assets: Path) -> str | None:
    libraries = json.loads(assets.read_text(encoding="utf-8"))["libraries"]
    versions = sorted({key.split("/", 1)[1] for key in libraries if key.split("/", 1)[0].endswith("Microsoft.DotNet.ILCompiler")})
    return versions[-1] if versions else None


def abi() -> dict[str, int]:
    text = HEADER.read_text(encoding="utf-8")
    major = int(re.search(r"#define AM_V1_ABI_MAJOR (\d+)u", text).group(1))
    minor = int(re.search(r"#define AM_V1_ABI_MINOR (\d+)u", text).group(1))
    return {"major": major, "minor": minor}


def newest_glibc(data: bytes) -> tuple[int, ...] | None:
    found = [tuple(int(n) for n in match.groups() if n is not None) for match in re.finditer(rb"GLIBC_(\d+)\.(\d+)(?:\.(\d+))?", data)]
    return max(found) if found else None


LC_VERSION_MIN_MACOSX, LC_BUILD_VERSION = 0x24, 0x32


def macos_minimum(data: bytes) -> str | None:
    """The oldest macOS a thin 64-bit Mach-O library runs on, as "major.minor" (LC_BUILD_VERSION's minos)."""
    if data[:4] != b"\xcf\xfa\xed\xfe":  # MH_MAGIC_64, little-endian
        return None

    def word(at: int) -> int:
        return int.from_bytes(data[at:at + 4], "little")

    offset = 32  # past the mach_header_64
    for _ in range(word(16)):  # ncmds
        command, size = word(offset), word(offset + 4)
        if command in (LC_BUILD_VERSION, LC_VERSION_MIN_MACOSX):
            version = word(offset + 12) if command == LC_BUILD_VERSION else word(offset + 8)
            return f"{version >> 16}.{(version >> 8) & 0xFF}"
        offset += size
    return None


def sdk_version() -> str:
    return subprocess.run(["dotnet", "--version"], capture_output=True, text=True, check=True).stdout.strip()


def commit(explicit: str | None) -> str:
    if explicit:
        return explicit
    if os.environ.get("GITHUB_SHA"):
        return os.environ["GITHUB_SHA"]
    return subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--rid", required=True)
    parser.add_argument("--bin", required=True, type=Path)
    parser.add_argument("--assets", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--max-glibc")
    parser.add_argument("--commit")
    args = parser.parse_args()

    library = args.bin / library_name(args.rid)
    if not library.is_file():
        sys.exit(f"no {library.name} in {args.bin}")
    data = library.read_bytes()
    manifest = {
        "format": "agentmemory-native.build/1",
        "rid": args.rid,
        "abi": abi(),
        "library": {"name": library.name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()},
        "build": {"commit": commit(args.commit), "sdk": sdk_version(), "nativeRuntime": native_runtime(args.assets)},
    }
    if args.rid.startswith("linux"):
        glibc = newest_glibc(data)
        manifest["glibcNewest"] = ".".join(map(str, glibc)) if glibc else None
        if args.max_glibc and glibc and glibc > tuple(int(n) for n in args.max_glibc.split(".")):
            sys.exit(f"{library.name} asks for GLIBC_{manifest['glibcNewest']}, newer than the promised baseline {args.max_glibc}")
    if args.rid.startswith("osx"):
        manifest["macosMinimum"] = macos_minimum(data)
        if manifest["macosMinimum"] is None:
            sys.exit(f"{library.name} is not a thin 64-bit Mach-O library with a minimum macOS version")
    args.out.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    written = json.loads(args.out.read_text(encoding="utf-8"))
    if written["library"]["sha256"] != hashlib.sha256(library.read_bytes()).hexdigest():
        sys.exit("the manifest's SHA-256 does not match the library")
    print(json.dumps(written, indent=2))


if __name__ == "__main__":
    main()
