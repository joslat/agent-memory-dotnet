"""Builds the agentmemory-native wheel: the Python package with one published native library inside.

Usage: python build_wheel.py <native-library> <output-dir> [--platform-tag manylinux_2_34_x86_64]

Standard library only, and reproducible: entries are sorted and every timestamp is fixed, so the same inputs give the
same wheel bytes. The platform tag must match the library: on Linux, the oldest glibc whose symbols it uses (check with
objdump -T); the published linux-x64 library needs GLIBC_2.34 at most.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import re
import sys
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
PACKAGE = HERE / "agentmemory_native"
NAME = "agentmemory_native"
DIST = "agentmemory-native"
FIXED_TIME = (1980, 1, 1, 0, 0, 0)
LIBRARY_NAMES = {"linux": "agentmemory_native.so", "win": "agentmemory_native.dll", "macosx": "agentmemory_native.dylib"}


def version() -> str:
    match = re.search(r'^__version__ = "([^"]+)"', (PACKAGE / "__init__.py").read_text(encoding="utf-8"), re.M)
    if not match:
        sys.exit("no __version__ in agentmemory_native/__init__.py")
    return match.group(1)


def record_line(path: str, data: bytes) -> str:
    digest = base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode()
    return f"{path},sha256={digest},{len(data)}"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("library", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--platform-tag", default="manylinux_2_34_x86_64")
    args = parser.parse_args()

    tag = args.platform_tag
    family = next((key for key in LIBRARY_NAMES if tag.startswith(key) or tag.startswith(f"many{key}")), None)
    if family is None:
        sys.exit(f"unknown platform tag {tag}")
    if not args.library.is_file():
        sys.exit(f"native library not found: {args.library}")

    ver = version()
    dist_info = f"{NAME}-{ver}.dist-info"
    files: dict[str, bytes] = {}
    for source in sorted(PACKAGE.rglob("*.py")):
        files[f"{NAME}/{source.relative_to(PACKAGE).as_posix()}"] = source.read_bytes()
    files[f"{NAME}/_native/{LIBRARY_NAMES[family]}"] = args.library.read_bytes()
    files[f"{dist_info}/METADATA"] = (
        "Metadata-Version: 2.1\n"
        f"Name: {DIST}\n"
        f"Version: {ver}\n"
        "Summary: AgentMemory for Python through the AgentMemory native library (no .NET installation needed)\n"
        "License: MIT\n"
        "Requires-Python: >=3.10\n"
        "Provides-Extra: langchain\n"
        "Requires-Dist: langchain-core>=0.3; extra == \"langchain\"\n"
    ).encode()
    files[f"{dist_info}/WHEEL"] = (
        "Wheel-Version: 1.0\nGenerator: agentmemory build_wheel.py\nRoot-Is-Purelib: false\n" f"Tag: py3-none-{tag}\n"
    ).encode()
    files[f"{dist_info}/top_level.txt"] = f"{NAME}\n".encode()

    record = [record_line(path, data) for path, data in sorted(files.items())]
    record.append(f"{dist_info}/RECORD,,")
    files[f"{dist_info}/RECORD"] = ("\n".join(record) + "\n").encode()

    args.output.mkdir(parents=True, exist_ok=True)
    wheel = args.output / f"{NAME}-{ver}-py3-none-{tag}.whl"
    with zipfile.ZipFile(wheel, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(files):
            info = zipfile.ZipInfo(path, date_time=FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o755 if path.endswith(LIBRARY_NAMES[family]) else 0o644) << 16
            archive.writestr(info, files[path])
    print(f"{wheel.name}  {wheel.stat().st_size:,} bytes  sha256 {hashlib.sha256(wheel.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
