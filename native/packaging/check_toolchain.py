"""Refuses a Native AOT build whose compiler is older than the security floor (SEC-NATIVE-BROTLI, NATIVE-PLAT-M1).

Usage: python check_toolchain.py <project.assets.json> [--floor 10.0.14]

Reads the restore graph a publish wrote (tools/AgentMemory.Native/obj/project.assets.json) and checks every
Microsoft.DotNet.ILCompiler package in it, the compiler and its runtime pack, against the floor. A Native AOT library
carries the runtime it was compiled with, so a fix in that runtime reaches users only through a rebuild. The floor is the
first version with the fix for the newest advisory in a compiled-in component: 10.0.14 for CVE-2026-111497 (the Brotli
decoder). Raise it when an advisory names a newer fixed version. Standard library only.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

FLOOR = "10.0.14"


def numbers(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in version.split("-")[0].split("+")[0].split("."))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("assets", type=Path)
    parser.add_argument("--floor", default=FLOOR)
    args = parser.parse_args()
    libraries = json.loads(args.assets.read_text(encoding="utf-8"))["libraries"]
    compilers = sorted(tuple(key.split("/", 1)) for key, library in libraries.items()
                       if library.get("type") == "package" and key.split("/", 1)[0].endswith("Microsoft.DotNet.ILCompiler"))
    if not compilers:
        sys.exit(f"{args.assets}: no Microsoft.DotNet.ILCompiler package; is this a Native AOT publish?")
    for package, version in compilers:
        print(f"{package} {version}")
    below = [f"{package} {version}" for package, version in compilers if numbers(version) < numbers(args.floor)]
    if below:
        sys.exit(f"compiler below the security floor {args.floor}: {', '.join(below)}. Update the .NET SDK, then rebuild.")
    print(f"toolchain floor {args.floor}: ok")


if __name__ == "__main__":
    main()
