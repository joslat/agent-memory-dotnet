# SPDX-License-Identifier: Apache-2.0
"""ampack: verify and read memory packs for AI agents.

    import ampack

    pack = ampack.load("notes.ampack", trust="trust.json")   # raises ampack.Refused with the reason
    print(pack.id, pack.version, pack.digest)
    for record in pack.records():
        print(record.type, record.key, record.properties)

This package verifies and reads. It does not build, sign or certify a pack.
"""
from ._reader import Pack, Record, Refused, UnsupportedPython, Verdict, load, verify

__version__ = "0.1.0"
__all__ = ["Pack", "Record", "Refused", "UnsupportedPython", "Verdict", "load", "verify", "__version__"]
