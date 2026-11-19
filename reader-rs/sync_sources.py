# SPDX-License-Identifier: Apache-2.0
"""Copies into the two crates that are published what a crate on crates.io must carry itself.

A published crate cannot reach outside its own folder. So before `cargo package` or `cargo publish`, this copies the
format registry (spec/registry.json) into ampack-core, and the licence files into both crates. The copies are build
products: never edited, not committed. In a checkout the build reads spec/registry.json itself (build.rs), so a stale
copy is never used there.

usage: python sync_sources.py [--check]
       --check copies nothing and exits 1 when a copy is missing or differs
"""
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parent
CORE, COMMAND = HERE / "crates" / "ampack-core", HERE / "crates" / "ampack-cli"

COPIES = {
    CORE / "registry.json": REPOSITORY / "spec" / "registry.json",
    CORE / "LICENSE": HERE / "LICENSE",
    CORE / "NOTICE": HERE / "NOTICE",
    COMMAND / "LICENSE": HERE / "LICENSE",
    COMMAND / "NOTICE": HERE / "NOTICE",
}


def main(argv):
    check = "--check" in argv[1:]
    stale = []
    for copy, source in COPIES.items():
        if not source.is_file():
            print(f"error: {source} is missing; run this from a checkout of the repository")
            return 2
        if copy.is_file() and copy.read_bytes() == source.read_bytes():
            continue
        stale.append(copy)
        if not check:
            shutil.copyfile(source, copy)
            print(f"copied {source.relative_to(REPOSITORY)} -> {copy.relative_to(REPOSITORY)}")
    if check and stale:
        for copy in stale:
            print(f"stale: {copy.relative_to(REPOSITORY)}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
