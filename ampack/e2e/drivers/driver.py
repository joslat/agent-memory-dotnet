# SPDX-License-Identifier: Apache-2.0
"""The end-to-end driver for the Python package: loads one pack as a user of the package does and prints what it read.

usage: python driver.py <pack> <trust.json> [--revocations <file>]... [--at <RFC 3339>]

Prints one line of JSON: {"verified": true, "pack": {...}} or {"verified": false, "reason": "..."}. Exit code 0 in both
cases; anything else is a defect of the package or of this driver.
"""
import hashlib
import json
import sys
from datetime import datetime
from pathlib import Path

import ampack


def shape(pack):
    return {
        "id": pack.id,
        "version": pack.version,
        "digest": pack.digest,
        "kind": pack.kind,
        "title": pack.title,
        "files": {path: hashlib.sha256(data).hexdigest() for path, data in pack.files.items()},
        "records": [
            {"type": r.type, "key": r.key, "properties": r.properties,
             "links": [[link["relation"], link["target"]] for link in r.links],
             "extra": r.extra, "file": r.file, "line": r.line}
            for r in pack.records()
        ],
        "members": [shape(member) for member in pack.members],
    }


def main(argv):
    positional, revocations, at = [], [], None
    i = 1
    while i < len(argv):
        if argv[i] == "--revocations":
            revocations.append(Path(argv[i + 1]).read_bytes())
            i += 2
        elif argv[i] == "--at":
            at = datetime.fromisoformat(argv[i + 1].replace("Z", "+00:00"))
            i += 2
        else:
            positional.append(argv[i])
            i += 1
    if len(positional) != 2:
        print(__doc__)
        return 2
    try:
        pack = ampack.load(Path(positional[0]).read_bytes(), Path(positional[1]).read_bytes(), revocations, at=at)
        answer = {"verified": True, "pack": shape(pack)}
    except ampack.Refused as refused:
        answer = {"verified": False, "reason": str(refused)}
    print(json.dumps(answer, default=dict))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
