# SPDX-License-Identifier: Apache-2.0
"""The command line: `ampack verify <pack.ampack> <trust.json> [<revocations.json>...]`, also `python -m ampack ...`.

It is the conformance kit's Python verifier with its own options (--receipt with --location, --certificate, --erasure,
--aggregate). Exit code 0 verified, 1 refused, 2 usage or a defect.
"""
import sys

from . import __version__, _verify

USAGE = """usage: ampack verify <pack.ampack> <trust.json> [<revocations.json>...]
              [--receipt <file> --location <url>] [--certificate <path>] [--erasure <path>] [--aggregate <file>]
       ampack --version

This client verifies and reads memory packs. It does not build, sign or certify them."""


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)
    if args[:1] == ["--version"]:
        print(f"ampack {__version__} (Python verifier, format {_verify.PACK_FORMAT})")
        return 0
    if args[:1] != ["verify"]:
        print(USAGE)
        return 0 if args[:1] in (["--help"], ["-h"]) else 2
    return _verify.main(["ampack verify", *args[1:]])


if __name__ == "__main__":
    sys.exit(main())
