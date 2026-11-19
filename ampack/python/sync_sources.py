# SPDX-License-Identifier: Apache-2.0
"""Copies into this package the files it ships but does not own, from their one real place in the repository.

The verifier is conformance/pack-v1.1/python/ampack_verify.py and the format registry is spec/registry.json. They are
written and reviewed there. The copies made here are build products: never edited, not committed, and checked byte for
byte by tests/test_sync.py. Run this before building or testing the package.

usage: python sync_sources.py [--check]
       --check copies nothing and exits 1 when a copy is missing or differs
"""
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPOSITORY = HERE.parent.parent

COPIES = {
    REPOSITORY / "conformance" / "pack-v1.1" / "python" / "ampack_verify.py": HERE / "src" / "ampack" / "_verify.py",
    REPOSITORY / "spec" / "registry.json": HERE / "src" / "ampack" / "registry.json",
    HERE.parent / "LICENSE": HERE / "LICENSE",
    HERE.parent / "NOTICE": HERE / "NOTICE",
}


def main(argv):
    check = "--check" in argv[1:]
    stale = []
    for source, copy in COPIES.items():
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
